using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTranslator.Ocr;
using SkiaSharp;

namespace ScreenTranslator.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--desktop")) return DesktopChecks.Run(Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "../../../../../")));
        if (args.Contains("--soak")) return SoakChecks.Run(Path.GetFullPath(args[1]), args.Length > 2 ? int.Parse(args[2]) : 1200);
        if (args.Contains("--exclusion")) return ExclusionChecks.Run(Path.GetFullPath(args[1]));
        if (args.Contains("--main-flow")) return MainFlowChecks.Run(Path.GetFullPath(args[1]));
        return RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        if (args.Contains("--ocr")) return await RunOcrAsync(root);
        if (args.Contains("--protocol")) return await ProtocolChecks.RunAsync(root);
        if (args.Contains("--session")) return await SessionChecks.RunAsync(root);
        Console.WriteLine("Use --ocr <project-root> to run the three-language OCR evidence check.");
        return 0;
    }

    private static async Task<int> RunOcrAsync(string root)
    {
        var output = Path.Combine(root, "artifacts", "verification", "ocr");
        Directory.CreateDirectory(output);
        using var ocr = new OcrService(Path.Combine(root, "models", "screen-ocr"), Path.Combine(root, ".tools", "test-model-cache"));
        var results = new List<object>();
        var summaries = new List<object>();
        var passed = true;
        foreach (var language in Enum.GetValues<SourceLanguage>())
        {
            var texts = Samples(language);
            var totalDistance = 0;
            var totalCharacters = 0;
            for (var index = 0; index < texts.Length; index++)
            {
                var difficult = index >= 6;
                var name = $"{language.ToString().ToLowerInvariant()}-{index + 1:00}";
                using var image = RenderSample(texts[index], language, difficult, index);
                using (var file = File.Create(Path.Combine(output, name + ".png"))) image.Encode(file, SKEncodedImageFormat.Png, 100);
                var result = await ocr.ReadAsync(image, language);
                var expected = Normalize(texts[index]);
                var actual = Normalize(result.Text);
                var distance = Distance(expected, actual);
                var characters = expected.EnumerateRunes().Count();
                if (!difficult) { totalDistance += distance; totalCharacters += characters; }
                results.Add(new { sample = name, language = language.ToString(), kind = difficult ? "difficult" : "clear", expected = texts[index], actual = result.Text, distance, characters, cer = (double)distance / Math.Max(1, characters), elapsedMs = result.Elapsed.TotalMilliseconds, lineCount = result.LineCount, provenance = "Original synthetic text authored for this project; rendered locally with installed Windows fonts" });
                Console.WriteLine($"{name}: CER={(double)distance / Math.Max(1, characters):P1}; {result.Elapsed.TotalMilliseconds:F0} ms; {result.Text}");
            }
            var cer = (double)totalDistance / Math.Max(1, totalCharacters);
            summaries.Add(new { language = language.ToString(), clearSamples = 6, difficultSamples = 4, clearCer = cer, target = 0.05, passed = cer <= 0.05 });
            passed &= cer <= 0.05;
        }
        var report = new { test = "A01 synthetic OCR; not a real-game compatibility claim", utcTime = DateTimeOffset.UtcNow, normalization = "Unicode Form C; remove whitespace only; retain punctuation, case, numbers and negations; Levenshtein distance over Unicode scalars", models = "PP-OCRv5 mobile; fixed URLs and SHA256 in models/screen-ocr/manifest.json", processorCount = Environment.ProcessorCount, workingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64, summaries, results, passed };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine(passed ? "OCR clear-sample targets passed." : "OCR clear-sample targets NOT met. See report.json.");
        return passed ? 0 : 1;
    }

    private static SKBitmap RenderSample(string text, SourceLanguage language, bool difficult, int index)
    {
        var image = new SKBitmap(1100, 160);
        using var canvas = new SKCanvas(image);
        canvas.Clear(difficult && index % 2 == 0 ? new SKColor(27, 35, 55) : SKColors.White);
        using var face = SKTypeface.FromFamilyName(language switch { SourceLanguage.Korean => "Malgun Gothic", SourceLanguage.Japanese => "Yu Gothic", _ => "Arial" });
        using var font = new SKFont(face, difficult ? 24 : 36);
        using var paint = new SKPaint { Color = difficult && index % 2 == 0 ? new SKColor(190, 204, 225) : SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, 32, 94, font, paint);
        return image;
    }

    internal static string[] Samples(SourceLanguage language) => language switch
    {
        SourceLanguage.English => ["Please open the door.", "The meeting starts at 10:30.", "Do not delete this file.", "You have 250 gold coins.", "Save your progress before leaving.", "The train arrives in 5 minutes.", "Press Enter to continue.", "Connection lost. Please try again.", "The price is 19.99 dollars.", "Find the key near the old bridge."],
        SourceLanguage.Japanese => ["ドアを開けてください。", "会議は午前十時に始まります。", "このファイルを削除しないでください。", "金貨を250枚持っています。", "終了する前に保存してください。", "電車は五分後に到着します。", "次に進むにはボタンを押してください。", "接続が切れました。もう一度試してください。", "お店は午後六時に閉まります。", "古い橋の近くで鍵を探してください。"],
        _ => ["문을 열어 주세요.", "회의는 오전 열 시에 시작합니다.", "이 파일을 삭제하지 마세요.", "금화 250개를 가지고 있습니다.", "종료하기 전에 저장해 주세요.", "기차는 오 분 후에 도착합니다.", "계속하려면 버튼을 누르세요.", "연결이 끊겼습니다. 다시 시도하세요.", "가게는 오후 여섯 시에 문을 닫습니다.", "오래된 다리 근처에서 열쇠를 찾으세요."]
    };

    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormC), @"\s+", "");
    private static int Distance(string first, string second)
    {
        var a = first.EnumerateRunes().ToArray(); var b = second.EnumerateRunes().ToArray();
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (var j = 1; j <= b.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
