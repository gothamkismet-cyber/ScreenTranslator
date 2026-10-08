using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenTranslator.Tests;

internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);
    public string BaseAddress { get; }
    public LoopbackServer()
    {
        _listener.Start(); BaseAddress = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        _accept = AcceptAsync();
    }
    public static string Expected(string text) => "本机模拟译文 · 原文编号 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    private async Task AcceptAsync()
    {
        try { while (!_cancel.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_cancel.Token); lock (_connections) _connections.Add(HandleAsync(client)); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_cancel.IsCancellationRequested) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); var headers = new List<byte>(); var one = new byte[1];
                while (headers.Count < 32768)
                {
                    if (await stream.ReadAsync(one, _cancel.Token) == 0) return;
                    headers.Add(one[0]);
                    if (headers.Count >= 4 && headers[^4] == 13 && headers[^3] == 10 && headers[^2] == 13 && headers[^1] == 10) break;
                }
                var header = Encoding.ASCII.GetString(headers.ToArray());
                var lengthLine = header.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                if (length is < 1 or > 1048576) return;
                var body = new byte[length]; await stream.ReadExactlyAsync(body, _cancel.Token);
                var json = JsonNode.Parse(body)!; var responses = header.StartsWith("POST /v1/responses ");
                var original = responses ? json["input"]!.GetValue<string>() : json["messages"]![1]!["content"]!.GetValue<string>();
                var text = Expected(original); Interlocked.Increment(ref _requests);
                var full = responses
                    ? JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } })
                    : JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { content = text }, finish_reason = "stop" } } });
                var streaming = json["stream"]?.GetValue<bool>() == true;
                var responseBody = full;
                if (streaming)
                {
                    responseBody = responses
                        ? "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text }) + "\n\ndata: " + JsonSerializer.Serialize(new { type = "response.completed", response = JsonNode.Parse(full) }) + "\n\n"
                        : "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text } } } }) + "\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                }
                var payload = Encoding.UTF8.GetBytes(responseBody);
                var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(streaming ? "text/event-stream" : "application/json")}; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, _cancel.Token);
                for (var offset = 0; offset < payload.Length; offset += 23) { await stream.WriteAsync(payload.AsMemory(offset, Math.Min(23, payload.Length - offset)), _cancel.Token); if (streaming) await Task.Delay(2, _cancel.Token); }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel(); _listener.Stop(); await _accept; Task[] connections; lock (_connections) connections = _connections.ToArray(); await Task.WhenAll(connections); _cancel.Dispose();
    }
}
