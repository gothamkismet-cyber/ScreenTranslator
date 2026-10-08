using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace ScreenTranslator.Ocr;

public enum SourceLanguage { English, Japanese, Korean }
public record OcrReading(string Text, TimeSpan Elapsed, int LineCount);

public sealed class OcrService : IDisposable
{
    private readonly string _models;
    private readonly string _dictionaryCache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RapidOcr? _engine;
    private SourceLanguage? _loaded;
    public OcrService(string? models = null, string? dictionaryCache = null)
    {
        _models = models ?? Path.Combine(AppContext.BaseDirectory, "models", "screen-ocr");
        _dictionaryCache = dictionaryCache ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenAiTranslator", "model-cache");
    }

    public async Task<OcrReading> ReadAsync(SKBitmap image, SourceLanguage language, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                EnsureEngine(language);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var result = _engine!.Detect(image, RapidOcrOptions.Default with { DoAngle = false });
                token.ThrowIfCancellationRequested();
                return new OcrReading((result.StrRes ?? "").Trim(), timer.Elapsed, result.TextBlocks.Length);
            }, token);
        }
        finally { _gate.Release(); }
    }

    private void EnsureEngine(SourceLanguage language)
    {
        if (_loaded == language && _engine is not null) return;
        var recognition = language switch { SourceLanguage.English => "en-rec.onnx", SourceLanguage.Japanese => "ja-rec.onnx", _ => "ko-rec.onnx" };
        var det = Path.Combine(_models, "det.onnx");
        var cls = Path.Combine(_models, "cls.onnx");
        var rec = Path.Combine(_models, recognition);
        foreach (var file in new[] { det, cls, rec })
            if (!File.Exists(file)) throw new FileNotFoundException("缺少本地识字模型，请按使用说明准备 models 文件夹。", file);
        Directory.CreateDirectory(_dictionaryCache);
        var modelHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rec)));
        var dictionary = Path.Combine(_dictionaryCache, modelHash + ".dict.txt");
        if (!File.Exists(dictionary))
        {
            using var metadataSession = new InferenceSession(rec);
            if (!metadataSession.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var characters) || string.IsNullOrEmpty(characters))
                throw new InvalidOperationException("识字模型缺少配套字符表，无法安全加载。");
            File.WriteAllText(dictionary, characters, new System.Text.UTF8Encoding(false));
        }
        _engine?.Dispose();
        _engine = null;
        _loaded = null;
        var next = new RapidOcr();
        try
        {
            next.InitModels(det, cls, rec, dictionary, numThread: Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            _engine = next;
            _loaded = language;
        }
        catch { next.Dispose(); throw; }
    }

    public void Dispose() { _engine?.Dispose(); _gate.Dispose(); }
}
