using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenTranslator.Display;

/// <summary>Attached properties used by the theme: icon glyphs for buttons and a title bar painted in the window palette.</summary>
public static class ThemeAssist
{
    public const string PlayGlyph = "";
    public const string PauseGlyph = "";
    public const string CheckGlyph = "";

    /// <summary>A Segoe Fluent Icons / MDL2 code point shown before the content; empty hides it.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(ThemeAssist), new FrameworkPropertyMetadata(""));
    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);

    /// <summary>Windows 11 paints the caption and border in the theme colors; older systems ignore the request and keep the default frame.</summary>
    public static readonly DependencyProperty TintCaptionProperty = DependencyProperty.RegisterAttached("TintCaption", typeof(bool), typeof(ThemeAssist), new PropertyMetadata(false, TintCaption_Changed));
    public static bool GetTintCaption(Window window) => (bool)window.GetValue(TintCaptionProperty);
    public static void SetTintCaption(Window window, bool value) => window.SetValue(TintCaptionProperty, value);

    private static void TintCaption_Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Window window || e.NewValue is not true) return;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Tint(window);
        else window.SourceInitialized += (_, _) => Tint(window);
    }
    private static void Tint(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetColor(hwnd, 35, window.TryFindResource("AccentPale"), 0xFDF2F8);
        SetColor(hwnd, 36, window.TryFindResource("Ink"), 0x45263B);
        SetColor(hwnd, 34, window.TryFindResource("CardBorder"), 0xF3C4DC);
        var round = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }
    private static void SetColor(IntPtr hwnd, int attribute, object? resource, int fallbackRgb)
    {
        var color = resource is SolidColorBrush brush ? brush.Color : Color.FromRgb((byte)(fallbackRgb >> 16), (byte)(fallbackRgb >> 8), (byte)fallbackRgb);
        var colorRef = color.R | color.G << 8 | color.B << 16;
        _ = DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
