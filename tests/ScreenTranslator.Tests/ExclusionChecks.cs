using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenTranslator.Capture;
using ScreenTranslator.Display;
using ScreenTranslator.Ocr;
using ScreenTranslator.Settings;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenTranslator.Tests;

internal static class ExclusionChecks
{
    public static int Run(string root)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenTranslator;component/Themes/Theme.xaml") });
        var text = new TextBlock { Text = "Do not delete 250 files.", FontSize = 32, Margin = new Thickness(28), FontFamily = new FontFamily("Arial") };
        var source = new Window { Title = "窗口排除验证", Width = 730, Height = 230, Left = 120, Top = 300, Topmost = true, Background = Brushes.White, Content = text };
        var rows = new List<object>(); var passed = false; Window? overlay = null; FloatingWindow? floating = null; HotkeyManager? first = null; HotkeyManager? second = null;
        source.ContentRendered += async (_, _) =>
        {
            var output = Path.Combine(root, "artifacts", "verification", "exclusion"); Directory.CreateDirectory(output);
            try
            {
                await Task.Delay(250);
                var begin = text.PointToScreen(new System.Windows.Point(0, 0)); var end = text.PointToScreen(new System.Windows.Point(text.ActualWidth, text.ActualHeight));
                var region = Rectangle.FromLTRB((int)Math.Floor(begin.X), (int)Math.Floor(begin.Y), (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
                overlay = new Window { Title = "被排除的覆盖窗口", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, Background = Brushes.Magenta, ShowInTaskbar = false };
                var excluded = false;
                overlay.SourceInitialized += (_, _) => { var hwnd = new WindowInteropHelper(overlay).Handle; SetWindowPos(hwnd, new IntPtr(-1), region.X, region.Y, region.Width, region.Height, 0x0040); excluded = ScreenCapture.ExcludeWindow(overlay); };
                overlay.Show(); await Task.Delay(400);
                using var capture = ScreenCapture.Capture(region);
                using var ocr = new OcrService(Path.Combine(root, "models", "screen-ocr"), Path.Combine(root, ".tools", "test-model-cache"));
                var reading = await ocr.ReadAsync(capture, SourceLanguage.English);
                using (var imageFile = File.Create(Path.Combine(output, "captured-under-overlay.png"))) capture.Encode(imageFile, SkiaSharp.SKEncodedImageFormat.Png, 100);
                var visibleSource = reading.Text == text.Text;
                rows.Add(new { check = "Opaque magenta test window covers source; captured pixels must still contain underlying source", excludedApi = excluded, expected = text.Text, actual = reading.Text, passed = excluded && visibleSource, dpiScale = VisualTreeHelper.GetDpi(source).DpiScaleX });
                overlay.Hide();
                // The shipped floating window is a transparent (layered) window; it must be excluded the same way.
                floating = new FloatingWindow { ShowActivated = false };
                floating.Apply(new AppSettings());
                floating.SourceInitialized += (_, _) => { var hwnd = new WindowInteropHelper(floating).Handle; SetWindowPos(hwnd, new IntPtr(-1), region.X - 40, region.Y - 40, region.Width + 80, region.Height + 80, 0x0040); };
                floating.Show(); await Task.Delay(400);
                using var floatingCapture = ScreenCapture.Capture(region);
                var floatingReading = await ocr.ReadAsync(floatingCapture, SourceLanguage.English);
                using (var imageFile = File.Create(Path.Combine(output, "captured-under-floating.png"))) floatingCapture.Encode(imageFile, SkiaSharp.SKEncodedImageFormat.Png, 100);
                var floatingExcluded = floating.CaptureExcluded && floatingReading.Text == text.Text;
                rows.Add(new { check = "Actual transparent floating window covers source; captured pixels must still contain underlying source", excludedApi = floating.CaptureExcluded, expected = text.Text, actual = floatingReading.Text, passed = floatingExcluded });
                first = new HotkeyManager(source); second = new HotkeyManager(overlay);
                var hook = typeof(HotkeyManager).GetMethod("Hook", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var messageArgs = new object[] { IntPtr.Zero, 0x0006, new IntPtr(1L << 40), IntPtr.Zero, false };
                var ignored = (IntPtr)hook.Invoke(first, messageArgs)! == IntPtr.Zero && !(bool)messageArgs[4];
                rows.Add(new { check = "64-bit ordinary window message is ignored without narrowing its pointer", passed = ignored });
                var settings = new AppSettings { SelectHotkey = "Ctrl+Alt+F23", PauseHotkey = "", FloatingHotkey = "", RefreshHotkey = "" };
                var firstErrors = first.Apply(settings); var conflicts = second.Apply(settings);
                var conflictOkay = firstErrors.Length == 0 && conflicts.Length == 1;
                rows.Add(new { check = "actual duplicate global hotkey registration reports conflict", passed = conflictOkay, firstErrors, conflicts });
                passed = excluded && visibleSource && floatingExcluded && conflictOkay && ignored;
            }
            catch (Exception error) { rows.Add(new { check = "exception", passed = false, error = error.GetType().Name, message = error.Message }); }
            finally
            {
                first?.Dispose(); second?.Dispose(); floating?.CloseForShutdown(); overlay?.Close(); source.Close();
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "Real Windows capture exclusion and global hotkey conflict", utcTime = DateTimeOffset.UtcNow, passed, rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                app.Shutdown(passed ? 0 : 1);
            }
        };
        source.Show(); return app.Run();
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
