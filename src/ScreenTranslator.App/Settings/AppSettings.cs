using System.Text.Json;
using ScreenTranslator.Ocr;

namespace ScreenTranslator.Settings;

public enum ApiFormat { ChatCompletions, Responses }
public enum AddressMode { Base, Full }
public enum AuthMode { Bearer, Header, None }
public enum OutputLimitField { None, MaxTokens, MaxCompletionTokens, MaxOutputTokens }

public sealed record ApiProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新连接";
    public ApiFormat Format { get; set; }
    public AddressMode AddressMode { get; set; }
    public string Address { get; set; } = "";
    public AuthMode Authentication { get; set; }
    public string KeyHeader { get; set; } = "x-api-key";
    public string Model { get; set; } = "";
    public bool Stream { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 20;
    public OutputLimitField OutputLimit { get; set; }
    public int MaxOutput { get; set; } = 512;
    public string ExtraBodyJson { get; set; } = "{}";
    public override string ToString() => Name;
}

public sealed class ProfileSecrets
{
    public string ApiKey { get; set; } = "";
    public string AddressQuery { get; set; } = "";
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SecretRevision { get; set; }
    public List<ApiProfile> Profiles { get; set; } = [];
    public Guid? SelectedProfileId { get; set; }
    public SourceLanguage SourceLanguage { get; set; }
    public bool ShowOriginal { get; set; } = true;
    public double FloatingFontSize { get; set; } = 20;
    public double FloatingOpacity { get; set; } = 1;
    public string SelectHotkey { get; set; } = "Ctrl+Alt+T";
    public string PauseHotkey { get; set; } = "Ctrl+Alt+P";
    public string FloatingHotkey { get; set; } = "Ctrl+Alt+H";
    public string RefreshHotkey { get; set; } = "Ctrl+Alt+R";
}

public sealed record SettingsBundle(AppSettings Settings, Dictionary<Guid, ProfileSecrets> Secrets)
{
    public SettingsBundle Copy() => new(
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))!,
        JsonSerializer.Deserialize<Dictionary<Guid, ProfileSecrets>>(JsonSerializer.Serialize(Secrets))!);
}
