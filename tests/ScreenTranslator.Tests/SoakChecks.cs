using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenTranslator.Capture;
using ScreenTranslator.Ocr;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;
using ScreenTranslator.Translation;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenTranslator.Tests;

internal static class SoakChecks
{
    public static int Run(string root, int seconds = 1200)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var text = new TextBlock { FontSize = 26, FontFamily = new FontFamily("Microsoft YaHei UI"), Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        var source = new Window { Title = "本项目持续捕获测试 · 关闭可中止", Width = 660, Height = 195, Left = 850, Top = 15, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Background = Brushes.White, Content = text };
        var cancel = new CancellationTokenSource(); source.Closed += (_, _) => cancel.Cancel();
        var started = false;
        source.ContentRendered += async (_, _) =>
        {
            if (started) return; started = true;
            var output = Path.Combine(root, "artifacts", "verification", "soak"); Directory.CreateDirectory(output);
            var metrics = new List<object>(); var trials = new List<object>(); var failures = new List<object>();
            var timer = Stopwatch.StartNew(); var process = Process.GetCurrentProcess(); var previousCpu = process.TotalProcessorTime; var previousTime = 0d;
            long captures = 0; var activeOcr = 0; var maximumOcr = 0; var scene = -1; var language = SourceLanguage.English; var profile = new ApiProfile(); var nextScene = 0d; var stableAt = 0d; var sampled = ""; var sampleCount = 0; var recorded = false;
            try
            {
                await using var server = new LoopbackServer(); using var client = new ApiTranslationClient();
                await using var session = new TranslationSession(client);
                using var ocr = new OcrService(Path.Combine(root, "models", "screen-ocr"), Path.Combine(root, ".tools", "test-model-cache"));
                while (timer.Elapsed.TotalSeconds < seconds && !cancel.IsCancellationRequested)
                {
                    var cycle = Stopwatch.StartNew();
                    if (timer.Elapsed.TotalSeconds >= nextScene)
                    {
                        scene++; var nextLanguage = (SourceLanguage)((scene / 10) % 3);
                        if (scene == 0 || nextLanguage != language)
                        {
                            language = nextLanguage;
                            profile = new ApiProfile { Name = "仅本机模拟", Address = server.BaseAddress, Model = "local-fixture-no-ai", Authentication = AuthMode.None, Format = language == SourceLanguage.Japanese ? ApiFormat.Responses : ApiFormat.ChatCompletions, Stream = true };
                            session.Configure(profile, new ProfileSecrets(), language); session.Start();
                        }
                        text.Text = Program.Samples(language)[scene % 10]; source.UpdateLayout();
                        nextScene = timer.Elapsed.TotalSeconds + (scene < 30 ? 3 : 90);
                        sampleCount = 0; sampled = ""; recorded = false;
                        await Task.Delay(100, cancel.Token);
                    }
                    var start = text.PointToScreen(new System.Windows.Point(0, 0)); var end = text.PointToScreen(new System.Windows.Point(text.ActualWidth, text.ActualHeight));
                    var region = Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
                    using var image = ScreenCapture.Capture(region);
                    activeOcr++; maximumOcr = Math.Max(maximumOcr, activeOcr);
                    OcrReading reading; try { reading = await ocr.ReadAsync(image, language, cancel.Token); } finally { activeOcr--; }
                    captures++;
                    if (reading.Text == sampled) sampleCount++; else { sampled = reading.Text; sampleCount = 1; }
                    if (sampleCount == 2) stableAt = timer.Elapsed.TotalMilliseconds;
                    session.Observe(reading.Text, reading.Elapsed.TotalMilliseconds);
                    var view = session.Current;
                    if (!recorded && view.TranslationMs is not null && view.Translation.Length > 0)
                    {
                        var matched = string.Concat(reading.Text.Where(c => !char.IsWhiteSpace(c))) == string.Concat(text.Text.Where(c => !char.IsWhiteSpace(c)));
                        var correctAssociation = view.Translation == LoopbackServer.Expected(view.Original);
                        trials.Add(new { scene, language = language.ToString(), format = profile.Format.ToString(), expected = text.Text, actual = reading.Text, matched, correctAssociation, ocrMs = reading.Elapsed.TotalMilliseconds, apiMs = view.TranslationMs, observedCompleteMs = timer.Elapsed.TotalMilliseconds - stableAt });
                        if (!matched || !correctAssociation) failures.Add(new { scene, kind = "OCR or result association mismatch" });
                        recorded = true;
                    }
                    if (metrics.Count == 0 || timer.Elapsed.TotalSeconds - previousTime >= 60)
                    {
                        process.Refresh(); var cpu = process.TotalProcessorTime;
                        var cpuPercent = (cpu - previousCpu).TotalSeconds / Math.Max(.001, timer.Elapsed.TotalSeconds - previousTime) / Environment.ProcessorCount * 100;
                        metrics.Add(new { elapsedSeconds = timer.Elapsed.TotalSeconds, captures, requests = server.Requests, workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, normalizedCpuPercent = cpuPercent, maxConcurrentOcr = maximumOcr, dpiScale = VisualTreeHelper.GetDpi(source).DpiScaleX });
                        previousTime = timer.Elapsed.TotalSeconds; previousCpu = cpu;
                        var progress = JsonSerializer.Serialize(new { elapsedSeconds = timer.Elapsed.TotalSeconds, requestedSeconds = seconds, captures, requests = server.Requests, workingSetBytes = process.WorkingSet64 });
                        File.WriteAllText(Path.Combine(output, "progress.json"), progress);
                        Console.WriteLine(progress);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 750 - cycle.Elapsed.TotalMilliseconds)), cancel.Token);
                }
                var complete = timer.Elapsed.TotalSeconds >= seconds;
                var localLatencies = trials.Take(30).Select(row => (double)row.GetType().GetProperty("observedCompleteMs")!.GetValue(row)!).Order().ToArray();
                var report = new { test = "Real Windows capture + local OCR + real loopback HTTP; translation content is a simulated fixture, not AI", utcTime = DateTimeOffset.UtcNow, requestedSeconds = seconds, elapsedSeconds = timer.Elapsed.TotalSeconds, passed = complete && failures.Count == 0 && maximumOcr == 1 && trials.Count >= 30, captures, requests = server.Requests, maximumOcr, mockLatencyP95Ms = localLatencies.Length == 0 ? 0 : localLatencies[(int)Math.Ceiling(localLatencies.Length * .95) - 1], hardware = new { processorCount = Environment.ProcessorCount, os = Environment.OSVersion.ToString() }, realAiPerformance = "pending", actualGameCompatibility = "pending", metrics, trials, failures };
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                application.Shutdown(report.passed ? 0 : 1);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new { elapsedSeconds = timer.Elapsed.TotalSeconds, captures, error = error.GetType().Name, message = error.Message }));
                application.Shutdown(1);
            }
            finally { source.Close(); cancel.Dispose(); }
        };
        source.Show(); return application.Run();
    }
}
