using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenTranslator.Settings;

public interface ISecretProtector
{
    byte[] Protect(byte[] bytes);
    byte[] Unprotect(byte[] bytes);
}

public sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public byte[] Unprotect(byte[] bytes) => Transform(bytes, false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows 无法保护或读取密钥。请在当前 Windows 用户下重新填写；软件不会改存明文。");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class SettingsStore
{
    private readonly string _root;
    private readonly ISecretProtector _protector;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record SecretEnvelope(Guid Revision, Dictionary<Guid, ProfileSecrets> Profiles);
    public string Root => _root;
    public SettingsStore(string? root = null, ISecretProtector? protector = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenAiTranslator");
        _protector = protector ?? new WindowsSecretProtector();
    }

    public SettingsBundle Load()
    {
        var path = Path.Combine(_root, "settings.json");
        if (!File.Exists(path)) return new SettingsBundle(new AppSettings(), []);
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("设置文件无效，请备份后重新配置。");
        if (settings.SchemaVersion != 1) throw new InvalidDataException("设置文件版本不受支持，请使用创建它的软件版本。");
        var secretPath = Path.Combine(_root, "credentials.dat");
        if (!File.Exists(secretPath))
        {
            if (settings.Profiles.Count != 0) throw new CryptographicException("受保护的密钥文件缺失，请重新填写连接设置。");
            return new SettingsBundle(settings, []);
        }
        var bytes = _protector.Unprotect(File.ReadAllBytes(secretPath));
        try
        {
            var envelope = JsonSerializer.Deserialize<SecretEnvelope>(bytes) ?? throw new CryptographicException("密钥文件无效。");
            if (envelope.Revision != settings.SecretRevision) throw new CryptographicException("设置与密钥文件版本不一致，请重新填写连接设置。");
            foreach (var profile in settings.Profiles)
                if (envelope.Profiles.TryGetValue(profile.Id, out var secret) && secret.AddressQuery.Length != 0) profile.Address = profile.Address.Split('?')[0] + secret.AddressQuery;
            return new SettingsBundle(settings, envelope.Profiles);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public AppSettings LoadConfigurationsOnly() => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(_root, "settings.json"))) ?? new AppSettings();

    public void Save(SettingsBundle bundle)
    {
        var revision = Guid.NewGuid();
        var copy = bundle.Copy();
        var settings = copy.Settings;
        var filtered = copy.Secrets.Where(pair => settings.Profiles.Any(p => p.Id == pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var profile in settings.Profiles)
        {
            if (!filtered.TryGetValue(profile.Id, out var secret)) filtered[profile.Id] = secret = new ProfileSecrets();
            var query = profile.Address.IndexOf('?');
            secret.AddressQuery = query < 0 ? "" : profile.Address[query..];
            if (query >= 0) profile.Address = profile.Address[..query];
        }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SecretEnvelope(revision, filtered));
        byte[] encrypted;
        try { encrypted = _protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        // Protect before any write. A failed Windows protection operation never becomes a plaintext fallback.
        Directory.CreateDirectory(_root);
        settings.SecretRevision = revision;
        AtomicWrite(Path.Combine(_root, "credentials.dat"), encrypted);
        AtomicWrite(Path.Combine(_root, "settings.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonOptions)));
        bundle.Settings.SecretRevision = revision;
    }

    public static string ExportWithoutSecrets(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        copy.SecretRevision = Guid.Empty;
        foreach (var profile in copy.Profiles) profile.Address = profile.Address.Split('?')[0];
        return JsonSerializer.Serialize(new { schema = "screen-ai-translator/config-export/v1", note = "No API keys or extra request headers included. Re-enter credentials on another Windows account.", settings = copy }, JsonOptions);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }
}
