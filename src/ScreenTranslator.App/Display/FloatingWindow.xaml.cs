using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ScreenTranslator.Capture;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;

namespace ScreenTranslator.Display;

public partial class FloatingWindow : Window
{
    private const string EmptyHint = "选择窗口或框选区域后，中文会显示在这里。";
    private const string WaitingHint = "等待译文…";
    private const string CopyGlyph = "";
    private readonly DispatcherTimer _copiedReset = new() { Interval = TimeSpan.FromSeconds(1.4) };
    private bool _allowClose;
    public bool CaptureExcluded { get; private set; }
    public event Action? PauseRequested;
    public event Action? MainRequested;
    public event Action? ExitRequested;
    public event Action? WindowRequested;
    public event Action? RegionRequested;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public FloatingWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => CaptureExcluded = ScreenCapture.ExcludeWindow(this);
        Closing += HideOnClose;
        _copiedReset.Tick += (_, _) => { _copiedReset.Stop(); CopyButton.Content = "复制译文"; ThemeAssist.SetGlyph(CopyButton, CopyGlyph); };
    }
    public void Apply(AppSettings settings)
    {
        OriginalPanel.Visibility = settings.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        TranslationBox.FontSize = Math.Clamp(settings.FloatingFontSize, 16, 36);
        Opacity = Math.Clamp(settings.FloatingOpacity, .5, 1);
    }
    public void Update(SessionView view)
    {
        OriginalBox.Text = view.Original;
        TranslationBox.Text = view.Translation.Length == 0 ? (view.Original.Length == 0 ? EmptyHint : WaitingHint) : view.Translation;
        StatusText.Text = view.Status; PauseButton.Content = view.Paused ? "开始" : "暂停";
        ThemeAssist.SetGlyph(PauseButton, view.Paused ? ThemeAssist.PlayGlyph : ThemeAssist.PauseGlyph);
        StatusDot.Fill = TryFindResource(view.Paused ? "InputBorder" : "Accent") as System.Windows.Media.Brush ?? StatusDot.Fill;
    }
    private void Pin_Changed(object sender, RoutedEventArgs e) => Topmost = (sender as System.Windows.Controls.CheckBox)?.IsChecked == true;
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Pause_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private void Main_Click(object sender, RoutedEventArgs e) => MainRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void Window_Click(object sender, RoutedEventArgs e) => WindowRequested?.Invoke();
    private void Region_Click(object sender, RoutedEventArgs e) => RegionRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void More_Click(object sender, RoutedEventArgs e) { MoreMenu.PlacementTarget = MoreButton; MoreMenu.IsOpen = true; }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TranslationBox.Text.Length == 0 || TranslationBox.Text is EmptyHint or WaitingHint) return;
            Clipboard.SetText(TranslationBox.Text);
            CopyButton.Content = "已复制"; ThemeAssist.SetGlyph(CopyButton, ThemeAssist.CheckGlyph);
            _copiedReset.Stop(); _copiedReset.Start();
        }
        catch (ExternalException) { StatusText.Text = "剪贴板暂时被占用，请稍后再复制。"; }
    }
    private void HideOnClose(object? sender, CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; Hide(); } }
    public void CloseForShutdown() { _allowClose = true; _copiedReset.Stop(); Close(); }
}
