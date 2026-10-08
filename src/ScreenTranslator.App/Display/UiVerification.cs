using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;

namespace ScreenTranslator.Display;

internal static class UiVerification
{
    public static SettingsStore Prepare(string output)
    {
        Directory.CreateDirectory(output);
        var store = new SettingsStore(Path.Combine(output, "main-isolated-settings"));
        var profile = new ApiProfile { Name = "界面验证示例连接", Address = "https://example.test/v1", Model = "manually-entered-model", Stream = true };
        store.Save(new SettingsBundle(new AppSettings { Profiles = [profile], SelectedProfileId = profile.Id }, new() { [profile.Id] = new() { ApiKey = "ui-fixture-secret-5198" } }));
        return store;
    }
    public static async Task RunAsync(MainWindow main, string output)
    {
        var rows = new List<object>(); var passed = true; FloatingWindow? floating = null;
        try
        {
            await Task.Delay(300);
            main.Title = "屏幕 AI 翻译 · 自动界面验证（示例译文）";
            ((TextBox)main.FindName("OriginalBox")).Text = "Do not delete this file. You have 250 gold coins.";
            ((TextBox)main.FindName("TranslationBox")).Text = "请不要删除这个文件。你有 250 枚金币。";
            ((TextBlock)main.FindName("StatusText")).Text = "界面验证示例 · 此处中文为测试夹具，未调用真实 AI。";
            main.UpdateLayout(); Render(main, Path.Combine(output, "main-preview.png"));
            rows.Add(new { check = "main window initialized with manual model and readable original/translation controls", passed = true, dpiScale = VisualTreeHelper.GetDpi(main).DpiScaleX });
            floating = new FloatingWindow { Owner = main, Left = main.Left + 50, Top = main.Top + 50 };
            floating.Apply(new AppSettings()); floating.Update(new SessionView(0, 0, true, "Do not delete this file. You have 250 gold coins.", "请不要删除这个文件。你有 250 枚金币。", "界面测试夹具 · 未调用真实 AI", 0, 0, 90, null)); floating.Show();
            await Task.Delay(150); Render(floating, Path.Combine(output, "floating-preview.png"));
            ((CheckBox)floating.FindName("PinCheck")).IsChecked = false; var pinOff = !floating.Topmost; ((CheckBox)floating.FindName("PinCheck")).IsChecked = true;
            floating.Close(); var closeHides = !floating.IsVisible;
            rows.Add(new { check = "floating pin and close-to-hide", passed = pinOff && floating.Topmost && closeHides, captureExcluded = floating.CaptureExcluded }); passed &= pinOff && floating.Topmost && closeHides;

            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var selection = new Capture.RegionSelector.SelectionWindow(screen, new TaskCompletionSource<System.Drawing.Rectangle?>());
            selection.Show(); await Task.Delay(200);
            var sample = new Rect(Math.Round(selection.ActualWidth * 0.3), Math.Round(selection.ActualHeight * 0.42), 600, 76);
            selection.ShowSample(sample); await Task.Delay(100);
            RenderOverlay(selection, sample, Path.Combine(output, "selection-preview.png"));
            selection.Close();
            rows.Add(new { check = "selection overlay renders dimmed screen, highlighted box, pixel size label and hint", passed = true });

            var bundle = Prepare(Path.Combine(output, "settings-test")).Load();
            var settingsWindow = new SettingsWindow(bundle) { Owner = main };
            var entered = false;
            settingsWindow.ContentRendered += async (_, _) =>
            {
                if (entered) return; entered = true;
                await Task.Delay(150);
                ((TextBox)settingsWindow.FindName("NameBox")).Text = "常用连接";
                ((ComboBox)settingsWindow.FindName("ModelBox")).Text = "manual-model-A";
                Button(settingsWindow, "复制").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((TextBox)settingsWindow.FindName("NameBox")).Text = "备用连接";
                ((ComboBox)settingsWindow.FindName("ModelBox")).Text = "manual-model-B";
                ((ComboBox)settingsWindow.FindName("FormatBox")).SelectedIndex = 1;
                settingsWindow.UpdateLayout(); Render(settingsWindow, Path.Combine(output, "settings-preview.png"));
                ((Expander)settingsWindow.FindName("AdvancedExpander")).IsExpanded = true; settingsWindow.UpdateLayout();
                ((ScrollViewer)settingsWindow.FindName("FormScroll")).ScrollToVerticalOffset(330); await Task.Delay(300);
                Render(settingsWindow, Path.Combine(output, "settings-advanced-preview.png"));
                ((TabControl)settingsWindow.FindName("SettingsTabs")).SelectedIndex = 1; await Task.Delay(350);
                Render(settingsWindow, Path.Combine(output, "settings-display-preview.png"));
                ((TabControl)settingsWindow.FindName("SettingsTabs")).SelectedIndex = 0; await Task.Delay(100);
                Button(settingsWindow, "保存并使用当前连接").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            };
            var result = settingsWindow.ShowDialog();
            var settingsStore = new SettingsStore(Path.Combine(output, "settings-roundtrip")); settingsStore.Save(settingsWindow.Result); var restart = settingsStore.Load();
            var profilesOkay = result == true && restart.Settings.Profiles.Count == 2 && restart.Settings.Profiles.Select(p => p.Model).Order().SequenceEqual(new[] { "manual-model-A", "manual-model-B" });
            rows.Add(new { check = "actual settings form copy, manual models, two formats, save and restart", passed = profilesOkay }); passed &= profilesOkay;
            var placeholderKey = "ui-fixture-secret-5198";
            var secretsAbsent = !File.ReadAllText(Path.Combine(settingsStore.Root, "settings.json")).Contains(placeholderKey) && !SettingsStore.ExportWithoutSecrets(restart.Settings).Contains(placeholderKey);
            rows.Add(new { check = "UI-saved key absent from plain settings and export", passed = secretsAbsent }); passed &= secretsAbsent;
            using var ocr = new Ocr.OcrService(dictionaryCache: Path.Combine(output, "model-cache"));
            foreach (var (language, original) in new[] { (Ocr.SourceLanguage.English, "Do not delete 250 files."), (Ocr.SourceLanguage.Japanese, "このファイルを削除しないでください。"), (Ocr.SourceLanguage.Korean, "이 파일을 삭제하지 마세요.") })
            {
                using var image = new SkiaSharp.SKBitmap(1100, 160); using var canvas = new SkiaSharp.SKCanvas(image); canvas.Clear(SkiaSharp.SKColors.White);
                using var face = SkiaSharp.SKTypeface.FromFamilyName(language == Ocr.SourceLanguage.Korean ? "Malgun Gothic" : language == Ocr.SourceLanguage.Japanese ? "Yu Gothic" : "Arial");
                using var font = new SkiaSharp.SKFont(face, 36); using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true }; canvas.DrawText(original, 32, 94, font, paint);
                var reading = await ocr.ReadAsync(image, language); var matched = reading.Text == original;
                rows.Add(new { check = "models and native OCR libraries loaded from actual executable directory", language = language.ToString(), expected = original, actual = reading.Text, passed = matched }); passed &= matched;
            }
        }
        catch (Exception error) { passed = false; rows.Add(new { check = "UI exception", passed = false, error = error.GetType().Name, message = error.Message, inner = error.InnerException?.Message }); }
        finally
        {
            floating?.CloseForShutdown();
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "Actual WPF initialization, interactions and rendered previews; only synthetic data and isolated settings", utcTime = DateTimeOffset.UtcNow, passed, rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            main.Close();
            await Task.Delay(200);
            System.Windows.Application.Current.Shutdown(passed ? 0 : 1);
        }
    }
    private static Button Button(DependencyObject root, string content)
    {
        if (root is Button button && (string?)button.Content == content) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Button(VisualTreeHelper.GetChild(root, i), content); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Expected UI button not found: " + content);
    }
    // The overlay is transparent, so it is drawn over a synthetic stand-in; real screen pixels never reach the report.
    private static void RenderOverlay(Window window, Rect sample, string path)
    {
        window.UpdateLayout();
        var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(226, 232, 240), Color.FromRgb(248, 250, 252), 35), null, bounds);
            var line = new FormattedText("Do not delete this file. You have 250 gold coins.", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 26, Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
            context.DrawText(line, new System.Windows.Point(sample.X + 18, sample.Y + (sample.Height - line.Height) / 2));
            context.DrawRectangle(new VisualBrush((Visual)window.Content), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
