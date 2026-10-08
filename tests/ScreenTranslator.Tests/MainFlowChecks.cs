using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ScreenTranslator.Capture;
using ScreenTranslator.Ocr;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenTranslator.Tests;

internal static class MainFlowChecks
{
    public static int Run(string root)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Use the application's real theme so the main window is built with the same styles as in the shipped program.
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenTranslator;component/Themes/Theme.xaml") });
        var text = new TextBlock { FontSize = 32, Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap, Height = 130 };
        var source = new Window { Title = "完整操作测试的原文窗口", Width = 820, Height = 230, Left = 80, Top = 350, Topmost = true, Background = Brushes.White, Content = text };
        var rows = new List<object>(); var passed = true; var started = false;
        source.ContentRendered += async (_, _) =>
        {
            if (started) return; started = true;
            var output = Path.Combine(root, "artifacts", "verification", "main-flow"); Directory.CreateDirectory(output);
            ScreenTranslator.MainWindow? main = null;
            try
            {
                await using var server = new LoopbackServer();
                var store = new SettingsStore(Path.Combine(root, ".tools", "main-flow-settings"));
                var profile = new ApiProfile { Name = "实际主窗口 · 本机模拟", Address = server.BaseAddress, Model = "manual-local-model", Authentication = AuthMode.None, Stream = true };
                store.Save(new SettingsBundle(new AppSettings { Profiles = [profile], SelectedProfileId = profile.Id, SelectHotkey = "", PauseHotkey = "", RefreshHotkey = "", FloatingHotkey = "" }, new() { [profile.Id] = new ProfileSecrets() }));
                main = new ScreenTranslator.MainWindow(store); main.Show(); await Task.Delay(250);
                var origin = text.PointToScreen(new System.Windows.Point(0, 0)); var end = text.PointToScreen(new System.Windows.Point(text.ActualWidth, text.ActualHeight));
                var region = Rectangle.FromLTRB((int)Math.Floor(origin.X), (int)Math.Floor(origin.Y), (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
                Field(main, "_region").SetValue(main, region); Field(main, "_topology").SetValue(main, DisplayTopology.Fingerprint());
                var session = (TranslationSession)Field(main, "_session").GetValue(main)!;
                foreach (var language in Enum.GetValues<SourceLanguage>())
                {
                    ((ComboBox)main.FindName("LanguagePicker")).SelectedIndex = (int)language;
                    var gate = (SemaphoreSlim)Field(main, "_operation").GetValue(main)!; await gate.WaitAsync(); gate.Release();
                    text.Text = Program.Samples(language)[2]; source.UpdateLayout(); await Task.Delay(150);
                    ((Button)main.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await SessionChecks.Until(() => session.Current.TranslationMs is not null && Normalize(session.Current.Original) == Normalize(text.Text));
                    await Task.Delay(100);
                    var view = session.Current;
                    var matched = view.Translation == LoopbackServer.Expected(view.Original) && ((TextBox)main.FindName("OriginalBox")).Text == view.Original && ((TextBox)main.FindName("TranslationBox")).Text == view.Translation;
                    rows.Add(new { check = "actual main Start button -> screenshot -> OCR -> loopback API -> main and floating display", language = language.ToString(), expected = text.Text, actual = view.Original, associatedTranslation = view.Translation, passed = matched }); passed &= matched;
                    ((Button)main.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Task.Delay(100);
                }
                var beforeManual = server.Requests;
                ((Button)main.FindName("ReadButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await SessionChecks.Until(() => server.Requests == beforeManual + 1 && session.Paused && session.Current.TranslationMs is not null);
                rows.Add(new { check = "manual translation while paused completes one request and returns to paused", passed = true });
                ((Button)main.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await SessionChecks.Until(() => !session.Paused && session.Current.TranslationMs is not null);
                ((Button)main.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var afterPause = server.Requests; await Task.Delay(1700);
                var pauseOkay = session.Paused && server.Requests == afterPause;
                rows.Add(new { check = "Pause button stops capture-produced requests", passed = pauseOkay }); passed &= pauseOkay;
                rows.Add(new { check = "selection setup boundary", note = "Test injects the known pixel rectangle; human drag selection remains pending. Translation responses are local synthetic fixtures, not AI quality evidence." });
                ((Button)main.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await SessionChecks.Until(() => !session.Paused);
                var closed = false; main.Closed += (_, _) => closed = true;
                main.Close(); await SessionChecks.Until(() => closed);
                rows.Add(new { check = "close during active capture cleans up and completes without recursive Closing error", passed = true });
            }
            catch (Exception error) { passed = false; rows.Add(new { check = "exception", passed = false, error = error.GetType().Name, message = error.Message }); main?.Close(); }
            finally
            {
                source.Close(); File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "Actual product main-window actions with synthetic screen text and loopback HTTP", utcTime = DateTimeOffset.UtcNow, passed, rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); application.Shutdown(passed ? 0 : 1);
            }
        };
        source.Show(); return application.Run();
    }
    private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
