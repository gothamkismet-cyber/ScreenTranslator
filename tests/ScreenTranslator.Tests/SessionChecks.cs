using System.Diagnostics;
using System.Text.Json;
using ScreenTranslator.Ocr;
using ScreenTranslator.Session;
using ScreenTranslator.Settings;
using ScreenTranslator.Translation;

namespace ScreenTranslator.Tests;

internal static class SessionChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var rows = new List<object>(); var failures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); rows.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; rows.Add(new { name, passed = false, error = error.GetType().Name, message = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        var profile = new ApiProfile { Name = "模拟", Address = "http://127.0.0.1:3210/v1", Model = "test-model", Authentication = AuthMode.None };
        TranslationSession Make(ITranslationService service) { var session = new TranslationSession(service); session.Configure(profile, new ProfileSecrets(), SourceLanguage.English); session.Start(); return session; }
        void Stable(TranslationSession session, string text) { session.Observe(text); session.Observe(text); }
        await Check("latest pending only; one active request", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ScriptedService(async (text, update, token) => { if (text == "A") { await release.Task; update("old A delta"); } return Result("中文 " + text); });
            await using var session = Make(service); Stable(session, "A"); await Until(() => service.Calls.Count == 1); Stable(session, "B"); Stable(session, "C"); release.SetResult(); await Until(() => session.Current.Translation == "中文 C");
            ProtocolChecks.Assert(service.Calls.SequenceEqual(new[] { "A", "C" }) && service.MaxConcurrent == 1);
        });
        await Check("stale delta final and UI snapshot do not overwrite B", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ScriptedService(async (text, update, token) => { if (text == "A") { await release.Task; update("wrong A"); } return Result("中文 " + text); });
            await using var session = Make(service); Stable(session, "A"); await Until(() => service.Calls.Count == 1); var old = session.Current; Stable(session, "B"); release.SetResult(); await Until(() => session.Current.Translation == "中文 B"); ProtocolChecks.Assert(!session.IsCurrent(old) && session.Current.Original == "B");
        });
        await Check("stale error cannot pause new content", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ScriptedService(async (text, update, token) => { if (text == "A") { await release.Task; throw new TranslationException(TranslationErrorKind.Authentication, "old authentication error"); } return Result("中文 B"); });
            await using var session = Make(service); Stable(session, "A"); await Until(() => service.Calls.Count == 1); Stable(session, "B"); release.SetResult(); await Until(() => session.Current.Translation == "中文 B"); ProtocolChecks.Assert(!session.Paused && session.Current.Error is null);
        });
        await Check("blank clears and discards in-flight output", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ScriptedService(async (text, update, token) => { await release.Task; update("old"); return Result("old"); });
            await using var session = Make(service); Stable(session, "A"); await Until(() => service.Calls.Count == 1); session.Observe(""); release.SetResult(); await Task.Delay(80); ProtocolChecks.Assert(session.Current.Original == "" && session.Current.Translation == "" && service.Calls.Count == 1);
        });
        await Check("pause blocks old updates and new requests", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ScriptedService(async (text, update, token) => { await release.Task; update("old"); return Result("old"); });
            await using var session = Make(service); Stable(session, "A"); await Until(() => service.Calls.Count == 1); session.Pause(); Stable(session, "B"); release.SetResult(); await Task.Delay(80); ProtocolChecks.Assert(session.Paused && session.Current.Translation == "" && service.Calls.Count == 1);
        });
        await Check("profile model language changes reset cache", async () =>
        {
            var service = new ScriptedService((text, update, token) => Task.FromResult(Result("译文")));
            await using var session = Make(service); Stable(session, "250 files, do not delete."); await Until(() => service.Calls.Count == 1 && session.Current.Translation == "译文"); session.Configure(profile with { Model = "other-model" }, new ProfileSecrets(), SourceLanguage.Japanese); session.Start(); Stable(session, "250 files, do not delete."); await Until(() => service.Calls.Count == 2 && session.Current.Translation == "译文"); ProtocolChecks.Assert(session.Current.CacheHits == 0);
        });
        await Check("changing region clears visible content and cached context", async () =>
        {
            var service = new ScriptedService((text, update, token) => Task.FromResult(Result("旧区域的译文")));
            await using var session = Make(service); Stable(session, "old region"); await Until(() => session.Current.Translation.Length != 0);
            session.Pause("选区已改变", clearContent: true); ProtocolChecks.Assert(session.Paused && session.Current.Original.Length == 0 && session.Current.Translation.Length == 0);
        });
        await Check("cache reuse and explicit manual bypass", async () =>
        {
            var service = new ScriptedService((text, update, token) => Task.FromResult(Result("中文 " + text)));
            await using var session = Make(service); Stable(session, "A"); await Until(() => session.Current.Translation == "中文 A"); Stable(session, "B"); await Until(() => session.Current.Translation == "中文 B"); Stable(session, "A"); ProtocolChecks.Assert(session.Current.CacheHits == 1 && service.Calls.Count == 2); session.Observe("A", force: true); await Until(() => service.Calls.Count == 3 && session.Current.Translation == "中文 A");
        });
        await Check("punctuation numbers negation retained in dedup", async () =>
        {
            var service = new ScriptedService((text, update, token) => Task.FromResult(Result(text)));
            await using var session = Make(service); foreach (var text in new[] { "Delete 250 files.", "Do not delete 250 files.", "Do not delete 251 files.", "Do not delete 251 files!" }) { Stable(session, text); await Until(() => session.Current.Translation == text); } ProtocolChecks.Assert(service.Calls.Count == 4);
        });
        await Check("truncated results are not cached", async () =>
        {
            var service = new ScriptedService((text, update, token) => Task.FromResult(Result(text, text == "A")));
            await using var session = Make(service); Stable(session, "A"); await Until(() => session.Current.Translation == "A"); Stable(session, "B"); await Until(() => session.Current.Translation == "B"); Stable(session, "A"); await Until(() => service.Calls.Count == 3 && session.Current.Translation == "A"); ProtocolChecks.Assert(session.Current.CacheHits == 0);
        });
        await Check("failed same content does not loop; manual can retry", async () =>
        {
            var service = new ScriptedService((text, update, token) => throw new TranslationException(TranslationErrorKind.Network, "simulated network failure"));
            await using var session = Make(service); Stable(session, "A"); await Until(() => session.Current.Error is not null); for (var i = 0; i < 100; i++) session.Observe("A"); await Task.Delay(50); ProtocolChecks.Assert(service.Calls.Count == 1); session.Observe("A", force: true); await Until(() => service.Calls.Count == 2);
        });
        await Check("current authentication error auto-pauses", async () =>
        {
            var service = new ScriptedService((text, update, token) => throw new TranslationException(TranslationErrorKind.Authentication, "simulated auth failure"));
            await using var session = Make(service); Stable(session, "A"); await Until(() => session.Paused); Stable(session, "B"); ProtocolChecks.Assert(service.Calls.Count == 1 && session.Current.Error == TranslationErrorKind.Authentication);
        });
        await Check("constant text for 60 real seconds sends once", async () =>
        {
            var service = new ScriptedService((text, update, token) => Task.FromResult(Result("请不要删除 250 个文件。")));
            await using var session = Make(service); Stable(session, "Do not delete 250 files."); await Until(() => session.Current.Translation.Length != 0);
            var timer = Stopwatch.StartNew(); while (timer.Elapsed < TimeSpan.FromSeconds(60)) { session.Observe("Do not delete 250 files."); await Task.Delay(750); }
            ProtocolChecks.Assert(service.Calls.Count == 1); Console.WriteLine($"Constant-text observation {timer.Elapsed.TotalSeconds:F1} seconds; one request.");
        });
        var output = Path.Combine(root, "artifacts", "verification", "session"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "Session concurrency, cache and cancellation using controlled translator; no external AI", utcTime = DateTimeOffset.UtcNow, passed = failures == 0, total = rows.Count, failures, rows }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static TranslationResult Result(string text, bool truncated = false) => new(text, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), truncated);
    public static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Controlled test did not reach its expected state."); await Task.Delay(10); }
    }
    private sealed class ScriptedService(Func<string, Action<string>, CancellationToken, Task<TranslationResult>> script) : ITranslationService
    {
        private int _active;
        public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();
        public int MaxConcurrent { get; private set; }
        public async Task<TranslationResult> TranslateAsync(ApiProfile profile, ProfileSecrets secrets, string original, SourceLanguage language, Action<string> onText, CancellationToken token)
        {
            Calls.Enqueue(original); var active = Interlocked.Increment(ref _active); MaxConcurrent = Math.Max(MaxConcurrent, active);
            try { return await script(original, onText, token); } finally { Interlocked.Decrement(ref _active); }
        }
    }
}
