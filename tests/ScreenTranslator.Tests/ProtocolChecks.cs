using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenTranslator.Ocr;
using ScreenTranslator.Settings;
using ScreenTranslator.Translation;

namespace ScreenTranslator.Tests;

internal static class ProtocolChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var rows = new List<object>();
        var failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); rows.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; rows.Add(new { name, passed = false, error = error.GetType().Name, message = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        var secrets = new ProfileSecrets { ApiKey = "test-secret-never-log-9173", ExtraHeaders = new() { ["x-custom-token"] = "header-secret-never-log-2819" } };
        ApiProfile Profile(ApiFormat format = ApiFormat.ChatCompletions, bool stream = false) => new() { Name = "测试连接", Address = "https://example.test/proxy/v1/", Model = "hand-entered-model", Format = format, Stream = stream };
        await Check("base address retains prefix and existing v1", () => Sync(() => Assert(EndpointResolver.Resolve(Profile()).AbsoluteUri == "https://example.test/proxy/v1/chat/completions")));
        await Check("no v1 is inserted", () => Sync(() => Assert(EndpointResolver.Resolve(Profile() with { Address = "https://example.test/api" }).AbsoluteUri == "https://example.test/api/chat/completions")));
        await Check("Responses URL", () => Sync(() => Assert(EndpointResolver.Resolve(Profile(ApiFormat.Responses)).AbsoluteUri.EndsWith("/proxy/v1/responses"))));
        await Check("full URL used exactly and query display redacted", () => Sync(() => { var p = Profile() with { AddressMode = AddressMode.Full, Address = "https://example.test/a/b?key=test-query-value" }; var uri = EndpointResolver.Resolve(p); Assert(uri.AbsoluteUri == p.Address); Assert(!EndpointResolver.Display(uri).Contains("test-query-value")); }));
        await Check("remote HTTP rejected", () => Sync(() => Throws<ArgumentException>(() => EndpointResolver.Resolve(Profile() with { Address = "http://example.test" }))));
        await Check("loopback HTTP no auth accepted", () => Sync(() => Assert(EndpointResolver.Resolve(Profile() with { Address = "http://127.0.0.1:3210/v1", Authentication = AuthMode.None }).Scheme == "http")));
        await Check("IPv6 loopback accepted", () => Sync(() => Assert(EndpointResolver.Resolve(Profile() with { Address = "http://[::1]:3210/v1", Authentication = AuthMode.None }).IsLoopback)));
        await Check("remote no-auth rejected", () => Sync(() => Throws<ArgumentException>(() => EndpointResolver.Resolve(Profile() with { Authentication = AuthMode.None }))));
        await Check("query in base address rejected", () => Sync(() => Throws<ArgumentException>(() => EndpointResolver.Resolve(Profile() with { Address = "https://example.test/?api_key=x" }))));
        await Check("core JSON cannot be overridden", () => Sync(() => Throws<ArgumentException>(() => ProfileValidator.Validate(Profile() with { ExtraBodyJson = "{\"input\":\"wrong\"}" }, secrets))));
        await Check("headers cannot override auth or contain newline", () => Sync(() => { Throws<ArgumentException>(() => ProfileValidator.Validate(Profile(), new ProfileSecrets { ApiKey = "x", ExtraHeaders = new() { ["authorization"] = "secret" } })); Throws<ArgumentException>(() => ProfileValidator.Validate(Profile(), new ProfileSecrets { ApiKey = "x\r\ny" })); }));
        await Check("optional parameters omitted by default", () => Sync(() => { var body = ApiTranslationClient.BuildBody(Profile(), "Do not delete 250 files.", SourceLanguage.English); Assert(!body.ContainsKey("temperature") && !body.ContainsKey("max_tokens") && !body.ContainsKey("max_completion_tokens")); Assert(body["messages"]![1]!["content"]!.GetValue<string>() == "Do not delete 250 files."); }));
        await Check("declared output parameter emitted", () => Sync(() => Assert(ApiTranslationClient.BuildBody(Profile(ApiFormat.Responses) with { OutputLimit = OutputLimitField.MaxOutputTokens, MaxOutput = 200 }, "x", SourceLanguage.Japanese)["max_output_tokens"]!.GetValue<int>() == 200)));
        await Check("Chat full response", async () => { var handler = new FixtureHandler(Json("{\"choices\":[{\"message\":{\"content\":\"请勿删除 250 个文件。\"},\"finish_reason\":\"stop\"}]}")); using var client = new ApiTranslationClient(handler); var r = await client.TranslateAsync(Profile(), secrets, "Do not delete 250 files.", SourceLanguage.English, _ => { }, default); Assert(r.Text == "请勿删除 250 个文件。" && !r.Truncated); Assert(handler.LastBody!.Contains("hand-entered-model")); Assert(handler.AuthHeaderPresent); });
        await Check("Responses full output array and ignored non-text", async () => { using var client = new ApiTranslationClient(new FixtureHandler(Json("{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\"},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"第一行\"},{\"type\":\"output_text\",\"text\":\"第二行\"}]}]}"))); var r = await client.TranslateAsync(Profile(ApiFormat.Responses), secrets, "x", SourceLanguage.Korean, _ => { }, default); Assert(r.Text == "第一行\n第二行"); });
        await Check("Chat SSE fragmented UTF8, empty delta, usage event", async () => { var sse = ": keepalive\r\n\r\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}\r\n\r\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"中文日本語한글\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"total_tokens\":8}}\n\ndata: [DONE]\n\n"; using var client = new ApiTranslationClient(new FixtureHandler(Sse(sse))); var updates = new List<string>(); var r = await client.TranslateAsync(Profile(stream: true), secrets, "x", SourceLanguage.English, updates.Add, default); Assert(r.Text == "中文日本語한글" && updates.Last() == r.Text); });
        await Check("Responses SSE delta and completed", async () => { var sse = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"不要删除。\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"不要删除。\"}]}]}}\n\n"; using var client = new ApiTranslationClient(new FixtureHandler(Sse(sse))); var r = await client.TranslateAsync(Profile(ApiFormat.Responses, true), secrets, "x", SourceLanguage.English, _ => { }, default); Assert(r.Text == "不要删除。"); });
        await Check("Chat length reports truncation", async () => { using var client = new ApiTranslationClient(new FixtureHandler(Json("{\"choices\":[{\"message\":{\"content\":\"一部分\"},\"finish_reason\":\"length\"}]}"))); Assert((await client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default)).Truncated); });
        await Check("Responses incomplete reports truncation", async () => { using var client = new ApiTranslationClient(new FixtureHandler(Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"一部分\"}\n\ndata: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"output\":[]}}\n\n"))); Assert((await client.TranslateAsync(Profile(ApiFormat.Responses, true), secrets, "x", SourceLanguage.English, _ => { }, default)).Truncated); });
        await Check("partial EOF is failure with no retry", async () => { var h = new FixtureHandler(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"部分\"}}]}\n\n")); using var client = new ApiTranslationClient(h); var e = await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(stream: true), secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(e.Kind == TranslationErrorKind.Incomplete && e.PartialText == "部分" && h.Count == 1); });
        await Check("Responses failed event does not pass", async () => { using var client = new ApiTranslationClient(new FixtureHandler(Sse("data: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\"}}\n\n"))); await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(ApiFormat.Responses, true), secrets, "x", SourceLanguage.English, _ => { }, default)); });
        await Check("empty response rejected", async () => { using var client = new ApiTranslationClient(new FixtureHandler(Json("{\"choices\":[]}"))); var e = await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(e.Kind == TranslationErrorKind.Empty); });
        await Check("refusal rejected", async () => { using var client = new ApiTranslationClient(new FixtureHandler(Json("{\"choices\":[{\"message\":{\"refusal\":\"No\"},\"finish_reason\":\"stop\"}]}"))); Assert((await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default))).Kind == TranslationErrorKind.Refusal); });
        await Check("401 stops and sanitizes echoed secret", async () => { var h = new FixtureHandler(Json("{\"error\":{\"message\":\"" + secrets.ApiKey + "\"}}", 401)); using var client = new ApiTranslationClient(h); var e = await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(e.StopSession && !e.Message.Contains(secrets.ApiKey) && h.Count == 1); });
        await Check("quota error stops without retry", async () => { var h = new FixtureHandler(Json("{\"error\":{\"code\":\"insufficient_quota\"}}", 429)); using var client = new ApiTranslationClient(h); var e = await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(e.Kind == TranslationErrorKind.Quota && h.Count == 1); });
        await Check("429 retry bounded to one", async () => { var h = new FixtureHandler(Json("{}", 429), Json("{}", 429), Json("{\"choices\":[{\"message\":{\"content\":\"wrong third attempt\"}}]}")); using var client = new ApiTranslationClient(h); await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(h.Count == 2); });
        await Check("redirect never followed", async () => { var response = Json("{}", 307); response.Headers.Location = new Uri("https://other.test/stolen"); var h = new FixtureHandler(response); using var client = new ApiTranslationClient(h); var e = await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(e.Kind == TranslationErrorKind.Redirect && h.Count == 1); });
        await Check("timeout has no blind retry", async () => { var h = new FixtureHandler { WaitForever = true }; using var client = new ApiTranslationClient(h); var e = await ThrowsAsync<TranslationException>(() => client.TranslateAsync(Profile() with { TimeoutSeconds = 5 }, secrets, "x", SourceLanguage.English, _ => { }, default)); Assert(e.Kind == TranslationErrorKind.Timeout && h.Count == 1); });
        await Check("cancel during retry prevents second request", async () => { var h = new FixtureHandler(Json("{}", 429)); using var client = new ApiTranslationClient(h); using var cancel = new CancellationTokenSource(80); await ThrowsAsync<OperationCanceledException>(() => client.TranslateAsync(Profile(), secrets, "x", SourceLanguage.English, _ => { }, cancel.Token)); Assert(h.Count == 1); });
        await Check("DPAPI roundtrip two profiles restart export delete", () => Sync(() => { var folder = Path.Combine(root, ".tools", "verification-settings"); var store = new SettingsStore(folder); var one = Profile(); var two = Profile(ApiFormat.Responses) with { Model = "second-manual-model", Address = "https://second.test/v1" }; var bundle = new SettingsBundle(new AppSettings { Profiles = [one, two], SelectedProfileId = two.Id }, new() { [one.Id] = secrets, [two.Id] = new() { ApiKey = "second-test-secret" } }); store.Save(bundle); var restart = new SettingsStore(folder).Load(); Assert(restart.Settings.Profiles.Count == 2 && restart.Settings.SelectedProfileId == two.Id && restart.Secrets[one.Id].ApiKey == secrets.ApiKey); foreach (var file in Directory.GetFiles(folder)) Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(secrets.ApiKey)); var exported = SettingsStore.ExportWithoutSecrets(restart.Settings); Assert(!exported.Contains(secrets.ApiKey) && !exported.Contains(secrets.ExtraHeaders.Values.First())); restart.Settings.Profiles.RemoveAll(p => p.Id == one.Id); store.Save(restart); Assert(!store.Load().Secrets.ContainsKey(one.Id)); }));
        await Check("protection failure writes no plaintext or config", () => Sync(() => { var folder = Path.Combine(root, ".tools", "verification-protection-failure"); var store = new SettingsStore(folder, new FailingProtector()); var p = Profile(); Throws<System.Security.Cryptography.CryptographicException>(() => store.Save(new SettingsBundle(new AppSettings { Profiles = [p] }, new() { [p.Id] = secrets }))); Assert(!File.Exists(Path.Combine(folder, "settings.json")) && !File.Exists(Path.Combine(folder, "credentials.dat"))); }));
        await Check("full URL query protected at rest and excluded from export", () => Sync(() => { var p = Profile() with { AddressMode = AddressMode.Full, Address = "https://example.test/full?access_token=query-private-6152" }; var store = new SettingsStore(Path.Combine(root, ".tools", "verification-query")); store.Save(new SettingsBundle(new AppSettings { Profiles = [p] }, new() { [p.Id] = secrets })); var read = store.Load(); Assert(read.Settings.Profiles[0].Address == p.Address); Assert(!File.ReadAllText(Path.Combine(store.Root, "settings.json")).Contains("query-private-6152") && !SettingsStore.ExportWithoutSecrets(read.Settings).Contains("query-private-6152")); }));
        var output = Path.Combine(root, "artifacts", "verification", "protocol"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { test = "API protocol fixtures and Windows DPAPI; no external AI calls", utcTime = DateTimeOffset.UtcNow, passed = failed == 0, total = rows.Count, failed, rows }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    public static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    public static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static HttpResponseMessage Json(string text, int status = 200) { var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") }; if (status == 429) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(300)); return r; }
    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(text))) };
    private sealed class FailingProtector : ISecretProtector { public byte[] Protect(byte[] bytes) => throw new System.Security.Cryptography.CryptographicException("simulated failure"); public byte[] Unprotect(byte[] bytes) => throw new NotImplementedException(); }
    private sealed class FixtureHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int Count { get; private set; }
        public string? LastBody { get; private set; }
        public bool AuthHeaderPresent { get; private set; }
        public bool WaitForever { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++; LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken); AuthHeaderPresent = request.Headers.Authorization is not null;
            if (WaitForever) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return _responses.Dequeue();
        }
    }
    private sealed class FragmentedStream(byte[] bytes) : Stream
    {
        private int _position;
        public override int Read(byte[] buffer, int offset, int count) { var length = Math.Min(Math.Min(count, (_position % 3) + 1), bytes.Length - _position); Array.Copy(bytes, _position, buffer, offset, length); _position += length; return length; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); var length = Math.Min(Math.Min(buffer.Length, (_position % 3) + 1), bytes.Length - _position); bytes.AsMemory(_position, length).CopyTo(buffer); _position += length; return ValueTask.FromResult(length); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => bytes.Length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
