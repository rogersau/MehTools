using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JamesTools.Core.Recording;

internal static class ObsWebSocketTests
{
    public static async Task Run()
    {
        await AuthRequestsAndOutputParsing();
        await ReplayStatusAndControls();
        await ReplaySaveWaitsForNewEventAndSerializes();
        await ReplaySaveWithoutEventTimesOut();
        await ReplaySaveDisconnectAndRequestError();
        await RequestTimeout();
        await DisconnectFailsPendingRequest();
        await DisposeCancelsHandshake();
    }

    private static async Task ReplayStatusAndControls()
    {
        await using var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            foreach (var requestType in new[] { "GetReplayBufferStatus", "StartReplayBuffer", "StopReplayBuffer" })
            {
                using var request = await ReceiveAsync(socket);
                var data = request.RootElement.GetProperty("d");
                Assert(data.GetProperty("requestType").GetString() == requestType, $"wrong {requestType} request type");
                var id = data.GetProperty("requestId").GetString();
                var responseData = requestType == "GetReplayBufferStatus" ? new { outputActive = true } : new { outputActive = true };
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType, requestId = id, requestStatus = new { result = true, code = 100 }, responseData } }));
            }
        });
        await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await client.ConnectAsync(server.Port);
        Assert((await client.GetReplayBufferStatusAsync()).Active, "replay buffer active state was not parsed");
        await client.StartReplayBufferAsync();
        await client.StopReplayBufferAsync();
        await server.Completion;
    }

    private static async Task ReplaySaveWaitsForNewEventAndSerializes()
    {
        var paths = new[] { Path.Combine(Path.GetTempPath(), "obs-new-clip-1.mkv"), Path.Combine(Path.GetTempPath(), "obs-new-clip-2.mkv") };
        await using var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            for (var i = 0; i < paths.Length; i++)
            {
                using var request = await ReceiveAsync(socket);
                var data = request.RootElement.GetProperty("d");
                Assert(data.GetProperty("requestType").GetString() == "SaveReplayBuffer", "wrong replay save request type");
                var id = data.GetProperty("requestId").GetString();
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 5, d = new { eventType = "ReplayBufferSaved", eventData = new { savedReplayPath = paths[i] } } }));
                await Task.Delay(5); // Event may precede the response.
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "SaveReplayBuffer", requestId = id, requestStatus = new { result = true, code = 100 }, responseData = new { } } }));
            }
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });
        await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await client.ConnectAsync(server.Port);
        var saves = await Task.WhenAll(client.SaveReplayBufferAsync(), client.SaveReplayBufferAsync());
        Assert(saves.SequenceEqual(paths), "concurrent replay saves were not serialized with their own confirmed paths");
        await server.Completion;
    }

    private static async Task ReplaySaveWithoutEventTimesOut()
    {
        var stalePath = Path.Combine(Path.GetTempPath(), "obs-previous-clip.mkv");
        await using var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            using var request = await ReceiveAsync(socket);
            var data = request.RootElement.GetProperty("d");
            var id = data.GetProperty("requestId").GetString();
            await SendAsync(socket, "{\"op\":5,\"d\":{\"eventType\":\"InputRemoved\"}}");
            await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "SaveReplayBuffer", requestId = id, requestStatus = new { result = true, code = 100 }, responseData = new { savedReplayPath = stalePath } } }));
            await Task.Delay(500);
        });
        await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
        await client.ConnectAsync(server.Port);
        try
        {
            var result = await client.SaveReplayBufferAsync();
            throw new Exception("SaveReplayBuffer reported an unconfirmed stale response path: " + result);
        }
        catch (TimeoutException) { }
        Assert(!client.IsConnected, "uncertain replay save could misattribute a late event on the next attempt");
        await client.DisposeAsync();
        await server.Completion;
    }

    private static async Task ReplaySaveDisconnectAndRequestError()
    {
        await using (var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            using var request = await ReceiveAsync(socket);
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "mock disconnect", CancellationToken.None);
        }))
        {
            await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
            await client.ConnectAsync(server.Port);
            try { await client.SaveReplayBufferAsync(); throw new Exception("disconnected replay save succeeded"); }
            catch (IOException) { }
            await server.Completion;
        }
        await using (var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            using var request = await ReceiveAsync(socket);
            var data = request.RootElement.GetProperty("d");
            var id = data.GetProperty("requestId").GetString();
            await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "SaveReplayBuffer", requestId = id, requestStatus = new { result = false, code = 500, comment = "buffer inactive" }, responseData = new { savedReplayPath = "stale.mkv" } } }));
        }))
        {
            await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
            await client.ConnectAsync(server.Port);
            try { await client.SaveReplayBufferAsync(); throw new Exception("rejected replay save succeeded"); }
            catch (ObsWebSocketException ex) { Assert(ex.Message.Contains("buffer inactive"), "save rejection detail missing"); }
            await server.Completion;
        }
    }

    private static async Task AuthRequestsAndOutputParsing()
    {
        const string salt = "test-salt-value";
        const string challenge = "test-challenge-value";
        const string password = "private test password";
        var savedPath = Path.Combine(Path.GetTempPath(), "obs-recording-test.mkv");
        await using var server = new MockObsServer(async socket =>
        {
            await SendAsync(socket, JsonSerializer.Serialize(new { op = 0, d = new { rpcVersion = 1, authentication = new { salt, challenge } } }));
            using (var identify = await ReceiveAsync(socket))
            {
                var data = identify.RootElement.GetProperty("d");
                Assert(data.GetProperty("rpcVersion").GetInt32() == 1, "Identify did not select RPC v1");
                Assert(data.GetProperty("authentication").GetString() == Authentication(password, salt, challenge), "authentication challenge was computed incorrectly");
            }
            await SendAsync(socket, "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");

            using (var statusRequest = await ReceiveAsync(socket))
            {
                var d = statusRequest.RootElement.GetProperty("d");
                Assert(d.GetProperty("requestType").GetString() == "GetRecordStatus", "wrong status request type");
                var id = d.GetProperty("requestId").GetString();
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 5, d = new { eventType = "RecordStateChanged", eventData = new { outputActive = false, outputState = "OBS_WEBSOCKET_OUTPUT_STOPPED", outputPath = savedPath } } }));
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "GetRecordStatus", requestId = "unrelated-id", requestStatus = new { result = true, code = 100 }, responseData = new { } } }));
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "GetRecordStatus", requestId = id, requestStatus = new { result = true, code = 100 }, responseData = new { outputActive = true, outputPaused = false, outputDuration = 12345, outputBytes = 67890 } } }));
            }

            using (var stopRequest = await ReceiveAsync(socket))
            {
                var d = stopRequest.RootElement.GetProperty("d");
                Assert(d.GetProperty("requestType").GetString() == "StopRecord", "wrong stop request type");
                var id = d.GetProperty("requestId").GetString();
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "StopRecord", requestId = id, requestStatus = new { result = true, code = 100 }, responseData = new { outputPath = savedPath } } }));
            }

            foreach (var requestType in new[] { "PauseRecord", "ResumeRecord" })
            {
                using var controlRequest = await ReceiveAsync(socket);
                var d = controlRequest.RootElement.GetProperty("d");
                Assert(d.GetProperty("requestType").GetString() == requestType, $"wrong {requestType} request type");
                var id = d.GetProperty("requestId").GetString();
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType, requestId = id, requestStatus = new { result = true, code = 100 }, responseData = new { } } }));
            }

            using (var startRequest = await ReceiveAsync(socket))
            {
                var d = startRequest.RootElement.GetProperty("d");
                var id = d.GetProperty("requestId").GetString();
                await SendAsync(socket, JsonSerializer.Serialize(new { op = 7, d = new { requestType = "StartRecord", requestId = id, requestStatus = new { result = false, code = 500, comment = "encoder unavailable" }, responseData = new { } } }));
            }
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        await using var client = new ObsWebSocketClient();
        ObsRecordStateChangedEventArgs? stoppedEvent = null;
        client.RecordStateChanged += (_, e) => stoppedEvent = e;
        await client.ConnectAsync(server.Port, password);
        var status = await client.GetRecordStatusAsync();
        Assert(status.Active && !status.Paused && status.DurationMilliseconds == 12345 && status.Bytes == 67890, "GetRecordStatus fields were not parsed");
        Assert(stoppedEvent is { Active: false, OutputPath: var eventPath } && eventPath == savedPath, "stopped output path event was not captured");
        Assert(await client.StopRecordAsync() == savedPath, "StopRecord outputPath was not parsed");
        await client.PauseRecordAsync();
        await client.ResumeRecordAsync();
        try
        {
            await client.StartRecordAsync();
            throw new Exception("OBS request error was ignored");
        }
        catch (ObsWebSocketException ex)
        {
            Assert(ex.Message.Contains("encoder unavailable", StringComparison.Ordinal), "OBS error comment was not surfaced");
        }
        await server.Completion;
    }

    private static async Task RequestTimeout()
    {
        await using var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            using var request = await ReceiveAsync(socket);
            await Task.Delay(500);
            try { await SendAsync(socket, "{\"op\":7,\"d\":{\"requestType\":\"GetRecordStatus\",\"requestId\":\"late\",\"requestStatus\":{\"result\":true,\"code\":100},\"responseData\":{}}}"); }
            catch (WebSocketException) { }
        });
        await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
        await client.ConnectAsync(server.Port);
        try
        {
            await client.GetRecordStatusAsync();
            throw new Exception("request timeout was ignored");
        }
        catch (TimeoutException) { }
        await client.DisposeAsync();
        await server.Completion;
    }

    private static async Task DisconnectFailsPendingRequest()
    {
        await using var server = new MockObsServer(async socket =>
        {
            await HandshakeAsync(socket);
            using var request = await ReceiveAsync(socket);
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "mock disconnect", CancellationToken.None);
        });
        await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await client.ConnectAsync(server.Port);
        try
        {
            await client.GetRecordStatusAsync();
            throw new Exception("disconnect did not fail the pending request");
        }
        catch (IOException) { }
        await server.Completion;
        Assert(!client.IsConnected, "closed transport still reports connected");
    }

    private static async Task DisposeCancelsHandshake()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new MockObsServer(async socket =>
        {
            accepted.SetResult();
            await finish.Task;
        });
        await using var client = new ObsWebSocketClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        var connecting = client.ConnectAsync(server.Port);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!client.IsConnected, "unidentified socket reports ready for commands");
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                await connecting.WaitAsync(TimeSpan.FromSeconds(2));
                throw new Exception("disposed handshake unexpectedly succeeded");
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
            Assert(!client.IsConnected, "disposed socket reports connected");
        }
        finally { finish.TrySetResult(); }
        await server.Completion;
    }

    private static async Task HandshakeAsync(WebSocket socket)
    {
        await SendAsync(socket, "{\"op\":0,\"d\":{\"rpcVersion\":1}}");
        using var identify = await ReceiveAsync(socket);
        await SendAsync(socket, "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");
    }

    private static async Task SendAsync(WebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (json.Contains("authentication", StringComparison.Ordinal) && bytes.Length > 20)
        {
            await socket.SendAsync(bytes.AsMemory(0, 17), WebSocketMessageType.Text, false, CancellationToken.None);
            await socket.SendAsync(bytes.AsMemory(17), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        else await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("client closed early");
            memory.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(memory.ToArray());
    }

    private static string Authentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class MockObsServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public Task Completion { get; }

        public MockObsServer(Func<WebSocket, Task> handler)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            Completion = RunAsync(handler);
        }

        private async Task RunAsync(Func<WebSocket, Task> handler)
        {
            var context = await _listener.GetContextAsync();
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await handler(socket);
        }

        public ValueTask DisposeAsync()
        {
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
