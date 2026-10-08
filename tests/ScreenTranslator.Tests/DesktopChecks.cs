using Rectangle = System.Drawing.Rectangle;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenTranslator.Capture;
using ScreenTranslator.Ocr;
using SkiaSharp;

namespace ScreenTranslator.Tests;

internal static class DesktopChecks
{
    public static int Run(string root)
    {
        var output = Path.Combine(root, "artifacts", "verification", "desktop");
        Directory.CreateDirectory(output);
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var text = new TextBlock { FontSize = 32, FontFamily = new FontFamily("Microsoft YaHei UI"), Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap };
        var source = new Window { Title = "屏幕翻译 · 本机捕获测试", Width = 820, Height = 230, Left = 100, Top = 100, Topmost = true, Background = Brushes.White, Content = text };
        var results = new List<object>();
        var passed = true;
        source.ContentRendered += async (_, _) =>
        {
            try
            {
                using var ocr = new OcrService(Path.Combine(root, "models", "screen-ocr"), Path.Combine(root, ".tools", "test-model-cache"));
                foreach (var (language, original) in new[] { (SourceLanguage.English, "Do not delete this file. You have 250 gold coins."), (SourceLanguage.Japanese, "このファイルを削除しないでください。"), (SourceLanguage.Korean, "이 파일을 삭제하지 마세요.") })
                {
                    text.Text = original;
                    source.UpdateLayout();
                    await Task.Delay(400);
                    var start = text.PointToScreen(new System.Windows.Point(0, 0));
                    var finish = text.PointToScreen(new System.Windows.Point(text.ActualWidth, text.ActualHeight));
                    var region = Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Ceiling(finish.X), (int)Math.Ceiling(finish.Y));
                    using var capture = ScreenCapture.Capture(region);
                    using (var file = File.Create(Path.Combine(output, language + ".png"))) capture.Encode(file, SKEncodedImageFormat.Png, 100);
                    var reading = await ocr.ReadAsync(capture, language);
                    var matched = Normalize(reading.Text) == Normalize(original);
                    passed &= matched;
                    results.Add(new { language = language.ToString(), expected = original, actual = reading.Text, matched, physicalRegion = new { region.X, region.Y, region.Width, region.Height }, dpiScale = VisualTreeHelper.GetDpi(source).DpiScaleX, elapsedMs = reading.Elapsed.TotalMilliseconds });
                }
                var excluded = ScreenCapture.ExcludeWindow(source);
                results.Add(new { exclusionApiReturnedSuccess = excluded, note = "Window exclusion return value only; pixel exclusion will be verified separately. Actual selection gestures and other DPI settings still need human trial." });
            }
            catch (Exception error) { passed = false; results.Add(new { error = error.GetType().Name, message = error.Message }); }
            finally
            {
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "Real Windows screen capture of this project's own test window", utcTime = DateTimeOffset.UtcNow, passed, results }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                source.Close();
                application.Shutdown(passed ? 0 : 1);
            }
        };
        source.Show();
        return application.Run();
    }

    private static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
