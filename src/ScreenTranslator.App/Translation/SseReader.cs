using System.Runtime.CompilerServices;
using System.Text;

namespace ScreenTranslator.Translation;

public sealed record SseEvent(string Event, string Data);
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var data = new StringBuilder();
        var name = "";
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null)
            {
                if (data.Length > 0) yield return new SseEvent(name, data.ToString().TrimEnd('\n'));
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return new SseEvent(name, data.ToString().TrimEnd('\n'));
                data.Clear(); name = ""; continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") name = value;
            if (field == "data") data.Append(value).Append('\n');
            if (data.Length > 1024 * 1024) throw new TranslationException(TranslationErrorKind.InvalidResponse, "流式事件过大，已停止读取。请检查服务的接口格式。");
        }
    }
}
