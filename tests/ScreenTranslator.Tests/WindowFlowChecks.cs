using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenTranslator.Capture;
using ScreenTranslator.Display;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;

namespace ScreenTranslator.Tests;

internal static class WindowFlowChecks
{
    public static int Run(string root)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenTranslator;component/Themes/Theme.xaml") });
        var text = new TextBlock { FontSize = 32, Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap, Text = "Please open the door." };
        var source = new Window { Title = "窗口翻译测试 · 合成文字", Width = 820, Height = 230, Left = 70, Top = 370, Topmost = true, Background = Brushes.White, Content = text };
        var rows = new List<object>(); var passed = true; var started = false;
        void Pass(string name) => rows.Add(new { check = name, passed = true });
        source.ContentRendered += async (_, _) =>
        {
            if (started) return; started = true;
            ScreenTranslator.MainWindow? main = null; Window? blocker = null; Window? duplicate = null;
            var output = Path.Combine(root, "artifacts", "verification", "window-flow"); Directory.CreateDirectory(output);
            try
            {
                await using var server = new LoopbackServer();
                var profile = new ApiProfile { Name = "窗口翻译 · 本机模拟", Address = server.BaseAddress, Model = "window-fixture-model", Authentication = AuthMode.None, Stream = true };
                var store = new SettingsStore(Path.Combine(root, ".tools", "window-flow-settings"));
                store.Save(new SettingsBundle(new AppSettings { Profiles = [profile], SelectedProfileId = profile.Id, SelectHotkey = "", PauseHotkey = "", RefreshHotkey = "", FloatingHotkey = "" }, new() { [profile.Id] = new ProfileSecrets() }));
                main = new ScreenTranslator.MainWindow(store); main.Show(); await Task.Delay(200);
                var floating = (FloatingWindow)Field(main, "_floating");
                var session = (TranslationSession)Field(main, "_session");
                duplicate = new Window { Title = source.Title, Width = 220, Height = 120, Left = 1100, Top = 100, Background = Brushes.White };
                duplicate.Show();
                await PickWindowAsync(main, source);
                var target = (WindowTarget)Field(main, "_windowTarget");
                ProtocolChecks.Assert(target.Handle == new WindowInteropHelper(source).Handle && target.Handle != new WindowInteropHelper(duplicate).Handle);
                Pass("actual picker selects native window among duplicate titles and excludes translator windows");
                await PickWindowAsync(main, source, cancel: true);
                ProtocolChecks.Assert(Equals(target, Field(main, "_windowTarget")));
                Pass("cancel picker preserves previous selected window");
                Click(main, "StartButton"); await Translated(session, text.Text);
                Pass("selected whole client area -> native screenshot -> OCR -> loopback translation");
                main.Close();
                ProtocolChecks.Assert(!main.IsVisible && floating.IsVisible && floating.Owner is null && !session.Paused);
                text.Text = "The meeting starts at 10:30."; source.UpdateLayout();
                await Translated(session, text.Text); await Task.Delay(100);
                ProtocolChecks.Assert(((TextBox)floating.FindName("TranslationBox")).Text == session.Current.Translation);
                Pass("new window text continues translating with main UI closed");
                var before = (System.Drawing.Rectangle)Field(main, "_region");
                source.Left += 40; source.Top += 20; source.Width -= 40; source.Height += 20; source.UpdateLayout();
                text.Text = "You have 250 gold coins."; source.UpdateLayout();
                await Translated(session, text.Text);
                var after = (System.Drawing.Rectangle)Field(main, "_region");
                ProtocolChecks.Assert(before.Location != after.Location && before.Size != after.Size && !session.Paused);
                rows.Add(new { check = "moving and resizing follows physical capture bounds without reselecting", before, after, passed = true });
                source.WindowState = WindowState.Minimized;
                await SessionChecks.Until(() => session.Paused && session.Current.Status.Contains("最小化"));
                await NoNewRequests(server); Pass("minimized target pauses instead of capturing another application");
                source.WindowState = WindowState.Normal; source.Activate(); await Task.Delay(200);
                text.Text = "Save your progress before leaving."; source.UpdateLayout();
                Click(floating, "PauseButton"); await Translated(session, text.Text);
                ProtocolChecks.Assert(!main.IsVisible); Pass("floating Start resumes restored target while main remains hidden");
                source.Hide();
                await SessionChecks.Until(() => session.Paused && session.Current.Status.Contains("不可见"));
                await NoNewRequests(server); source.Show(); await Task.Delay(200);
                text.Text = "The train arrives in 5 minutes."; source.UpdateLayout();
                Click(floating, "PauseButton"); await Translated(session, text.Text);
                Pass("hidden target pauses; showing it allows explicit resume");
                source.Topmost = false; source.Activate();
                blocker = new Window { Title = "遮挡检查 · 合成无关内容", Width = 300, Height = 150, Left = source.Left + 20, Top = source.Top + 60, Topmost = true, Background = Brushes.White, Content = new TextBlock { Text = "UNRELATED WINDOW TEXT", FontSize = 24 } };
                blocker.Show();
                await SessionChecks.Until(() => session.Paused && session.Current.Status.Contains("遮住"));
                await NoNewRequests(server); ProtocolChecks.Assert(!session.Current.Original.Contains("UNRELATED"));
                Pass("topmost unrelated window covering ordinary target pauses without unrelated request");
                blocker.Close(); blocker = null; source.Topmost = true; source.Activate(); await Task.Delay(200);
                text.Text = "Find the key near the old bridge."; source.UpdateLayout();
                Click(floating, "PauseButton"); await Translated(session, text.Text);
                Pass("removing obstruction allows resume from floating UI");
                source.Close();
                await SessionChecks.Until(() => session.Paused && session.Current.Status.Contains("已关闭"));
                await NoNewRequests(server); Pass("closed target pauses instead of switching to same-title window");
                var closed = false; main.Closed += (_, _) => closed = true;
                ((MenuItem)floating.FindName("ExitMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await SessionChecks.Until(() => closed);
                ProtocolChecks.Assert((bool)Field(main, "_closed") && !floating.IsVisible);
                Pass("floating Exit menu shuts down session, native resources and both interfaces");
            }
            catch (Exception error) { passed = false; rows.Add(new { check = "exception", passed = false, error = error.GetType().Name, message = error.Message }); }
            finally
            {
                main?.ExitApplication(); blocker?.Close(); duplicate?.Close(); source.Close();
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "Real WPF window selection and independent floating lifecycle; only synthetic windows and loopback HTTP", utcTime = DateTimeOffset.UtcNow, realAiCalls = 0, normalUserSettingsTouched = false, passed, rows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                application.Shutdown(passed ? 0 : 1);
            }
        };
        source.Show(); return application.Run();
    }
    private static async Task PickWindowAsync(ScreenTranslator.MainWindow main, Window source, bool cancel = false)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (System.Windows.Application.Current.Windows.OfType<WindowSelector>().FirstOrDefault() is not { } selector || !selector.IsLoaded) return;
            timer.Stop();
            try
            {
                if (cancel) { selector.Close(); ready.SetResult(); return; }
                var list = (ListBox)selector.FindName("WindowList");
                var handles = new[] { new WindowInteropHelper(main).Handle, new WindowInteropHelper((FloatingWindow)Field(main, "_floating")).Handle, new WindowInteropHelper(selector).Handle };
                ProtocolChecks.Assert(!list.Items.Cast<WindowTarget>().Any(window => handles.Contains(window.Handle)));
                list.SelectedItem = list.Items.Cast<WindowTarget>().Single(window => window.Handle == new WindowInteropHelper(source).Handle);
                Click(selector, "UseButton"); ready.SetResult();
            }
            catch (Exception error) { selector.Close(); ready.SetException(error); }
        };
        timer.Start();
        try { Click(main, "SelectWindowButton"); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { timer.Stop(); }
        var gate = (SemaphoreSlim)Field(main, "_operation"); await gate.WaitAsync(); gate.Release();
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static Task Translated(TranslationSession session, string expected) => SessionChecks.Until(() => Normalize(session.Current.Original) == Normalize(expected) && session.Current.Translation == LoopbackServer.Expected(session.Current.Original) && !session.Paused);
    private static async Task NoNewRequests(LoopbackServer server) { var count = server.Requests; await Task.Delay(1100); ProtocolChecks.Assert(server.Requests == count); }
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
