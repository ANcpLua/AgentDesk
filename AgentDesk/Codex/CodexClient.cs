using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentDesk.Domain;

namespace AgentDesk.Codex;

// Uses public app-server JSON-RPC over its local WebSocket socket for reads,
// and the user's installed `codex queue` command for writes. Never opens Codex's SQLite DB.
public sealed partial class CodexClient(TimeProvider timeProvider) : ICodexClient
{
    private static readonly JsonSerializerOptions NativeJson = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly string _executable = Environment.GetEnvironmentVariable("QUESTION_INBOX_CODEX") ?? "/opt/homebrew/bin/codex";
    private readonly string _socketPath = Environment.GetEnvironmentVariable("QUESTION_INBOX_CODEX_SOCKET")
        ?? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            "app-server-control", "app-server-control.sock");

    public async Task<CodexUsage> UsageAsync(CancellationToken ct)
    {
        await using var rpc = await ConnectAsync(ct);
        return await rpc.CallAsync<CodexUsage>("account/rateLimits/read", new { }, ct);
    }

    public async Task<CodexTokenActivity> TokenActivityAsync(CancellationToken ct)
    {
        await using var rpc = await ConnectAsync(ct);
        var activity = await rpc.CallAsync<NativeTokenActivity>("account/usage/read", new { }, ct);
        return activity.ToResponse();
    }

    public async Task<CodexMcpServerPage> McpServersAsync(string? threadId, string? serverName, string? cursor, CancellationToken ct)
    {
        if (threadId is not null) ValidateId(threadId);
        await using var rpc = await ConnectAsync(ct);
        NativeMcpServerPage page;
        try
        {
            page = await rpc.CallAsync<NativeMcpServerPage>("mcpServerStatus/list", new
            {
                threadId, serverName, cursor, limit = 50, detail = "toolsAndAuthOnly"
            }, ct);
        }
        catch (CodexConnectionException error) when (error.RpcCode == -32601)
        {
            throw new Questions.QuestionException(501, "Diese Codex-Version stellt MCP-Verbindungsstatus nicht bereit.");
        }
        if (page.Data is null || page.Data.Any(server => server is null || string.IsNullOrWhiteSpace(server.Name)
                || string.IsNullOrWhiteSpace(server.AuthStatus) || server.Tools is null))
            throw new CodexConnectionException("Codex hat keine lesbaren MCP-Statusdaten geliefert.");
        return new(page.Data.Select(server => new CodexMcpServer(server.Name, server.RuntimeStatus,
            server.AuthStatus, server.ToolsError is null ? server.Tools.Count : null,
            server.ToolsError, server.HttpOrigin, server.PluginId)).ToArray(), page.NextCursor);
    }

    public async Task<CodexThreadPage> ListAsync(string? workspacePath, string? cursor, CancellationToken ct)
    {
        await using var rpc = await ConnectAsync(ct);
        var response = await rpc.CallAsync<NativeThreadList>("thread/list", new
        {
            limit = 50,
            sortKey = "updated_at",
            sortDirection = "desc",
            cwd = workspacePath,
            cursor,
            useStateDbOnly = true
        }, ct);
        return new CodexThreadPage(response.Data.Select(thread => thread.ToResponse()).ToArray(), response.NextCursor);
    }

    public async Task<CodexThread> GetAsync(string threadId, CancellationToken ct)
    {
        ValidateId(threadId);
        await using var rpc = await ConnectAsync(ct);
        var response = await rpc.CallAsync<NativeThreadRead>("thread/read", new
        {
            threadId,
            includeTurns = false
        }, ct);
        return response.Thread.ToResponse();
    }

    public async Task<CodexThreadDetail> DetailAsync(string threadId, CancellationToken ct)
    {
        ValidateId(threadId);
        await using var rpc = await ConnectAsync(ct);
        var response = await rpc.CallAsync<NativeThreadRead>("thread/read", new
        {
            threadId,
            includeTurns = false
        }, ct);
        var thread = response.Thread.ToResponse();
        NativeGoal? goal = null;
        try
        {
            var responseGoal = await rpc.CallAsync<NativeGoalResponse>("thread/goal/get", new
            {
                threadId
            }, ct);
            goal = responseGoal.Goal;
        }
        catch (CodexConnectionException)
        {
            // Older servers may not expose goals.
        }
        var turns = await rpc.CallAsync<NativeTurnPage>("thread/turns/list", new
        {
            threadId,
            limit = 1,
            sortDirection = "desc",
            itemsView = "summary"
        }, ct);
        var turn = turns.Data.FirstOrDefault();
        if (turn is null)
            return new(thread, goal?.Objective, goal?.Status, null, null, null);
        var reply = turn.Items.Where(item => item.Type == "agentMessage").Select(item => item.Text).LastOrDefault();
        return new(thread, goal?.Objective, goal?.Status, turn.Id, turn.Status,
            reply is { Length: > 6000 } ? reply[..6000] + "\n…" : reply);
    }

    public async Task<QueueOutcome> QueueAsync(string threadId, string message, CancellationToken ct)
    {
        ValidateId(threadId);
        var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("QUESTION_INBOX_QUEUE_TIMEOUT_SECONDS"), out var configured)
            ? Math.Clamp(configured, 1, 60) : 20;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var output = await Process.RunAndCaptureTextAsync(_executable,
                ["queue", "--remote", "unix://" + _socketPath, "--thread", threadId, "--message", message], timeout.Token);
            if (output.ExitStatus.ExitCode != 0)
                return new QueueRejected("Codex hat die Nachricht nicht bestätigt: " + Clip(output.StandardError, 1000));
            // CLI success is a queue receipt, never proof that a model has processed the message.
            var words = output.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var marker = Array.IndexOf(words, "message");
            var messageId = marker >= 0 && marker + 1 < words.Length && Guid.TryParse(words[marker + 1], out _)
                ? words[marker + 1] : null;
            if (messageId is null || !output.StandardOutput.Contains(threadId, StringComparison.Ordinal))
                return new QueueUncertain("Codex beendete den Versand ohne eine lesbare, zu diesem Thread passende Quittung. Ziel vor erneutem Senden prüfen.");
            return new QueueAccepted(messageId);
        }
        catch (OperationCanceledException)
        {
            return new QueueUncertain("Keine sichere Zustellbestätigung erhalten. Vor erneutem Senden den Ziel-Thread prüfen.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new QueueRejected("Das konfigurierte Codex-Programm konnte nicht gestartet werden.");
        }
    }

    private async Task<RpcConnection> ConnectAsync(CancellationToken ct)
    {
        try
        {
            return await RpcConnection.OpenAsync(_socketPath, timeProvider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is SocketException or WebSocketException or IOException or OperationCanceledException)
        {
            throw new CodexConnectionException("Der lokale Codex App Server ist nicht erreichbar. Starte Codex und aktualisiere die Ansicht.");
        }
    }

    public static void ValidateId(string id)
    {
        if (!Guid.TryParse(id, out _))
            throw new Questions.QuestionException(400, "Eine konkrete Codex-Thread-ID ist erforderlich.");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Clip(string? value, int size) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(size, value.Length)];

    private sealed class RpcConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _webSocket = new();
        private readonly HttpMessageInvoker _invoker;
        private readonly TimeProvider _timeProvider;
        private int _id;

        private RpcConnection(string socketPath, TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            _invoker = new HttpMessageInvoker(new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });
        }

        public static async Task<RpcConnection> OpenAsync(string socketPath, TimeProvider timeProvider, CancellationToken ct)
        {
            var rpc = new RpcConnection(socketPath, timeProvider);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8), timeProvider);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                await rpc._webSocket.ConnectAsync(new Uri("ws://localhost/"), rpc._invoker, timeout.Token);
                await rpc.CallElementAsync("initialize", new
                {
                    clientInfo = new
                    {
                        name = "question_inbox",
                        title = "Fragenpostfach",
                        version = "0.3.0"
                    },
                    capabilities = new
                    {
                    }
                }, timeout.Token);
                await rpc.SendAsync(new
                {
                    method = "initialized"
                }, timeout.Token);
                return rpc;
            }
            catch
            {
                await rpc.DisposeAsync();
                throw;
            }
        }

        public async Task<TResult> CallAsync<TResult>(string method, object parameters, CancellationToken ct)
            where TResult : class
        {
            var result = await CallElementAsync(method, parameters, ct);
            JsonTypeInfo<TResult> typeInfo = NativeJson.GetTypeInfo<TResult>();
            try
            {
                return result.Deserialize(typeInfo)
                    ?? throw UnreadableResponse();
            }
            catch (JsonException)
            {
                throw UnreadableResponse();
            }
        }

        private async Task<JsonElement> CallElementAsync(string method, object parameters, CancellationToken ct)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8), _timeProvider);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            {
                return await CallCoreAsync(method, parameters, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CodexConnectionException("Codex hat nicht rechtzeitig auf die Leseanfrage geantwortet.");
            }
            catch (Exception e) when (e is SocketException or WebSocketException or IOException)
            {
                throw new CodexConnectionException("Die Verbindung zum Codex App Server wurde unterbrochen.");
            }
            catch (JsonException)
            {
                throw UnreadableResponse();
            }
        }

        private async Task<JsonElement> CallCoreAsync(string method, object parameters, CancellationToken ct)
        {
            var id = ++_id;
            await SendAsync(new
            {
                id,
                method,
                @params = parameters
            }, ct);
            var buffer = new byte[8192];
            while (true)
            {
                using var data = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await _webSocket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (frame.MessageType == WebSocketMessageType.Close)
                        throw new CodexConnectionException("Codex hat die Verbindung beendet.");
                    data.Write(buffer, 0, frame.Count);
                    if (data.Length > 2 * 1024 * 1024)
                        throw new CodexConnectionException("Codex-Antwort zu groß. Bitte den Thread gezielt in Codex öffnen.");
                } while (!frame.EndOfMessage);
                using var document = JsonDocument.Parse(data.ToArray());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw UnreadableResponse();
                if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                    || !responseId.TryGetInt32(out var returnedId) || returnedId != id)
                    continue;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeValue)
                        && codeValue.ValueKind == JsonValueKind.Number && codeValue.TryGetInt32(out var parsedCode) ? parsedCode : (int?)null;
                    throw new CodexConnectionException(Text(error, "message") ?? "Codex-Anfrage fehlgeschlagen.", code);
                }
                if (!root.TryGetProperty("result", out var result))
                    throw UnreadableResponse();
                return result.Clone();
            }
        }

        private static CodexConnectionException UnreadableResponse() =>
            new("Codex hat keine lesbaren Antwortdaten geliefert.");

        private Task SendAsync(object value, CancellationToken ct) =>
            _webSocket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1), _timeProvider);
                    await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Read complete", timeout.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                _webSocket.Abort();
            }
            finally
            {
                _webSocket.Dispose();
                _invoker.Dispose();
            }
        }
    }
}
