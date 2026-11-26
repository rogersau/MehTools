using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JamesTools.Core.Recording;

public sealed record ObsRecordStatus(bool Active, bool Paused, long DurationMilliseconds, long Bytes);
public sealed record ObsReplayBufferStatus(bool Active)
{
    public bool Available { get; init; } = true;
}
public sealed record ObsReplayBufferSavedEventArgs(string OutputPath);
public sealed record ObsRecordFileChangedEventArgs(string OutputPath);

public sealed record ObsRecordStateChangedEventArgs(bool Active, bool Paused, string? State, string? OutputPath);

public sealed class ObsWebSocketException : IOException
{
    public ObsWebSocketException(string message) : base(message) { }
    internal int? RequestStatusCode { get; init; }
}

/// <summary>Small OBS WebSocket 5 JSON client scoped to the local OBS server.</summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int InvalidResourceState = 604;
    private const int AuthenticationFailed = 4009;
    private const int SessionInvalidated = 4011;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _replaySaveLock = new(1, 1);
    private readonly TimeSpan _saveEventTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _receiver;
    private ObsWebSocketException? _terminalError;
    private int _requestId;
    private int _disposed;
    private bool _identified;

    public ObsWebSocketClient() : this(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)) { }

    internal ObsWebSocketClient(TimeSpan connectTimeout, TimeSpan requestTimeout, TimeSpan? saveEventTimeout = null)
    {
        _connectTimeout = connectTimeout;
        _requestTimeout = requestTimeout;
        _saveEventTimeout = saveEventTimeout ?? TimeSpan.FromSeconds(30);
    }

    public event EventHandler<ObsRecordStateChangedEventArgs>? RecordStateChanged;
    public event EventHandler<ObsRecordFileChangedEventArgs>? RecordFileChanged;
    public event EventHandler<ObsReplayBufferSavedEventArgs>? ReplayBufferSaved;
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _identified &&
        !_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open;
    public bool UsesAuthentication { get; private set; }

    public async Task ConnectAsync(int port, string? password = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Enter a port from 1 to 65535.");
        if (_socket.State != WebSocketState.None) throw new InvalidOperationException("This OBS connection has already been used.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token).ConfigureAwait(false);
            using var hello = await ReceiveDocumentAsync(_socket, timeout.Token).ConfigureAwait(false);
            if (GetInt(hello.RootElement, "op") != 0)
                throw new ObsWebSocketException("OBS sent an unexpected message before Hello.");
            var helloData = hello.RootElement.GetProperty("d");
            var identify = new Dictionary<string, object?> { ["rpcVersion"] = 1 };
            bool authenticationRequired = helloData.TryGetProperty("authentication", out var auth);
            if (authenticationRequired)
            {
                if (string.IsNullOrEmpty(password))
                    throw new ObsWebSocketException("OBS requires a WebSocket password. Enter it and connect again.");
                identify["authentication"] = CreateAuthentication(password, GetString(auth, "salt"), GetString(auth, "challenge"));
            }
            await SendJsonAsync(new { op = 1, d = identify }, timeout.Token).ConfigureAwait(false);

            while (true)
            {
                using var identified = await ReceiveDocumentAsync(_socket, timeout.Token).ConfigureAwait(false);
                var op = GetInt(identified.RootElement, "op");
                if (op == 2)
                {
                    if (identified.RootElement.GetProperty("d").GetProperty("negotiatedRpcVersion").GetInt32() != 1)
                        throw new ObsWebSocketException("OBS negotiated an unsupported RPC version.");
                    break;
                }
                if (op == 7) throw ParseRequestError(identified.RootElement);
            }
            UsesAuthentication = authenticationRequired;
            _identified = true;
            _receiver = ReceiveLoopAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            await AbortSocketAsync().ConfigureAwait(false);
            throw new TimeoutException("Timed out connecting to OBS WebSocket on localhost.");
        }
        catch
        {
            await AbortSocketAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ObsRecordStatus> GetRecordStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);
        return new ObsRecordStatus(
            response.GetProperty("outputActive").GetBoolean(), response.GetProperty("outputPaused").GetBoolean(),
            response.GetProperty("outputDuration").GetInt64(), response.GetProperty("outputBytes").GetInt64());
    }

    public Task StartRecordAsync(CancellationToken cancellationToken = default) => RequestAsync("StartRecord", null, cancellationToken);

    public async Task<string?> StopRecordAsync(CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync("StopRecord", null, cancellationToken).ConfigureAwait(false);
        return GetNullableString(response, "outputPath");
    }

    public Task PauseRecordAsync(CancellationToken cancellationToken = default) => RequestAsync("PauseRecord", null, cancellationToken);

    public Task ResumeRecordAsync(CancellationToken cancellationToken = default) => RequestAsync("ResumeRecord", null, cancellationToken);

    public async Task<ObsReplayBufferStatus> GetReplayBufferStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await RequestAsync("GetReplayBufferStatus", null, cancellationToken).ConfigureAwait(false);
            return new ObsReplayBufferStatus(response.GetProperty("outputActive").GetBoolean());
        }
        catch (ObsWebSocketException ex) when (ex.RequestStatusCode == InvalidResourceState)
        {
            // OBS uses this status when the replay buffer output is unavailable.
            return new ObsReplayBufferStatus(false) { Available = false };
        }
    }

    public Task StartReplayBufferAsync(CancellationToken cancellationToken = default) => RequestAsync("StartReplayBuffer", null, cancellationToken);
    public Task StopReplayBufferAsync(CancellationToken cancellationToken = default) => RequestAsync("StopReplayBuffer", null, cancellationToken);

    public async Task<string> SaveReplayBufferAsync(CancellationToken cancellationToken = default)
    {
        await _replaySaveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ObsReplayBufferSavedEventArgs> handler = (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.OutputPath)) saved.TrySetResult(e.OutputPath);
            else saved.TrySetException(new IOException("OBS reported a replay save without an output path."));
        };
        try
        {
            // Arm after acquiring the request slot, immediately before dispatch.
            await RequestAsync("SaveReplayBuffer", null, cancellationToken,
                () => ReplayBufferSaved += handler).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_saveEventTimeout);
            try { return await saved.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (_lifetime.IsCancellationRequested) throw await DisconnectedErrorAsync("OBS disconnected before confirming the replay clip save.").ConfigureAwait(false);
                throw new TimeoutException($"OBS did not confirm the replay clip save within {_saveEventTimeout.TotalSeconds:0.##} seconds.");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Save events carry no request ID. A late event from this attempt
            // must never be mistaken for confirmation of a subsequent save.
            await AbortSocketAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ReplayBufferSaved -= handler;
            _replaySaveLock.Release();
        }
    }

    private async Task<JsonElement> RequestAsync(string requestType, object? requestData, CancellationToken cancellationToken,
        Action? beforeSend = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected) throw await DisconnectedErrorAsync("OBS WebSocket is disconnected.").ConfigureAwait(false);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_requestTimeout);
            var id = Interlocked.Increment(ref _requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                beforeSend?.Invoke();
                await SendJsonAsync(new { op = 6, d = new { requestType, requestId = id, requestData = requestData ?? new { } } }, timeout.Token).ConfigureAwait(false);
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (_lifetime.IsCancellationRequested)
                    throw await DisconnectedErrorAsync("OBS disconnected while handling the request.").ConfigureAwait(false);
                throw new TimeoutException($"OBS did not answer {requestType} within {_requestTimeout.TotalSeconds:0.##} seconds.");
            }
            catch (WebSocketException) when (!IsConnected)
            {
                throw await DisconnectedErrorAsync("OBS disconnected while sending the request.").ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }
        finally { _requestLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var document = await ReceiveDocumentAsync(_socket, cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                switch (GetInt(root, "op"))
                {
                    case 5:
                        HandleEvent(root);
                        break;
                    case 7:
                        var data = root.GetProperty("d");
                        var id = GetString(data, "requestId");
                        if (_pending.TryGetValue(id, out var pending))
                        {
                            var status = data.GetProperty("requestStatus");
                            if (GetBool(status, "result")) pending.TrySetResult(data.TryGetProperty("responseData", out var response) ? response.Clone() : EmptyObject());
                            else pending.TrySetException(ParseRequestError(root));
                        }
                        break;
                }
            }
            FailPending(new IOException("OBS disconnected the WebSocket connection."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _terminalError = ex as ObsWebSocketException;
            FailPending(ex is IOException ? ex : new IOException("OBS WebSocket connection failed.", ex));
            _lifetime.Cancel();
        }
    }

    private async Task<IOException> DisconnectedErrorAsync(string message)
    {
        // A peer close can change the socket state before the receiver records its reason.
        if (_receiver is { } receiver) await receiver.ConfigureAwait(false);
        return _terminalError ?? new IOException(message);
    }

    private void HandleEvent(JsonElement root)
    {
        var data = root.GetProperty("d");
        var eventType = GetString(data, "eventType");
        if (eventType is not ("ReplayBufferSaved" or "RecordStateChanged" or "RecordFileChanged")) return;
        var eventData = data.GetProperty("eventData");
        if (eventType == "RecordFileChanged")
        {
            RecordFileChanged?.Invoke(this, new ObsRecordFileChangedEventArgs(GetNullableString(eventData, "newOutputPath") ?? ""));
            return;
        }
        if (string.Equals(eventType, "ReplayBufferSaved", StringComparison.Ordinal))
        {
            ReplayBufferSaved?.Invoke(this, new ObsReplayBufferSavedEventArgs(GetNullableString(eventData, "savedReplayPath") ?? ""));
            return;
        }
        if (!string.Equals(eventType, "RecordStateChanged", StringComparison.Ordinal)) return;
        var state = GetNullableString(eventData, "outputState");
        var paused = string.Equals(state, "OBS_WEBSOCKET_OUTPUT_PAUSED", StringComparison.Ordinal);
        RecordStateChanged?.Invoke(this, new ObsRecordStateChangedEventArgs(
            GetBool(eventData, "outputActive") || paused, paused, state, GetNullableString(eventData, "outputPath")));
    }

    private async Task SendJsonAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private static async Task<JsonDocument> ReceiveDocumentAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var frame = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(frame, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw (int?)result.CloseStatus switch
                {
                    AuthenticationFailed => new ObsWebSocketException("OBS rejected the WebSocket password. Enter it and connect again."),
                    SessionInvalidated => new ObsWebSocketException("OBS ended this WebSocket session. Select Connect to OBS to start a new one."),
                    _ => new IOException("OBS closed the WebSocket connection.")
                };
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new ObsWebSocketException("OBS sent an unsupported non-text WebSocket frame.");
            if (buffer.Length + result.Count > 1024 * 1024)
                throw new ObsWebSocketException("OBS sent a WebSocket message larger than 1 MiB.");
            buffer.Write(frame, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static string CreateAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static ObsWebSocketException ParseRequestError(JsonElement root)
    {
        var data = root.GetProperty("d");
        var status = data.GetProperty("requestStatus");
        var code = status.TryGetProperty("code", out var codeValue) ? codeValue.ToString() : "unknown";
        var comment = GetNullableString(status, "comment");
        return new ObsWebSocketException(string.IsNullOrWhiteSpace(comment)
            ? $"OBS rejected {GetString(data, "requestType")} (error {code})."
            : $"OBS rejected {GetString(data, "requestType")}: {comment} (error {code}).")
        {
            RequestStatusCode = codeValue.ValueKind == JsonValueKind.Number && codeValue.TryGetInt32(out var numericCode) ? numericCode : null
        };
    }

    private void FailPending(Exception exception)
    {
        foreach (var pending in _pending.Values) pending.TrySetException(exception);
    }

    private Task AbortSocketAsync()
    {
        // Disconnecting must also unblock a stalled receive/send or handshake.
        // Closing this transport does not stop OBS or its recording output.
        _lifetime.Cancel();
        _socket.Abort();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        FailPending(new IOException("OBS WebSocket client was disconnected."));
        await AbortSocketAsync().ConfigureAwait(false);
        if (_receiver is not null)
        {
            try { await _receiver.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await _requestLock.WaitAsync().ConfigureAwait(false);
        _requestLock.Release();
        _socket.Dispose();
        _sendLock.Dispose();
        _requestLock.Dispose();
        _lifetime.Dispose();
    }

    private static int GetInt(JsonElement element, string property) => element.GetProperty(property).GetInt32();
    private static bool GetBool(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static string GetString(JsonElement element, string property) => GetNullableString(element, property) ?? "";
    private static string? GetNullableString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement EmptyObject()
    {
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }
}
