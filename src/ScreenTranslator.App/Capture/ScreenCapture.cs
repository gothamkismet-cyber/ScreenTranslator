using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SkiaSharp;

namespace ScreenTranslator.Capture;

public static class ScreenCapture
{
    public static SKBitmap Capture(Rectangle region)
    {
        if (region.Width < 8 || region.Height < 8 || region.Width > 8192 || region.Height > 8192 || (long)region.Width * region.Height > 16000000)
            throw new InvalidOperationException("选区大小无效，请重新选择。");
        if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(region)))
            throw new InvalidOperationException("选区已离开当前显示器，请重新选择。");
        using var image = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
            graphics.CopyFromScreen(region.Location, System.Drawing.Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
        using var buffer = new MemoryStream();
        image.Save(buffer, ImageFormat.Png);
        return SKBitmap.Decode(buffer.ToArray()) ?? throw new InvalidOperationException("没有取得可识别的画面。");
    }

    public static bool ExcludeWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, 0x11);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
