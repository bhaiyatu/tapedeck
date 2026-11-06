using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tapedeck.Services.Discord;

/// <summary>Discord's desktop app isn't running (no local RPC pipe to connect to).</summary>
public sealed class DiscordNotRunningException() : Exception("Discord isn't running");

/// <summary>
/// A small client for the Discord desktop app's local RPC pipe (<c>\\.\pipe\discord-ipc-N</c>).
/// It only ever sends Rich Presence for this process; it never asks Discord for anything else.
/// Frames are <c>[opcode:int32 LE][length:int32 LE][UTF-8 JSON]</c>.
/// </summary>
public sealed class DiscordIpcClient : IAsyncDisposable
{
    // Anything Discord legitimately sends us is tiny; refuse oversized frames from whatever holds the pipe.
    private const int MaxFrameBytes = 64 * 1024;

    private enum Op
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private NamedPipeClientStream? _pipe;
    private volatile bool _closed;

    /// <summary>The Discord account's display name, from the READY handshake.</summary>
    public string? UserName { get; private set; }

    public bool IsConnected => !_closed && _pipe is { IsConnected: true };

    /// <summary>Raised once when the connection ends (Discord closed, pipe broken, or rejected).</summary>
    public event Action<string>? Disconnected;

    /// <summary>Raised for every reply Discord sends to a command (useful for diagnostics).</summary>
    public event Action<JsonNode>? Reply;

    /// <summary>Discord rejected a command (for example an invalid activity).</summary>
    public event Action<string>? CommandError;

    /// <summary>Opens the pipe and performs the handshake. Throws <see cref="DiscordNotRunningException"/> when Discord isn't open.</summary>
    public async Task ConnectAsync(string applicationId, CancellationToken ct)
    {
        foreach (var name in CandidatePipes())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, ct);
                _pipe = pipe;
                break;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
            }
        }
        if (_pipe is null) throw new DiscordNotRunningException();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await WriteAsync(Op.Handshake, new JsonObject { ["v"] = 1, ["client_id"] = applicationId }, timeout.Token);
        var (op, body) = await ReadAsync(timeout.Token);
        if (op == Op.Close)
            throw new InvalidOperationException(Str(body?["message"]) ?? "Discord refused the connection");
        if (op != Op.Frame || Str(body?["evt"]) != "READY")
            throw new InvalidDataException("Unexpected handshake reply from Discord");

        var user = body?["data"]?["user"];
        UserName = Str(user?["global_name"]) ?? Str(user?["username"]);
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Sets (or, with null, clears) this app's activity on the user's Discord profile.</summary>
    public Task SetActivityAsync(JsonObject? activity, CancellationToken ct)
    {
        var args = new JsonObject { ["pid"] = Environment.ProcessId };
        if (activity is not null) args["activity"] = activity;
        return WriteAsync(Op.Frame, new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            ["nonce"] = Guid.NewGuid().ToString("N"),
        }, ct);
    }

    private static IEnumerable<string> CandidatePipes()
    {
        // Listing the pipe namespace doesn't open anything, so we only try pipes that exist.
        string[] existing;
        try
        {
            existing = Directory.GetFiles(@"\\.\pipe\", "discord-ipc-*")
                .Select(p => p[(p.LastIndexOf('\\') + 1)..])
                .ToArray();
        }
        catch (Exception)
        {
            existing = ["discord-ipc-0"];
        }
        for (var i = 0; i < 10; i++)
        {
            var name = $"discord-ipc-{i}";
            if (existing.Contains(name, StringComparer.OrdinalIgnoreCase)) yield return name;
        }
    }

    private async Task ReadLoopAsync()
    {
        var reason = "Discord closed the connection";
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var (op, body) = await ReadAsync(_life.Token);
                switch (op)
                {
                    case Op.Ping:
                        await WriteAsync(Op.Pong, body ?? new JsonObject(), _life.Token);
                        break;
                    case Op.Close:
                        reason = Str(body?["message"]) ?? reason;
                        return;
                    case Op.Frame when body is not null:
                        Reply?.Invoke(body);
                        if (Str(body["evt"]) == "ERROR")
                            CommandError?.Invoke(Str(body["data"]?["message"]) ?? "Discord rejected the update");
                        break;
                }
            }
        }
        catch (Exception) when (!_life.IsCancellationRequested)
        {
            reason = "Lost the connection to Discord";
        }
        catch (Exception)
        {
            // Shutting down.
        }
        finally
        {
            MarkClosed(reason);
        }
    }

    private async Task<(Op Op, JsonNode? Body)> ReadAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Not connected");
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var op = (Op)BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("Oversized frame from Discord");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, ct);
        JsonNode? body = null;
        if (length > 0)
        {
            try
            {
                body = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                throw new InvalidDataException("Malformed frame from Discord");
            }
        }
        return (op, body);
    }

    private async Task WriteAsync(Op op, JsonNode body, CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe is null || _closed) throw new IOException("Not connected to Discord");
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            MarkClosed("Lost the connection to Discord");
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void MarkClosed(string reason)
    {
        if (_closed) return;
        _closed = true;
        Disconnected?.Invoke(reason);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        _life.Cancel();
        // Discord removes this app's activity as soon as the pipe closes.
        if (_pipe is not null) await _pipe.DisposeAsync();
    }
}
