using System.Text.Json;

namespace ScreenTranslator.Settings;

public sealed class DiagnosticLog
{
    private readonly string _path;
    private readonly object _gate = new();
    public DiagnosticLog(string root) => _path = Path.Combine(root, "diagnostics.jsonl");
    // Callers pass controlled event names/categories, never provider messages, headers, or screen text.
    public void Record(string stage, string category, long requestId = 0, double? elapsedMs = null)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024) File.Move(_path, _path + ".previous", true);
                File.AppendAllText(_path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, stage, category, requestId, elapsedMs }) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
