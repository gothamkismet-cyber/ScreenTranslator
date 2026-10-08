using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ScreenTranslator.Capture;
using ScreenTranslator.Settings;
using ScreenTranslator.Translation;

namespace ScreenTranslator.Display;

public partial class SettingsWindow : Window
{
    private readonly SettingsBundle _draft;
    private readonly ApiTranslationClient _client = new();
    private readonly CancellationTokenSource _cancel = new();
    private ApiProfile? _editing;
    private bool _ready;
    private bool _closed;
    public SettingsBundle Result => _draft;

    public SettingsWindow(SettingsBundle current)
    {
        InitializeComponent();
        _draft = current.Copy();
        ProfilesList.ItemsSource = _draft.Settings.Profiles;
        OriginalCheck.IsChecked = _draft.Settings.ShowOriginal;
        FontSlider.Value = _draft.Settings.FloatingFontSize;
        OpacitySlider.Value = _draft.Settings.FloatingOpacity;
        SelectKeyBox.Text = _draft.Settings.SelectHotkey; PauseKeyBox.Text = _draft.Settings.PauseHotkey;
        FloatingKeyBox.Text = _draft.Settings.FloatingHotkey; RefreshKeyBox.Text = _draft.Settings.RefreshHotkey;
        SourceInitialized += (_, _) => ScreenCapture.ExcludeWindow(this);
        Closed += (_, _) => { _closed = true; _cancel.Cancel(); _client.Dispose(); };
        _ready = true;
        if (_draft.Settings.Profiles.Count == 0) AddProfile(new ApiProfile(), new ProfileSecrets());
        else ProfilesList.SelectedItem = _draft.Settings.Profiles.FirstOrDefault(p => p.Id == _draft.Settings.SelectedProfileId) ?? _draft.Settings.Profiles[0];
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        try { StoreForm(); }
        catch (ArgumentException error) { MessageBox.Show(this, error.Message, "请检查当前输入"); _ready = false; ProfilesList.SelectedItem = _editing; _ready = true; return; }
        LoadForm(ProfilesList.SelectedItem as ApiProfile);
    }

    private void LoadForm(ApiProfile? profile)
    {
        _ready = false; _editing = profile; FormPanel.IsEnabled = profile is not null;
        if (profile is not null)
        {
            var secret = _draft.Secrets.GetValueOrDefault(profile.Id) ?? new ProfileSecrets();
            NameBox.Text = profile.Name; FormatBox.SelectedIndex = (int)profile.Format; AddressModeBox.SelectedIndex = (int)profile.AddressMode;
            AddressBox.Text = profile.Address; AuthBox.SelectedIndex = (int)profile.Authentication; KeyHeaderBox.Text = profile.KeyHeader;
            KeyBox.Password = secret.ApiKey; ModelBox.Text = profile.Model; StreamBox.IsChecked = profile.Stream; TimeoutBox.Text = profile.TimeoutSeconds.ToString();
            LimitBox.SelectedIndex = (int)profile.OutputLimit; MaxOutputBox.Text = profile.MaxOutput.ToString();
            HeadersBox.Text = JsonSerializer.Serialize(secret.ExtraHeaders, new JsonSerializerOptions { WriteIndented = true }); BodyBox.Text = profile.ExtraBodyJson;
            TestText.Text = "";
        }
        _ready = true; UpdateEndpoint();
    }

