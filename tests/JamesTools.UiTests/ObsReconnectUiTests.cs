using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using JamesTools;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;

internal static class ObsReconnectUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string FixturePassword = "synthetic test password";

    public static async Task Run(string data)
    {
        var passwordStore = typeof(MainWindow).Assembly.GetType("JamesTools.ObsPasswordStore")!;
        var read = passwordStore.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
        var forget = passwordStore.GetMethod("Forget", BindingFlags.Static | BindingFlags.NonPublic)!;
        int port;
        do
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        } while (read.Invoke(null, [port]) is not null);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var store = new SettingsStore(Path.Combine(data, "obs-reconnect"));
        store.Save(new AppSettings { ObsWebSocketPort = port, ShowNotifications = false });
        MainWindow? window = null;
        T Control<T>(string name) => (T)window!.FindName(name);
        string Status() => Control<TextBlock>("ObsConnectionStatus").Text;
        bool CanRecord() => Status().StartsWith("Connected", StringComparison.Ordinal) && Control<Button>("ObsStartButton").IsEnabled;
        try
        {
            window = CreateWindow(store.DirectoryPath);
            var close = new TaskCompletionSource<int>();
            var first = ServeAsync(listener, close, "first OBS", timeout.Token);
            Control<PasswordBox>("ObsWebSocketPasswordBox").Password = FixturePassword;
            Control<Button>("ObsConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try { await WaitForAsync(CanRecord, timeout.Token, "initial connection"); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"{ex.Message}. {Status()} {Control<TextBlock>("ObsRemoteError").Text}", ex);
            }
            Check(read.Invoke(null, [port]) is not null, "A successful OBS login did not save the requested password");

            close.SetResult(1001);
            await first;
            listener.Stop();
            await WaitForAsync(() => Status().StartsWith("Waiting for OBS", StringComparison.Ordinal), timeout.Token, "waiting after OBS closes");
            Check(Control<Button>("ObsDisconnectButton").Content as string == "Cancel connection" && Control<Button>("ObsConnectButton").Visibility == Visibility.Collapsed,
                "Restart should wait without asking for the password again");

            close = new TaskCompletionSource<int>();
            listener.Start();
            var second = ServeAsync(listener, close, "restarted OBS", timeout.Token);
            await WaitForAsync(CanRecord, timeout.Token, "recording controls after restart");

            close.SetResult(1001);
            await second;
            listener.Stop();
            await CloseWindowAsync(window);
            window = null;

            close = new TaskCompletionSource<int>();
            listener.Start();
            var third = ServeAsync(listener, close, "OBS after Meh Tools restarts", timeout.Token);
            window = CreateWindow(store.DirectoryPath);
            await WaitForAsync(CanRecord, timeout.Token, "startup connection using the saved password");

            close.SetResult(4011);
            await third;
            await WaitForAsync(() => Status() == "Not connected", timeout.Token, "session ended in OBS");
            Check(Control<Button>("ObsConnectButton").IsEnabled && Control<Button>("ObsConnectButton").Visibility == Visibility.Visible &&
                Control<Button>("ObsDisconnectButton").Visibility == Visibility.Collapsed,
                "A session ended by OBS must require an explicit new connection");
            var saved = read.Invoke(null, [port]);
            Check(saved?.GetType().GetProperty("AutoConnect")?.GetValue(saved) is false, "A session ended by OBS should pause startup connection and retain the saved password");
            Check(Control<PasswordBox>("ObsWebSocketPasswordBox").Password.Length == 0 && !File.ReadAllText(store.SettingsPath).Contains(FixturePassword, StringComparison.Ordinal),
                "OBS password must stay out of saved settings and the password field");
            Control<Button>("ObsForgetPasswordButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(read.Invoke(null, [port]) is null, "Forget saved password did not remove the Windows credential");
            Console.WriteLine("PASS OBS reconnect, saved password across Meh Tools restart, explicit session end and forget");
        }
        finally
        {
            try { if (window is not null) await CloseWindowAsync(window); }
            finally { forget.Invoke(null, [port]); }
        }
    }

    private static MainWindow CreateWindow(string directory) => (MainWindow)Activator.CreateInstance(typeof(MainWindow), Private, null, [directory], null)!;

    internal static async Task CloseWindowAsync(MainWindow window)
    {
        typeof(MainWindow).GetField("_exiting", Private)!.SetValue(window, true);
        await (Task)typeof(MainWindow).GetMethod("DisconnectObsAsync", Private)!.Invoke(window, [false])!;
        await ((AutomationService)typeof(MainWindow).GetField("_automation", Private)!.GetValue(window)!).DisposeAsync();
        ((IDisposable)typeof(MainWindow).GetField("_tray", Private)!.GetValue(window)!).Dispose();
        window.Close();
    }

    private static async Task ServeAsync(HttpListener listener, TaskCompletionSource<int> close, string challenge, CancellationToken token)
    {
        var context = await listener.GetContextAsync().WaitAsync(token);
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        const string salt = "fixture salt";
        await SendAsync(socket, new { op = 0, d = new { rpcVersion = 1, authentication = new { salt, challenge } } }, token);
        using (var identify = await ReadAsync(socket, token))
        {
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(FixturePassword + salt)));
            var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
            Check(identify.RootElement.GetProperty("d").GetProperty("authentication").GetString() == expected, "OBS authentication did not use the same password after restart");
        }
        await SendAsync(socket, new { op = 2, d = new { negotiatedRpcVersion = 1 } }, token);
        while (true)
        {
            using var request = await ReadAsync(socket, token);
            if (close.Task.IsCompleted)
            {
                await socket.CloseOutputAsync((WebSocketCloseStatus)await close.Task, "fixture close", token);
                return;
            }
            var data = request.RootElement.GetProperty("d");
            await SendAsync(socket, new
            {
                op = 7, d = new
                {
                    requestType = data.GetProperty("requestType").GetString(), requestId = data.GetProperty("requestId").GetString(),
                    requestStatus = new { result = true, code = 100 },
                    responseData = new { outputActive = false, outputPaused = false, outputDuration = 0, outputBytes = 0 }
                }
            }, token);
        }
    }

    private static async Task<JsonDocument> ReadAsync(WebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[2048];
        WebSocketReceiveResult message;
        do
        {
            message = await socket.ReceiveAsync(buffer, token);
            if (message.MessageType == WebSocketMessageType.Close) throw new IOException("Client ended the test connection early");
            data.Write(buffer, 0, message.Count);
        } while (!message.EndOfMessage);
        return JsonDocument.Parse(data.ToArray());
    }

    private static Task SendAsync(WebSocket socket, object message, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, token);

    private static async Task WaitForAsync(Func<bool> ready, CancellationToken token, string state)
    {
        try { while (!ready()) await Task.Delay(50, token); }
        catch (OperationCanceledException) { throw new TimeoutException("Meh Tools did not reach " + state); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
