using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenTranslator.Capture;

public sealed record WindowTarget(IntPtr Handle, uint ProcessId, uint ThreadId, string Title);
public sealed class WindowFrameChangedException : Exception { }

/// <summary>Identifies a live window and follows its visible client area in physical screen pixels.</summary>
public static class WindowTargets
{
    public static IReadOnlyList<WindowTarget> List(IReadOnlySet<IntPtr> excluded)
    {
        var windows = new List<WindowTarget>();
        if (!EnumWindows((handle, _) =>
        {
            if (!excluded.Contains(handle) && IsVisible(handle) && !IsIconic(handle) && Describe(handle) is { } target)
                windows.Add(target);
            return true;
        }, IntPtr.Zero)) throw new ArgumentException("暂时无法读取窗口列表，请刷新后重试。");
        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static WindowTarget? Describe(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return null;
        var title = new StringBuilder(1024);
        GetWindowText(handle, title, title.Capacity);
        var thread = GetWindowThreadProcessId(handle, out var process);
        return title.Length == 0 || thread == 0 ? null : new WindowTarget(handle, process, thread, title.ToString());
    }

    public static Rectangle RequireVisibleArea(WindowTarget target, IReadOnlySet<IntPtr> excluded)
    {
        var thread = GetWindowThreadProcessId(target.Handle, out var process);
        if (!IsWindow(target.Handle) || process != target.ProcessId || thread != target.ThreadId)
            throw new ArgumentException("所选窗口已关闭，请重新选择窗口。");
        if (IsIconic(target.Handle)) throw new ArgumentException("所选窗口已最小化。恢复窗口后，点击开始继续翻译。");
        if (!IsVisible(target.Handle)) throw new ArgumentException("所选窗口当前不可见。显示窗口后，点击开始继续翻译。");
        if (!GetClientRect(target.Handle, out var client)) throw new ArgumentException("无法读取所选窗口，请重新选择。");
        var origin = new NativePoint();
        if (!ClientToScreen(target.Handle, ref origin)) throw new ArgumentException("无法读取窗口位置，请重新选择。");
        var region = new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
        if (region.Width < 8 || region.Height < 8 || region.Width > 8192 || region.Height > 8192 || (long)region.Width * region.Height > 16000000)
            throw new ArgumentException("窗口大小超出识别范围，请调整窗口大小后再开始。");
        if (!System.Windows.Forms.Screen.AllScreens.Any(screen => screen.Bounds.Contains(region)))
            throw new ArgumentException("请将所选窗口完整放在同一块显示器内，再开始翻译。");

        // GetWindow can race with window destruction; bound the walk and reject cycles rather than guessing visibility.
        var visited = new HashSet<IntPtr>();
        var above = GetWindow(target.Handle, 3); // GW_HWNDPREV
        while (above != IntPtr.Zero)
        {
            if (!visited.Add(above) || visited.Count > 2048) throw new ArgumentException("窗口位置正在变化，请稍后点击开始重试。");
            if (!excluded.Contains(above) && IsVisible(above) && !IsIconic(above) && FrameBounds(above) is { } frame && frame.IntersectsWith(region))
                throw new ArgumentException("所选窗口被其他窗口遮住，已暂停。移开遮挡窗口后，点击开始继续。");
            above = GetWindow(above, 3);
        }
        return region;
    }

    public static SKBitmap Capture(WindowTarget target, IReadOnlySet<IntPtr> excluded)
    {
        var region = RequireVisibleArea(target, excluded);
        var image = ScreenCapture.Capture(region);
        try
        {
            // Never submit a frame whose target moved, disappeared, or became covered during the screenshot.
            if (region != RequireVisibleArea(target, excluded))
                throw new WindowFrameChangedException();
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static bool IsVisible(IntPtr handle) => IsWindowVisible(handle) &&
        (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0);
    private static Rectangle? FrameBounds(IntPtr handle)
    {
        if (DwmFrameBounds(handle, 9, out var frame, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(handle, out frame)) return null;
        return Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmFrameBounds(IntPtr handle, uint attribute, out NativeRect value, int size);
}
