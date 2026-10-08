using System.Windows;

namespace ScreenTranslator;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作遇到错误，请暂停后重试。\n" + args.Exception.GetType().Name, "屏幕翻译", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        if (e.Args.Length == 2 && e.Args[0] == "--verify-ui")
        {
            var output = System.IO.Path.GetFullPath(e.Args[1]);
            try
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = new MainWindow(Display.UiVerification.Prepare(output));
                MainWindow = window;
                var started = false;
                window.ContentRendered += async (_, _) => { if (started) return; started = true; await Display.UiVerification.RunAsync(window, output); };
                window.Show();
            }
            catch (Exception error) { System.IO.File.WriteAllText(System.IO.Path.Combine(output, "startup-failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
