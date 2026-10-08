using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenTranslator.Capture;
using ScreenTranslator.Display;
using ScreenTranslator.Ocr;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;
using ScreenTranslator.Translation;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenTranslator;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private SettingsBundle _bundle;
    private readonly OcrService _ocr = new();
    private readonly ApiTranslationClient _client = new();
    private readonly TranslationSession _session;
    private readonly FloatingWindow _floating;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private Rectangle? _region;
    private string _topology = "";
    private CancellationTokenSource? _captureCancel;
    private Task _captureTask = Task.CompletedTask;
    private HotkeyManager? _hotkeys;
    private bool _ready;
    private bool _closing;
    private bool _closed;
    private bool _allowClose;
    private bool _captureExcluded;
    private bool _canSave = true;
    private bool _singleRun;
    private long _captures;

    public MainWindow(SettingsStore? store = null)
    {
        InitializeComponent();
        _store = store ?? new SettingsStore();
        string? loadingError = null;
        try { _bundle = _store.Load(); }
        catch (Exception error)
        {
            _canSave = false;
            try { _bundle = new SettingsBundle(_store.LoadConfigurationsOnly(), []); }
            catch { _bundle = new SettingsBundle(new AppSettings(), []); }
            loadingError = $"设置或密钥读取失败（{error.GetType().Name}）。原文件已保留；请在连接设置中重新填写密钥并保存。";
        }
        _session = new TranslationSession(_client, new DiagnosticLog(_store.Root));
        _floating = new FloatingWindow();
        Loaded += (_, _) => { if (_floating.Owner is null) _floating.Owner = this; };
        _floating.Apply(_bundle.Settings);
        _floating.PauseRequested += () => Toggle_Click(this, new RoutedEventArgs());
        _session.Changed += Session_Changed;
        ReloadPickers();
        if (loadingError is not null) StatusText.Text = loadingError;
        SourceInitialized += (_, _) =>
        {
            _captureExcluded = ScreenCapture.ExcludeWindow(this);
            _hotkeys = new HotkeyManager(this);
            _hotkeys.Pressed += Hotkey_Pressed;
            ApplyHotkeys();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(DisplayHook);
        };
        Closing += ClosingAsync;
        _ready = true;
    }

    private void ReloadPickers()
    {
        var ready = _ready; _ready = false;
        ProfilePicker.ItemsSource = null; ProfilePicker.ItemsSource = _bundle.Settings.Profiles;
        ProfilePicker.SelectedItem = _bundle.Settings.Profiles.FirstOrDefault(p => p.Id == _bundle.Settings.SelectedProfileId) ?? _bundle.Settings.Profiles.FirstOrDefault();
        LanguagePicker.SelectedIndex = Math.Clamp((int)_bundle.Settings.SourceLanguage, 0, 2);
        UpdateConnectionHint();
        _ready = ready;
    }
    private void UpdateConnectionHint()
    {
        if (ProfilePicker.SelectedItem is not ApiProfile profile) { ConnectionHint.Text = "先在“连接与设置”里填入地址、密钥和模型。"; return; }
        try { ConnectionHint.Text = $"{profile.Model} · {EndpointResolver.Display(EndpointResolver.Resolve(profile))}"; }
        catch (ArgumentException) { ConnectionHint.Text = "连接设置尚未完整，请打开设置检查。"; }
    }
    private void BindSelected()
    {
        if (ProfilePicker.SelectedItem is not ApiProfile profile) throw new ArgumentException("请先打开“连接与设置”，填写并保存一个 AI 连接。");
        _session.Configure(profile, _bundle.Secrets.GetValueOrDefault(profile.Id) ?? new ProfileSecrets(), (SourceLanguage)LanguagePicker.SelectedIndex);
    }
    private void Session_Changed(SessionView view)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_closed || !_session.IsCurrent(view)) return;
            // A queued notification can arrive after a newer notification for the same content.
            // Read the authoritative snapshot on the UI thread instead of painting an older partial result.
            view = _session.Current;
            OriginalBox.Text = view.Original; TranslationBox.Text = view.Translation;
            StatusText.Text = view.Status;
            StartButton.Content = view.Paused ? "② 开始连续翻译" : "暂停连续翻译";
            ThemeAssist.SetGlyph(StartButton, view.Paused ? ThemeAssist.PlayGlyph : ThemeAssist.PauseGlyph);
            StatusDot.Fill = TryFindResource(view.Paused ? "InputBorder" : "Accent") as System.Windows.Media.Brush ?? StatusDot.Fill;
            MetricsText.Text = $"本次识字：{view.OcrMs?.ToString("F0") ?? "—"} 毫秒\n本次翻译：{view.TranslationMs?.ToString("F0") ?? "—"} 毫秒\n已发送 {view.Requests} 次 · 缓存 {view.CacheHits} 次\n采集 { _captures } 次 · 每轮约 750 毫秒";
            _floating.Update(view);
            if (view.Paused) _captureCancel?.Cancel();
            if (_singleRun && (view.TranslationMs is not null || view.Error is not null))
            {
                _singleRun = false;
                if (!view.Paused) _session.Pause(view.Status + " · 单次翻译结束");
            }
        });
    }

    private async void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        await _operation.WaitAsync();
        var floatingVisible = _floating.IsVisible;
        try
        {
            _singleRun = false; _session.Pause(); await StopCaptureAsync();
            Hide(); _floating.Hide();
            var selected = await RegionSelector.SelectAsync();
            if (selected is { } region)
            {
                _region = region; _topology = DisplayTopology.Fingerprint();
                _session.Pause("选区已就绪，可以开始或翻译一次。", clearContent: true);
                ShowRegion($"{region.Width} × {region.Height} 像素", selected: true); RegionPreview.Source = null;
                OriginalBox.Text = ""; TranslationBox.Text = "";
                StatusText.Text = "选区已就绪，可以开始或翻译一次。";
            }
            else StatusText.Text = _region is null ? "已取消选区。" : "已取消，保留原选区。";
        }
        catch (Exception error) { StatusText.Text = SafeMessage(error); }
        finally
        {
            if (!_closing) { Show(); Activate(); if (floatingVisible) _floating.Show(); }
            _operation.Release();
        }
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        // Pause immediately, before waiting for an OCR operation to finish.
        if (!_session.Paused) { _singleRun = false; _session.Pause(); _captureCancel?.Cancel(); return; }
        await _operation.WaitAsync();
        try
        {
            await StopCaptureAsync();
            RequireRegion(); BindSelected(); _singleRun = false; _session.Start();
            _floating.Apply(_bundle.Settings); _floating.Show();
            _captureCancel = new CancellationTokenSource();
            _captureTask = CaptureLoopAsync(_captureCancel.Token, (SourceLanguage)LanguagePicker.SelectedIndex);
        }
        catch (Exception error) { StatusText.Text = SafeMessage(error); }
        finally { _operation.Release(); }
    }
    private async Task CaptureLoopAsync(CancellationToken token, SourceLanguage language)
    {
        try
        {
            while (!token.IsCancellationRequested && !_session.Paused)
            {
                if (ProfilePicker.IsDropDownOpen || LanguagePicker.IsDropDownOpen) { await Task.Delay(750, token); continue; }
                var timer = Stopwatch.StartNew();
                await ReadRegionAsync(language, false, token);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 750 - timer.Elapsed.TotalMilliseconds)), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _session.Pause(SafeMessage(error)); }
    }
    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        await _operation.WaitAsync();
        ReadButton.IsEnabled = false;
        try
        {
            RequireRegion();
            if (_session.Paused) { await StopCaptureAsync(); BindSelected(); _singleRun = true; _session.Start(); }
            var language = (SourceLanguage)LanguagePicker.SelectedIndex;
            await ReadRegionAsync(language, true, CancellationToken.None);
            if (_singleRun && _session.Current.Original.Length == 0) { _singleRun = false; _session.Pause("没有识别到文字，请重新选区或切换来源语言。"); }
            _floating.Apply(_bundle.Settings); _floating.Show();
        }
        catch (Exception error) { _singleRun = false; _session.Pause(SafeMessage(error)); }
        finally { ReadButton.IsEnabled = true; _operation.Release(); }
    }
    // Once a region exists the first-use steps are done; hiding them leaves room for the live metrics.
    private void ShowRegion(string text, bool selected) { RegionText.Text = text; FirstUseGuide.Visibility = selected ? Visibility.Collapsed : Visibility.Visible; }
    private void RequireRegion()
    {
        if (ProfilePicker.IsDropDownOpen || LanguagePicker.IsDropDownOpen) throw new ArgumentException("请先关闭语言或连接菜单，再开始或刷新。");
        if (_region is null) throw new ArgumentException("请先点击“选择屏幕区域”，拖动框选需要翻译的文字。");
        if (_topology != DisplayTopology.Fingerprint()) { _region = null; ShowRegion("请重新选区", selected: false); throw new ArgumentException("显示器或缩放已改变，请重新选择屏幕区域。"); }
        if (!_captureExcluded && DisplayTopology.Overlaps(this, _region.Value) || !_floating.CaptureExcluded && DisplayTopology.Overlaps(_floating, _region.Value)) throw new ArgumentException("当前系统没有成功排除翻译窗口，请将主窗口和悬浮窗移出选区后再开始。");
    }
    private async Task ReadRegionAsync(SourceLanguage language, bool force, CancellationToken token)
    {
        await _captureGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested(); RequireRegion();
            var region = _region!.Value;
            var epoch = _session.Current.Epoch;
            using var image = await Task.Run(() => ScreenCapture.Capture(region), token);
            var reading = await _ocr.ReadAsync(image, language, token);
            token.ThrowIfCancellationRequested();
            if (epoch != _session.Current.Epoch || _session.Paused) return;
            _captures++;
            if (_captures == 1 || _captures % 4 == 0 || force)
            {
                using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var buffer = png.AsStream();
                var preview = new BitmapImage(); preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad; preview.StreamSource = buffer; preview.DecodePixelWidth = 480; preview.EndInit(); preview.Freeze();
                RegionPreview.Source = preview;
            }
            _session.Observe(reading.Text, reading.Elapsed.TotalMilliseconds, force);
        }
        finally { _captureGate.Release(); }
    }
    private async Task StopCaptureAsync()
    {
        _captureCancel?.Cancel();
        try { await _captureTask; } catch (OperationCanceledException) { }
        _captureCancel?.Dispose(); _captureCancel = null; _captureTask = Task.CompletedTask;
    }
    private async void Profile_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) await ChangeConfigurationAsync(); }
    private async void Language_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) await ChangeConfigurationAsync(); }
    private async Task ChangeConfigurationAsync()
    {
        if (_closing) return;
        _singleRun = false; _session.Pause("连接或语言已改变，连续翻译已暂停。"); _captureCancel?.Cancel();
        await _operation.WaitAsync();
        try
        {
            await StopCaptureAsync();
            _bundle.Settings.SelectedProfileId = (ProfilePicker.SelectedItem as ApiProfile)?.Id;
            _bundle.Settings.SourceLanguage = (SourceLanguage)LanguagePicker.SelectedIndex;
            UpdateConnectionHint();
            if (ProfilePicker.SelectedItem is ApiProfile) BindSelected();
            SaveSettings();
        }
        catch (Exception error) { StatusText.Text = SafeMessage(error); }
        finally { _operation.Release(); }
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _singleRun = false; _session.Pause("设置期间已暂停。"); _captureCancel?.Cancel();
        await _operation.WaitAsync();
        try
        {
            await StopCaptureAsync();
            var window = new SettingsWindow(_bundle) { Owner = this };
            if (window.ShowDialog() == true)
            {
                _store.Save(window.Result); _bundle = window.Result; _canSave = true;
                ReloadPickers(); _floating.Apply(_bundle.Settings); ApplyHotkeys();
                if (ProfilePicker.SelectedItem is ApiProfile) BindSelected();
                StatusText.Text = "设置已保存。选择区域后开始；修改快捷键时请留意冲突提示。";
            }
        }
        catch (Exception error) { StatusText.Text = SafeMessage(error); }
        finally { _operation.Release(); }
    }
    private void Floating_Click(object sender, RoutedEventArgs e) { if (_floating.IsVisible) _floating.Hide(); else { _floating.Apply(_bundle.Settings); _floating.Update(_session.Current); _floating.Show(); } }
    private void ApplyHotkeys()
    {
        try
        {
            HotkeyManager.Validate(_bundle.Settings);
            var failures = _hotkeys?.Apply(_bundle.Settings) ?? [];
            HotkeyHint.Text = $"⚠ 快捷键 {string.Join("、", failures)} 已被其他软件占用，未能注册；可在“连接与设置”中更换或留空。";
            HotkeyHint.Visibility = failures.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            SelectButton.ToolTip = _bundle.Settings.SelectHotkey; StartButton.ToolTip = _bundle.Settings.PauseHotkey; ReadButton.ToolTip = _bundle.Settings.RefreshHotkey;
        }
        catch (ArgumentException error) { HotkeyHint.Text = error.Message; HotkeyHint.Visibility = Visibility.Visible; }
    }
    private void Hotkey_Pressed(HotkeyAction action)
    {
        if (_closing || !IsEnabled) return;
        switch (action) { case HotkeyAction.Select: SelectRegion_Click(this, new RoutedEventArgs()); break; case HotkeyAction.Pause: Toggle_Click(this, new RoutedEventArgs()); break; case HotkeyAction.Floating: Floating_Click(this, new RoutedEventArgs()); break; case HotkeyAction.Refresh: Read_Click(this, new RoutedEventArgs()); break; }
    }
    private IntPtr DisplayHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((message == 0x007E || message == 0x02E0) && _region is not null)
            Dispatcher.InvokeAsync(() => { _region = null; ShowRegion("请重新选区", selected: false); _singleRun = false; _session.Pause("显示器或缩放发生变化，已暂停。请重新选区。", clearContent: true); _captureCancel?.Cancel(); RegionPreview.Source = null; });
        return IntPtr.Zero;
    }
    private void SaveSettings() { if (_canSave) _store.Save(_bundle); }
    private async void ClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _singleRun = false; _session.Pause("正在关闭…"); _captureCancel?.Cancel();
        await _operation.WaitAsync();
        try { await StopCaptureAsync(); await _session.DisposeAsync(); SaveSettings(); }
        catch { /* Closing must still release native resources if settings cannot be saved. */ }
        finally
        {
            _closed = true; _hotkeys?.Dispose(); _ocr.Dispose(); _client.Dispose(); _floating.CloseForShutdown(); _operation.Release(); _allowClose = true;
            // All awaited work can complete synchronously. Leave the first Closing event before closing again.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
    private static string SafeMessage(Exception error) => error switch
    {
        ArgumentException => error.Message,
        FileNotFoundException => "缺少本地识字模型，请按使用说明补齐 models 文件夹。",
        System.Security.Cryptography.CryptographicException => "Windows 无法保护或读取密钥，请在当前用户下重新填写；不会保存明文密钥。",
        _ => $"操作失败（{error.GetType().Name}）。请检查模型、选区、文件权限和连接设置后再试。"
    };
}
