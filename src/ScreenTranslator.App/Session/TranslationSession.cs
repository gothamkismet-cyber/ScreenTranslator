using System.Text;
using ScreenTranslator.Ocr;
using ScreenTranslator.Settings;
using ScreenTranslator.Translation;

namespace ScreenTranslator.Session;

public sealed record SessionView(long Epoch, long ContentId, bool Paused, string Original, string Translation, string Status, long Requests, long CacheHits, double? OcrMs, double? TranslationMs, TranslationErrorKind? Error = null);

public sealed class TranslationSession : IAsyncDisposable
{
    private sealed record Work(long Epoch, long ContentId, string Text, string Key, ApiProfile Profile, ProfileSecrets Secrets, SourceLanguage Language);
    private readonly object _gate = new();
    private readonly ITranslationService _translator;
    private readonly DiagnosticLog? _log;
    private readonly Dictionary<string, string> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private ApiProfile? _profile;
    private ProfileSecrets _secrets = new();
    private SourceLanguage _language;
    private Work? _pending;
    private CancellationTokenSource? _requestCancel;
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;
    private bool _paused = true;
    private bool _attempted;
    private int _sameSamples;
    private string _candidate = "";
    private string _translation = "";
    private string _status = "已暂停";
    private TranslationErrorKind? _error;
    private long _epoch;
    private long _contentId;
    private long _requests;
    private long _cacheHits;
    private double? _ocrMs;
    private double? _translationMs;
    public event Action<SessionView>? Changed;
    public TranslationSession(ITranslationService translator, DiagnosticLog? log = null) { _translator = translator; _log = log; }
    public bool Paused { get { lock (_gate) return _paused; } }
    public SessionView Current { get { lock (_gate) return View(); } }
    public bool IsCurrent(SessionView view) { lock (_gate) return view.Epoch == _epoch && view.ContentId == _contentId; }

