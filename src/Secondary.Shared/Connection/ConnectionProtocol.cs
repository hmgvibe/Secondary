using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Secondary.Shared.Connection;

// Wire format shared by the connection helper and devices: TLS over TCP carrying length-prefixed
// UTF-8 JSON objects, each with a "type". Unknown types are ignored so later stages can add messages.
public static class ConnectionProtocol
{
    public const int ProtocolVersion = 1;
    public const int TcpPort = 47611;
    public const int DiscoveryPort = 47610;
    public const int MaxFrameBytes = 1024 * 1024;
    public const string DiscoverRequest = "SECONDARY-DISCOVER/1";
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    public static JsonObject Message(string type, params (string Name, JsonNode? Value)[] fields)
    {
        var message = new JsonObject { ["type"] = type };
        foreach (var (name, value) in fields) message[name] = value;
        return message;
    }
    // A reply to a request names the request's id.
    public static JsonObject Reply(JsonObject request, JsonObject reply)
    {
        reply["reply-to"] = request["id"]?.DeepClone();
        return reply;
    }
    public static string TypeOf(JsonObject message) => message["type"]?.GetValueKind() == JsonValueKind.String ? message["type"]!.GetValue<string>() : "";
    public static string? Text(JsonObject message, string name) => message[name]?.GetValueKind() == JsonValueKind.String ? message[name]!.GetValue<string>() : null;
    public static long? Number(JsonObject message, string name) => message[name]?.GetValueKind() == JsonValueKind.Number && message[name]!.AsValue().TryGetValue<long>(out var value) ? value : null;
}

// One framed, encrypted conversation. Sends are serialized so heartbeats and replies never interleave.
public sealed class FrameChannel(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    public Stream Stream => stream;

    public async Task SendAsync(JsonObject message, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > ConnectionProtocol.MaxFrameBytes) throw new InvalidDataException("訊息超過大小上限");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    // Throws EndOfStreamException when the peer closes and InvalidDataException for a malformed frame.
    public async Task<JsonObject> ReceiveAsync(CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > ConnectionProtocol.MaxFrameBytes) throw new InvalidDataException("訊息長度無效");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        try { return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("訊息不是 JSON 物件"); }
        catch (JsonException error) { throw new InvalidDataException("訊息格式錯誤：" + error.Message, error); }
    }

    // A receive that fails if nothing arrives in time, for handshakes and heartbeats.
    public async Task<JsonObject> ReceiveAsync(TimeSpan timeout, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try { return await ReceiveAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("對方沒有回應"); }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        writeGate.Dispose();
    }
}

// A refusal the other side explained; the message is shown to the user as is.
public sealed class ConnectionRefusedException(string code, string message) : Exception(message)
{
    public string Code => code;
}
