using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ScreenTranslator.Settings;

namespace ScreenTranslator.Translation;

public static class EndpointResolver
{
    public static Uri Resolve(ApiProfile profile)
    {
        if (!Uri.TryCreate(profile.Address.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("请输入完整地址，例如 https://example.com/v1。");
        if (uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("地址不能包含用户名、密码或 # 片段；请在密钥栏填写凭据。");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && IsLoopback(uri.Host))) throw new ArgumentException("远程连接需要 HTTPS；HTTP 仅用于 localhost 或回环地址的本机服务。");
        if (profile.Authentication == AuthMode.None && !IsLoopback(uri.Host)) throw new ArgumentException("无认证模式仅用于明确配置的本机服务。");
        if (profile.AddressMode == AddressMode.Full) return uri;
        if (uri.Query.Length != 0) throw new ArgumentException("带查询参数的地址请改选“完整请求地址”。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + (profile.Format == ApiFormat.ChatCompletions ? "/chat/completions" : "/responses"));
    }

    public static bool IsLoopback(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    public static string Display(Uri uri) => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length == 0 ? "" : "?（查询值已隐藏）");
}

public static class ProfileValidator
{
    private static readonly HashSet<string> CoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "messages", "input", "instructions", "stream", "tools", "tool_choice", "parallel_tool_calls", "functions", "function_call",
        "max_tokens", "max_completion_tokens", "max_output_tokens", "previous_response_id", "conversation", "background"
    };
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Content-Type", "Connection", "Transfer-Encoding", "Cookie", "Set-Cookie" };
    public static JsonObject ExtraBody(ApiProfile profile)
    {
        if (profile.ExtraBodyJson.Length > 32768) throw new ArgumentException("额外 JSON 过长，请缩减到 32 KB 内。");
        JsonObject body;
        try { body = JsonNode.Parse(string.IsNullOrWhiteSpace(profile.ExtraBodyJson) ? "{}" : profile.ExtraBodyJson) as JsonObject ?? throw new ArgumentException("额外 JSON 必须是对象，例如 {\"temperature\":0}。"); }
        catch (System.Text.Json.JsonException) { throw new ArgumentException("额外 JSON 格式有误，请检查括号和引号。"); }
        if (body.Any(pair => CoreFields.Contains(pair.Key))) throw new ArgumentException("额外 JSON 不能覆盖模型、原文、翻译规则、流式开关、工具或输出限制字段。");
        return body;
    }

    public static void Validate(ApiProfile profile, ProfileSecrets secrets)
    {
        EndpointResolver.Resolve(profile);
        if (!Enum.IsDefined(profile.Format) || !Enum.IsDefined(profile.AddressMode) || !Enum.IsDefined(profile.Authentication) || !Enum.IsDefined(profile.OutputLimit)) throw new ArgumentException("连接设置中有未知选项。");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("请给连接起一个名称。");
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("请填写模型名称；不需要先获取模型列表。");
        if (profile.TimeoutSeconds is < 5 or > 120) throw new ArgumentException("超时请填写 5 到 120 秒。");
        if (profile.OutputLimit != OutputLimitField.None && profile.MaxOutput is < 32 or > 32768) throw new ArgumentException("输出限制请填写 32 到 32768。");
        if (profile.Format == ApiFormat.Responses && profile.OutputLimit is OutputLimitField.MaxTokens or OutputLimitField.MaxCompletionTokens || profile.Format == ApiFormat.ChatCompletions && profile.OutputLimit == OutputLimitField.MaxOutputTokens) throw new ArgumentException("输出限制字段与接口格式不匹配。");
        ExtraBody(profile);
        if (profile.Authentication != AuthMode.None && string.IsNullOrWhiteSpace(secrets.ApiKey)) throw new ArgumentException("请填写 API 密钥，或为本机服务明确选择无认证。");
        if (secrets.ApiKey.Contains('\r') || secrets.ApiKey.Contains('\n')) throw new ArgumentException("密钥不能包含换行。");
        if (profile.Authentication == AuthMode.Header) CheckHeader(profile.KeyHeader);
        foreach (var (name, value) in secrets.ExtraHeaders)
        {
            CheckHeader(name);
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || (profile.Authentication == AuthMode.Header && name.Equals(profile.KeyHeader, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("额外请求头不能覆盖选定的密钥认证头。");
            if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("请求头的值不能包含换行。");
        }
    }

    private static void CheckHeader(string name)
    {
        if (TransportHeaders.Contains(name) || !Regex.IsMatch(name, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")) throw new ArgumentException("请求头名称无效或属于软件管理的传输字段。");
    }
}