    public void Configure(ApiProfile profile, ProfileSecrets secrets, SourceLanguage language)
    {
        ProfileValidator.Validate(profile, secrets);
        SessionView view;
        lock (_gate)
        {
            Invalidate(); _paused = true;
            _profile = profile with { }; _secrets = new ProfileSecrets { ApiKey = secrets.ApiKey, ExtraHeaders = new(secrets.ExtraHeaders, StringComparer.OrdinalIgnoreCase) }; _language = language;
            _cache.Clear(); _cacheOrder.Clear(); _candidate = ""; _translation = ""; _sameSamples = 0; _attempted = false; _error = null; _status = "已暂停，准备开始"; _ocrMs = null; _translationMs = null;
            view = View();
        }
        Changed?.Invoke(view);
    }
    public void Start()
    {
        SessionView view;
        lock (_gate)
        {
            if (_profile is null) throw new InvalidOperationException("请先配置 AI 连接。");
            if (!_paused) return;
            Invalidate(); _paused = false; _candidate = ""; _translation = ""; _sameSamples = 0; _attempted = false; _error = null;
            _cache.Clear(); _cacheOrder.Clear(); _status = "等待区域中的文字"; view = View();
        }
        Changed?.Invoke(view);
    }
    public void Pause(string status = "已暂停", bool clearContent = false)
    {
        SessionView view;
        lock (_gate)
        {
            Invalidate(); _paused = true; _status = status; _error = null;
            if (clearContent) { _candidate = ""; _translation = ""; _sameSamples = 0; _attempted = false; _ocrMs = null; _translationMs = null; _cache.Clear(); _cacheOrder.Clear(); }
            view = View();
        }
        Changed?.Invoke(view);
    }
    public void Observe(string rawText, double ocrMs = 0, bool force = false)
    {
        var text = rawText.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Trim();
        SessionView? view = null;
        lock (_gate)
        {
            if (_paused || _profile is null) return;
            _ocrMs = ocrMs;
            if (text != _candidate || force)
            {
                _contentId++; _requestCancel?.Cancel(); _pending = null;
                _candidate = text; _translation = ""; _translationMs = null; _sameSamples = 0; _attempted = false; _error = null;
                _status = text.Length == 0 ? "等待文字：当前区域没有识别到文字" : "正在确认文字是否稳定";
                view = View();
            }
            _sameSamples = Math.Min(_sameSamples + 1, 2);
            if (force) _sameSamples = 2;
            if (text.Length != 0 && _sameSamples >= 2 && !_attempted)
            {
                _attempted = true;
                var key = CacheKey(text);
                if (!force && _cache.TryGetValue(key, out var cached))
                {
                    _translation = cached; _cacheHits++; _status = "已更新 · 使用本次会话的缓存"; view = View();
                }
                else
                {
                    _pending = new Work(_epoch, _contentId, text, key, _profile, _secrets, _language);
                    _status = "等待翻译"; view = View();
                    if (!_workerRunning) { _workerRunning = true; _worker = Task.Run(WorkerAsync); }
                }
            }
        }
        if (view is not null) Changed?.Invoke(view);
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            Work work; CancellationTokenSource cancel; SessionView started;
            lock (_gate)
            {
                if (_pending is null || _paused) { _workerRunning = false; return; }
                work = _pending; _pending = null; cancel = new CancellationTokenSource(); _requestCancel = cancel;
                _requests++; _status = "正在翻译…"; started = View();
            }
            Changed?.Invoke(started);
            try
            {
                var result = await _translator.TranslateAsync(work.Profile, work.Secrets, work.Text, work.Language, text =>
                {
                    SessionView? view = null;
                    lock (_gate) if (CurrentWork(work)) { _translation = text; _status = "正在翻译…"; view = View(); }
                    if (view is not null) Changed?.Invoke(view);
                }, cancel.Token);
                SessionView? completed = null;
                lock (_gate)
                {
                    if (CurrentWork(work))
                    {
                        _translation = result.Text; _translationMs = result.Elapsed.TotalMilliseconds;
                        _status = result.Truncated ? "译文未完整 · 请提高输出限制后手动刷新" : "已更新";
                        if (!result.Truncated)
                        {
                            if (!_cache.ContainsKey(work.Key)) _cacheOrder.Enqueue(work.Key);
                            _cache[work.Key] = result.Text;
                            while (_cacheOrder.Count > 200) _cache.Remove(_cacheOrder.Dequeue());
                        }
                        completed = View();
                        _log?.Record("translation", result.Truncated ? "truncated" : "complete", _requests, result.Elapsed.TotalMilliseconds);
                    }
                }
                if (completed is not null) Changed?.Invoke(completed);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                SessionView? failed = null;
                lock (_gate)
                {
                    if (CurrentWork(work))
                    {
                        var known = error as TranslationException;
                        _error = known?.Kind ?? TranslationErrorKind.InvalidResponse;
                        if (!string.IsNullOrEmpty(known?.PartialText)) _translation = known.PartialText;
                        _status = known?.Message ?? $"翻译失败（{error.GetType().Name}），请检查设置后手动刷新。";
                        if (known?.StopSession == true) { Invalidate(); _paused = true; }
                        failed = View(); _log?.Record("translation", known?.Kind.ToString() ?? error.GetType().Name, _requests);
                    }
                }
                if (failed is not null) Changed?.Invoke(failed);
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_requestCancel, cancel)) _requestCancel = null;
                cancel.Dispose();
            }
        }
    }

    private bool CurrentWork(Work work) => !_paused && work.Epoch == _epoch && work.ContentId == _contentId;
    private void Invalidate() { _epoch++; _contentId++; _pending = null; _requestCancel?.Cancel(); }
    private string CacheKey(string text) => $"{_language}\u001f{_profile!.Id}\u001f{_profile.Model}\u001f{TranslationPrompt.Version}\u001fno-context\u001f{text}";
    private SessionView View() => new(_epoch, _contentId, _paused, _candidate, _translation, _status, _requests, _cacheHits, _ocrMs, _translationMs, _error);
    public async ValueTask DisposeAsync()
    {
        Pause(); Task worker; lock (_gate) worker = _worker;
        await worker;
    }
}
