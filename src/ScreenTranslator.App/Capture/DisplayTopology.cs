using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenTranslator.Capture;

public static class DisplayTopology
{
    public static string Fingerprint() => string.Join("|", System.Windows.Forms.Screen.AllScreens.Select(screen =>
    {
        var point = new NativePoint { X = screen.Bounds.X + 1, Y = screen.Bounds.Y + 1 };
        var monitor = MonitorFromPoint(point, 2);
        GetDpiForMonitor(monitor, 0, out var x, out var y);
        return $"{screen.DeviceName}:{screen.Bounds}:{x}:{y}";
    }));
    public static bool Overlaps(Window window, Rectangle region)
    {
        if (!window.IsVisible) return false;
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && GetWindowRect(handle, out var bounds) && region.IntersectsWith(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
}
