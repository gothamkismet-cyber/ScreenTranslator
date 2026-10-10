using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenTranslator.Capture;

public partial class WindowSelector : Window
{
    private readonly HashSet<IntPtr> _excluded;
    public WindowTarget? SelectedTarget { get; private set; }
    public WindowSelector(IEnumerable<IntPtr> excluded)
    {
        InitializeComponent();
        _excluded = new HashSet<IntPtr>(excluded);
        SourceInitialized += (_, _) => { var handle = new WindowInteropHelper(this).Handle; _excluded.Add(handle); ScreenCapture.ExcludeWindow(this); };
        Loaded += (_, _) => RefreshWindows();
    }
    private void RefreshWindows()
    {
        try
        {
            var previous = (WindowList.SelectedItem as WindowTarget)?.Handle;
            var windows = WindowTargets.List(_excluded);
            WindowList.ItemsSource = windows;
            WindowList.SelectedItem = windows.FirstOrDefault(window => window.Handle == previous);
            HintText.Text = windows.Count == 0 ? "未找到可选窗口。先打开目标软件并恢复窗口，再刷新列表。" : "窗口需要保持可见；关闭、最小化或被其他软件遮住时，翻译会暂停。";
        }
        catch (ArgumentException error) { HintText.Text = error.Message; }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();
    private void Window_Changed(object sender, SelectionChangedEventArgs e) => UseButton.IsEnabled = WindowList.SelectedItem is WindowTarget;
    private void Window_DoubleClick(object sender, MouseButtonEventArgs e) { if (WindowList.SelectedItem is WindowTarget) Use_Click(sender, e); }
    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowTarget selected) return;
        if (WindowTargets.Describe(selected.Handle) is not { } live || live.ProcessId != selected.ProcessId || live.ThreadId != selected.ThreadId)
        { HintText.Text = "这个窗口已经关闭，请刷新列表重新选择。"; return; }
        SelectedTarget = live;
        DialogResult = true;
    }
}
