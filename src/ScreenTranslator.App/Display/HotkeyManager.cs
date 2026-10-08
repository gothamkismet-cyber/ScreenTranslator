using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenTranslator.Settings;

namespace ScreenTranslator.Display;

public enum HotkeyAction { Select, Pause, Floating, Refresh }
public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly List<int> _registered = [];
    public event Action<HotkeyAction>? Pressed;
    public HotkeyManager(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)!;
        _source.AddHook(Hook);
    }
    /// <summary>Registers the configured hotkeys and returns the ones Windows refused, as typed in settings.</summary>
    public string[] Apply(AppSettings settings)
    {
        foreach (var id in _registered) UnregisterHotKey(_handle, id);
        _registered.Clear();
        var failed = new List<string>();
        foreach (var (action, text) in Bindings(settings))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var (modifiers, key) = Parse(text);
            var id = 4100 + (int)action;
            if (RegisterHotKey(_handle, id, modifiers | 0x4000, key)) _registered.Add(id);
            else failed.Add(text.Trim());
        }
        return failed.ToArray();
    }
    public static void Validate(AppSettings settings)
    {
        var used = new HashSet<(uint, uint)>();
        foreach (var (_, text) in Bindings(settings))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!used.Add(Parse(text))) throw new ArgumentException("多个操作不能使用同一个快捷键；留空可以关闭某个快捷键。");
        }
    }
    private static IEnumerable<(HotkeyAction, string)> Bindings(AppSettings settings) => [(HotkeyAction.Select, settings.SelectHotkey), (HotkeyAction.Pause, settings.PauseHotkey), (HotkeyAction.Floating, settings.FloatingHotkey), (HotkeyAction.Refresh, settings.RefreshHotkey)];
    private static (uint Modifiers, uint Key) Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint modifiers = 0;
        if (parts.Length < 2) throw new ArgumentException("快捷键请使用修饰键加按键，例如 Ctrl+Alt+T；留空可以关闭。");
        foreach (var part in parts[..^1])
            modifiers |= part.ToLowerInvariant() switch { "ctrl" or "control" => 2u, "alt" => 1u, "shift" => 4u, "win" or "windows" => 8u, _ => throw new ArgumentException("快捷键修饰键只支持 Ctrl、Alt、Shift、Win。") };
        if ((modifiers & (1u | 2u | 8u)) == 0) throw new ArgumentException("全局快捷键需要 Ctrl、Alt 或 Win，避免干扰正常打字。");
        Key key;
        try { key = (Key)new KeyConverter().ConvertFromInvariantString(parts[^1])!; }
        catch { throw new ArgumentException("快捷键末尾需要单个按键，例如 T、F8、Space。"); }
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) throw new ArgumentException("快捷键末尾需要普通按键。");
        return (modifiers, (uint)virtualKey);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        var value = wParam.ToInt64();
        if (value < 4100 || value > 4103) return IntPtr.Zero;
        var id = (int)value;
        if (_registered.Contains(id)) { handled = true; Pressed?.Invoke((HotkeyAction)(id - 4100)); }
        return IntPtr.Zero;
    }
    public void Dispose() { foreach (var id in _registered) UnregisterHotKey(_handle, id); _source.RemoveHook(Hook); _registered.Clear(); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
