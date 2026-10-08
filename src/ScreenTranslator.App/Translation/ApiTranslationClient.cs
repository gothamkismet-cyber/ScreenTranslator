using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenTranslator.Ocr;
using ScreenTranslator.Settings;

namespace ScreenTranslator.Translation;

public sealed class ApiTranslationClient : ITranslationService, IDisposable
{
    private readonly HttpClient _http;
    public ApiTranslationClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<TranslationResult> TranslateAsync(ApiProfile profile, ProfileSecrets secrets, string original, SourceLanguage language, Action<string> onText, CancellationToken token)
    {
        ProfileValidator.Validate(profile, secrets);
        if (string.IsNullOrWhiteSpace(original) || original.Length > 6000) throw new ArgumentException("原文需要 1 到 6000 个字符，请缩小选区。");
        var timer = Stopwatch.StartNew();
        var partial = "";
        TimeSpan? first = null;
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, EndpointResolver.Resolve(profile));
                AddHeaders(request, profile, secrets);
                request.Content = new StringContent(BuildBody(profile, original, language).ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400) throw new TranslationException(TranslationErrorKind.Redirect, "服务返回了重定向。请填写最终接口地址；软件不会向跳转地址转发密钥。");
                if (!response.IsSuccessStatusCode) throw await HttpErrorAsync(response, timeout.Token);
                Action<string> update = value =>
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (value.Length > 100000) throw new TranslationException(TranslationErrorKind.InvalidResponse, "返回文字过长，已停止读取。");
                    partial = value;
                    if (value.Length > 0) first ??= timer.Elapsed;
                    onText(value);
                };
                (string Text, bool Truncated) result;
                if (profile.Stream)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    result = await ParseStreamAsync(stream, profile.Format, update, timeout.Token);
                }
                else
                {
                    var json = await ReadBoundedAsync(response.Content, timeout.Token);
                    using var document = JsonDocument.Parse(json);
                    result = ParseFull(document.RootElement, profile.Format);
                    update(result.Text);
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(result.Text)) throw new TranslationException(TranslationErrorKind.Empty, "接口返回了空译文。请检查模型和接口格式，或改用完整返回再试。");
                return new TranslationResult(result.Text, timer.Elapsed, first, result.Truncated);
            }
            catch (TranslationException error) when (error.Retryable && attempt == 0 && partial.Length == 0)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(error.RetryDelay, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TranslationException(TranslationErrorKind.Timeout, "请求超时，结果是否已在服务端生成无法确定。请检查连接后手动刷新。", partialText: partial); }
            catch (HttpRequestException)
            { throw new TranslationException(TranslationErrorKind.Network, "网络连接失败或已断开。请检查网络和接口地址，再手动刷新。", partialText: partial); }
            catch (IOException)
            { throw new TranslationException(TranslationErrorKind.Incomplete, "返回中途断开，当前译文未完成。请手动刷新；软件不会自动重复发送。", partialText: partial); }
            catch (JsonException)
            { throw new TranslationException(TranslationErrorKind.InvalidResponse, "返回内容不符合所选 API 格式。请检查 Chat Completions / Responses 选项。", partialText: partial); }
            catch (DecoderFallbackException)
            { throw new TranslationException(TranslationErrorKind.InvalidResponse, "返回内容不是有效的 UTF-8 文字。请检查接口服务。", partialText: partial); }
        }
    }

    public static JsonObject BuildBody(ApiProfile profile, string original, SourceLanguage language)
    {
        var body = ProfileValidator.ExtraBody(profile);
        body["model"] = profile.Model.Trim();
        body["stream"] = profile.Stream;
        if (profile.Format == ApiFormat.ChatCompletions)
            body["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = TranslationPrompt.Instructions(language) }, new JsonObject { ["role"] = "user", ["content"] = original });
        else { body["instructions"] = TranslationPrompt.Instructions(language); body["input"] = original; }
        var limit = profile.OutputLimit switch { OutputLimitField.MaxTokens => "max_tokens", OutputLimitField.MaxCompletionTokens => "max_completion_tokens", OutputLimitField.MaxOutputTokens => "max_output_tokens", _ => null };
        if (limit is not null) body[limit] = profile.MaxOutput;
        return body;
    }

    private static void AddHeaders(HttpRequestMessage request, ApiProfile profile, ProfileSecrets secrets)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(profile.Stream ? "text/event-stream" : "application/json"));
        if (profile.Authentication == AuthMode.Bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.ApiKey.Trim());
        if (profile.Authentication == AuthMode.Header && !request.Headers.TryAddWithoutValidation(profile.KeyHeader, secrets.ApiKey.Trim())) throw new ArgumentException("无法添加密钥请求头，请检查名称。");
        foreach (var (name, value) in secrets.ExtraHeaders)
            if (!request.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException("无法添加额外请求头，请检查名称。");
    }

    public async Task<string[]> ListModelsAsync(ApiProfile profile, ProfileSecrets secrets, CancellationToken token)
    {
        ProfileValidator.Validate(profile, secrets);
        if (profile.AddressMode != AddressMode.Base) throw new ArgumentException("完整地址模式不能推算模型列表地址，请直接手填模型名称。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(profile.Address.Trim().TrimEnd('/') + "/models"));
        AddHeaders(request, profile with { Stream = false }, secrets);
        using var response = await _http.SendAsync(request, timeout.Token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new TranslationException(TranslationErrorKind.Redirect, "模型列表返回重定向，请直接手填模型。");
        if (!response.IsSuccessStatusCode) throw await HttpErrorAsync(response, timeout.Token);
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, timeout.Token));
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new TranslationException(TranslationErrorKind.InvalidResponse, "服务没有返回标准模型列表，仍可直接手填模型名称。");
        return data.EnumerateArray().Select(item => String(item, "id")).Where(id => id.Length != 0).Distinct().Take(1000).ToArray();
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (memory.Length + count > 2 * 1024 * 1024) throw new TranslationException(TranslationErrorKind.InvalidResponse, "返回内容过大，请检查接口格式。");
            memory.Write(bytes, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(memory.ToArray());
    }

    private static async Task<TranslationException> HttpErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        var code = "";
        try
        {
            using var doc = JsonDocument.Parse(await ReadBoundedAsync(response.Content, token));
            if (doc.RootElement.TryGetProperty("error", out var error)) code = String(error, "code") + " " + String(error, "type");
        }
        catch (JsonException) { }
        catch (DecoderFallbackException) { }
        if (code.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) || code.Contains("billing", StringComparison.OrdinalIgnoreCase)) return new TranslationException(TranslationErrorKind.Quota, "服务报告余额或额度不足，已暂停。请检查账户额度后再开始。");
        var status = (int)response.StatusCode;
        if (status is 401 or 403) return new TranslationException(TranslationErrorKind.Authentication, $"认证失败（HTTP {status}），已暂停。请检查密钥、认证方式和模型权限。");
        if (status == 429)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1);
            return new TranslationException(TranslationErrorKind.RateLimit, "服务限速（HTTP 429）。本次最多重试一次；仍失败时请稍后手动刷新。", delay <= TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0.3, 30)));
        }
        if (status is 500 or 502 or 503 or 504) return new TranslationException(TranslationErrorKind.Server, $"服务暂时异常（HTTP {status}）。本次最多重试一次；仍失败时请稍后手动刷新。", true);
        return new TranslationException(TranslationErrorKind.Unsupported, $"接口拒绝请求（HTTP {status}）。请检查地址、模型、API 格式和额外参数；软件不会自动删参数重发。");
    }

    private static (string Text, bool Truncated) ParseFull(JsonElement root, ApiFormat format)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new TranslationException(TranslationErrorKind.Server, "服务返回错误，请检查连接设置后手动刷新。");
        if (format == ApiFormat.ChatCompletions)
        {
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return ("", false);
            var choice = choices[0];
            var finish = String(choice, "finish_reason");
            CheckFinish(finish);
            if (!choice.TryGetProperty("message", out var message)) return ("", false);
            if (String(message, "refusal").Length != 0) throw Refusal();
            return (ContentText(message), finish == "length");
        }
        var status = String(root, "status");
        if (status == "failed") throw new TranslationException(TranslationErrorKind.Server, "服务未完成生成，请手动刷新。");
        return (ResponseText(root), status == "incomplete");
    }

    private static async Task<(string Text, bool Truncated)> ParseStreamAsync(Stream stream, ApiFormat format, Action<string> onText, CancellationToken token)
    {
        var text = new StringBuilder();
        var finished = false;
        var truncated = false;
        await foreach (var item in SseReader.ReadAsync(stream, token))
        {
            token.ThrowIfCancellationRequested();
            if (item.Data == "[DONE]") { finished = true; break; }
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            if (format == ApiFormat.ChatCompletions)
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new TranslationException(TranslationErrorKind.Server, "流式服务返回错误，当前译文未完成，请手动刷新。", partialText: text.ToString());
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("index", out var index) && index.TryGetInt32(out var number) && number != 0) continue;
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (String(delta, "refusal").Length != 0) throw Refusal();
                        var chunk = ContentText(delta);
                        if (chunk.Length != 0) { text.Append(chunk); onText(text.ToString()); }
                    }
                    var finish = String(choice, "finish_reason");
                    if (finish.Length != 0) { CheckFinish(finish); finished = true; truncated = finish == "length"; }
                }
            }
            else
            {
                var type = String(root, "type");
                if (type.Length == 0) type = item.Event;
                if (type == "response.output_text.delta") { text.Append(String(root, "delta")); onText(text.ToString()); }
                if (type.Contains("refusal", StringComparison.Ordinal)) throw Refusal();
                if (type is "response.failed" or "error" or "response.error") throw new TranslationException(TranslationErrorKind.Server, "流式服务报告生成失败，当前译文未完成，请手动刷新。", partialText: text.ToString());
                if (type is "response.completed" or "response.incomplete")
                {
                    finished = true;
                    truncated = type == "response.incomplete";
                    if (root.TryGetProperty("response", out var response))
                    {
                        if (String(response, "status") == "failed") throw new TranslationException(TranslationErrorKind.Server, "服务未完成生成，请手动刷新。", partialText: text.ToString());
                        var final = ResponseText(response);
                        if (final.Length > 0) { text.Clear().Append(final); onText(final); }
                        truncated |= String(response, "status") == "incomplete";
                    }
                    break;
                }
            }
        }
        if (!finished) throw new TranslationException(TranslationErrorKind.Incomplete, "返回中途结束，缺少完成标记。当前译文未完成，请手动刷新。", partialText: text.ToString());
        return (text.ToString(), truncated);
    }

    private static string ResponseText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return "";
        var texts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                var type = String(part, "type");
                if (type == "refusal") throw Refusal();
                if (type == "output_text") texts.Add(String(part, "text"));
            }
        }
        return string.Join("\n", texts);
    }

    private static string ContentText(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(content.EnumerateArray().Where(part => String(part, "type") == "text").Select(part => String(part, "text")));
    }
    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static TranslationException Refusal() => new(TranslationErrorKind.Refusal, "模型拒绝了这次翻译，未取得可用译文。请检查原文或换用你选择的模型。");
    private static void CheckFinish(string finish)
    {
        if (finish == "content_filter") throw Refusal();
        if (finish is "tool_calls" or "function_call") throw new TranslationException(TranslationErrorKind.Unsupported, "模型返回了工具调用而非译文，请使用支持纯文本翻译的模型。");
    }
    public void Dispose() => _http.Dispose();
}