    private (ApiProfile Profile, ProfileSecrets Secrets) ReadForm()
    {
        if (_editing is null) throw new ArgumentException("请先新增一个连接。");
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || !int.TryParse(MaxOutputBox.Text, out var max)) throw new ArgumentException("超时和输出数量需要填写整数。");
        Dictionary<string, string> headers;
        try { headers = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(HeadersBox.Text) ? "{}" : HeadersBox.Text) ?? []; }
        catch (JsonException) { throw new ArgumentException("额外请求头需要 JSON 对象，名称和值都应为字符串。"); }
        if (headers.Values.Any(value => value is null)) throw new ArgumentException("请求头的值不能为 null。");
        return (_editing with { Name = NameBox.Text.Trim(), Format = (ApiFormat)Math.Max(0, FormatBox.SelectedIndex), AddressMode = (AddressMode)Math.Max(0, AddressModeBox.SelectedIndex), Address = AddressBox.Text.Trim(), Authentication = (AuthMode)Math.Max(0, AuthBox.SelectedIndex), KeyHeader = KeyHeaderBox.Text.Trim(), Model = ModelBox.Text.Trim(), Stream = StreamBox.IsChecked == true, TimeoutSeconds = timeout, OutputLimit = (OutputLimitField)Math.Max(0, LimitBox.SelectedIndex), MaxOutput = max, ExtraBodyJson = BodyBox.Text }, new ProfileSecrets { ApiKey = KeyBox.Password, ExtraHeaders = headers });
    }

    private void StoreForm()
    {
        if (_editing is null) return;
        var (profile, secret) = ReadForm();
        var index = _draft.Settings.Profiles.FindIndex(p => p.Id == _editing.Id);
        if (index >= 0) _draft.Settings.Profiles[index] = profile;
        _draft.Secrets[profile.Id] = secret;
        _editing = profile;
    }

    private void AddProfile(ApiProfile profile, ProfileSecrets secret)
    {
        _ready = false; _draft.Settings.Profiles.Add(profile); _draft.Secrets[profile.Id] = secret;
        ProfilesList.Items.Refresh(); ProfilesList.SelectedItem = profile; _ready = true; LoadForm(profile);
    }
    private void Add_Click(object sender, RoutedEventArgs e) { try { StoreForm(); AddProfile(new ApiProfile(), new ProfileSecrets()); } catch (ArgumentException error) { TestText.Text = error.Message; } }
    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        try { StoreForm(); if (_editing is not null) { var copy = _draft.Copy(); AddProfile(_editing with { Id = Guid.NewGuid(), Name = _editing.Name + " 副本" }, copy.Secrets[_editing.Id]); } }
        catch (ArgumentException error) { TestText.Text = error.Message; }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        _ready = false; _draft.Settings.Profiles.RemoveAll(p => p.Id == _editing.Id); _draft.Secrets.Remove(_editing.Id);
        _editing = null; ProfilesList.Items.Refresh(); ProfilesList.SelectedIndex = _draft.Settings.Profiles.Count > 0 ? 0 : -1;
        _ready = true; LoadForm(ProfilesList.SelectedItem as ApiProfile);
    }
    private void Endpoint_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateEndpoint(); }
    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _editing is null) return;
        _editing.Name = NameBox.Text.Trim();
        _ready = false; ProfilesList.Items.Refresh(); ProfilesList.SelectedItem = _editing; _ready = true;
    }
    private void Address_Changed(object sender, TextChangedEventArgs e) { if (_ready) UpdateEndpoint(); }
    private void UpdateEndpoint()
    {
        if (_editing is null) { EndpointText.Text = "新增连接后填写地址。"; return; }
        try { var p = _editing with { Address = AddressBox.Text, Format = (ApiFormat)Math.Max(0, FormatBox.SelectedIndex), AddressMode = (AddressMode)Math.Max(0, AddressModeBox.SelectedIndex), Authentication = (AuthMode)Math.Max(0, AuthBox.SelectedIndex) }; EndpointText.Text = "最终请求：" + EndpointResolver.Display(EndpointResolver.Resolve(p)); }
        catch (ArgumentException error) { EndpointText.Text = error.Message; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false; TestText.Text = "正在发送测试短句…";
        try
        {
            var (profile, secret) = ReadForm();
            var result = await _client.TranslateAsync(profile, secret, "Do not delete this file. You have 250 gold coins.", Ocr.SourceLanguage.English, text => Dispatcher.InvokeAsync(() => { if (!_closed) TestText.Text = text; }), _cancel.Token);
            if (!_closed) TestText.Text = result.Text + $"\n{result.Elapsed.TotalMilliseconds:F0} 毫秒" + (result.Truncated ? " · 译文被截断，请调高输出限制再试。" : " · 已收到非空译文，请核对数字和“不要删除”的意思。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) TestText.Text = SafeMessage(error); }
        finally { if (!_closed) TestButton.IsEnabled = true; }
    }
    private async void Models_Click(object sender, RoutedEventArgs e)
    {
        ModelsButton.IsEnabled = false;
        try { var (profile, secret) = ReadForm(); var models = await _client.ListModelsAsync(profile with { Model = string.IsNullOrWhiteSpace(profile.Model) ? "listing-only" : profile.Model }, secret, _cancel.Token); if (!_closed) { var manual = ModelBox.Text; ModelBox.ItemsSource = models; ModelBox.Text = manual; TestText.Text = $"收到 {models.Length} 个模型名；也可以继续手填。"; } }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) TestText.Text = SafeMessage(error) + " 仍可手填模型。"; }
        finally { if (!_closed) ModelsButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StoreForm();
            foreach (var profile in _draft.Settings.Profiles) ProfileValidator.Validate(profile, _draft.Secrets.GetValueOrDefault(profile.Id) ?? new ProfileSecrets());
            _draft.Settings.SelectedProfileId = _editing?.Id;
            _draft.Settings.ShowOriginal = OriginalCheck.IsChecked == true; _draft.Settings.FloatingFontSize = FontSlider.Value; _draft.Settings.FloatingOpacity = OpacitySlider.Value;
            _draft.Settings.SelectHotkey = SelectKeyBox.Text.Trim(); _draft.Settings.PauseHotkey = PauseKeyBox.Text.Trim(); _draft.Settings.FloatingHotkey = FloatingKeyBox.Text.Trim(); _draft.Settings.RefreshHotkey = RefreshKeyBox.Text.Trim();
            HotkeyManager.Validate(_draft.Settings);
            DialogResult = true;
        }
        catch (ArgumentException error) { MessageBox.Show(this, error.Message, "请检查设置"); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try { StoreForm(); var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "翻译软件连接配置.json", Filter = "JSON 配置|*.json" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, SettingsStore.ExportWithoutSecrets(_draft.Settings)); }
        catch (Exception error) { MessageBox.Show(this, SafeMessage(error), "导出失败"); }
    }
    private static string SafeMessage(Exception error) => error is TranslationException or ArgumentException ? error.Message : $"操作失败（{error.GetType().Name}）。请检查网络、文件权限和连接设置。";
}
