using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using JamesTools.Core.Mcp;
using JamesTools.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace JamesTools.Mcp.Tests;

internal static class TransportTests
{
    internal static async Task StdioAsync()
    {
        var failures = new List<Exception>();
        foreach (var scenario in new[] { "normal", "oversized", "failure", "saturated" })
        {
            using var directory = new TestDirectory();
            var errors = new ConcurrentQueue<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var transport = new StdioClientTransport(new()
            {
                Command = "dotnet",
                Arguments = [Assembly.GetExecutingAssembly().Location, "--fixture-stdio", directory.Path, scenario],
                ShutdownTimeout = TimeSpan.FromSeconds(12),
                StandardErrorLines = line => { if (errors.Count < 12) errors.Enqueue(line); }
            });
            try
            {
                await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
                {
                    if (scenario is "normal" or "saturated") await EndpointTests.RunAsync(client, false, timeout.Token, scenario == "saturated" ? LargeSessionFixture.Id : null);
                    else await AssertSafeFailureAsync(client, scenario == "failure" ? "failure" : "unavailable", timeout.Token);
                }
                Check.That(File.Exists(Path.Combine(directory.Path, "disconnected")), "Stdio client disposal did not gracefully stop/dispose the server.");
            }
            catch (Exception ex) { failures.Add(new InvalidOperationException($"Stdio {scenario}: {string.Join(Environment.NewLine, errors)}", ex)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    internal static async Task HttpAsync()
    {
        using var directory = new TestDirectory();
        using var backend = new FixtureBackend();
        LargeSessionFixture.Write(Path.Combine(directory.Path, "sessions"));
        await using var sessions = new DiagnosticSessionService(backend, Path.Combine(directory.Path, "sessions"));
        await WithHttpAsync(backend, sessions, async (client, config, token, ct) =>
        {
            Exception? protocolFailure = null;
            try { await EndpointTests.RunAsync(client, false, ct, LargeSessionFixture.Id); }
            catch (Exception ex) { protocolFailure = ex; }
            await SecurityTests.HttpAsync(config, token, ct);
            backend.Oversized = true;
            try { await AssertSafeFailureAsync(client, "unavailable", ct); }
            finally { backend.Oversized = false; }
            backend.Fail = true;
            try { await AssertSafeFailureAsync(client, "failure", ct); }
            finally { backend.Fail = false; }
            backend.InventoryGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var beforeCancellation = backend.InventoryReads;
            using (var cancelRequest = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pending = client.CallToolAsync("get_system_info", cancellationToken: cancelRequest.Token).AsTask();
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(8));
                    while (Volatile.Read(ref backend.InventoryReads) == beforeCancellation) await Task.Delay(20, wait.Token);
                    cancelRequest.Cancel();
                    await Check.ThrowsAsync<OperationCanceledException>(() => pending);
                }
                finally { backend.InventoryGate.TrySetResult(); backend.InventoryGate = null; }
            }
            _ = await EndpointTests.CallAsync(client, "get_system_info", [], ct);
            // Hold the actual backend calls so admission control is deterministic.
            backend.InventoryGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var baseline = backend.InventoryReads;
            async Task<CallToolResult?> SubmitAsync()
            {
                try { return await client.CallToolAsync("get_system_info", cancellationToken: ct); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) { return null; }
            }
            var calls = Enumerable.Range(0, 12).Select(_ => SubmitAsync()).ToArray();
            try
            {
                using var admissionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                admissionTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                while (Volatile.Read(ref backend.InventoryReads) < baseline + 4) await Task.Delay(20, admissionTimeout.Token);
                await Task.Delay(150, ct);
                Check.That(backend.InventoryReads == baseline + 4, "Operation capacity exceeded four backend calls.");
            }
            finally { backend.InventoryGate.TrySetResult(); backend.InventoryGate = null; }
            var outcomes = await Task.WhenAll(calls);
            Check.That(outcomes.Any(r => r is null || r.IsError == true) && outcomes.Any(r => r is { IsError: not true }), "Concurrent requests did not exercise bounded admission.");
            _ = await EndpointTests.CallAsync(client, "get_system_info", [], ct);
            if (protocolFailure is not null) throw new InvalidOperationException("HTTP protocol regression; remaining HTTP security/admission checks completed.", protocolFailure);
        });
    }

    internal static async Task HardwareAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("--hardware requires Windows.");
        using var directory = new TestDirectory();
        var configPath = Path.Combine(directory.Path, "config.json");
        ConfigurationStore.Save(configPath, new() { Enabled = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var failures = new List<Exception>();
        var transport = new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = [typeof(McpHost).Assembly.Location, "--config", configPath],
            ShutdownTimeout = TimeSpan.FromSeconds(12)
        });
        try
        {
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            await EndpointTests.RunAsync(client, true, timeout.Token);
        }
        catch (Exception ex) { failures.Add(new InvalidOperationException("Production Windows stdio", ex)); }
        using var backend = new WindowsDiagnosticBackend(ConfigurationStore.AppDataDirectory);
        await using var sessions = new DiagnosticSessionService(backend, Path.Combine(directory.Path, "hardware-sessions"));
        try { await WithHttpAsync(backend, sessions, (client, _, _, ct) => EndpointTests.RunAsync(client, true, ct)); }
        catch (Exception ex) { failures.Add(new InvalidOperationException("Production Windows HTTP", ex)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static async Task WithHttpAsync(IDiagnosticBackend backend, IDiagnosticSessions sessions,
        Func<McpClient, HttpConfiguration, string, CancellationToken, Task> run)
    {
        var variable = "MEHTOOLS_MCP_TEST_" + Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Environment.SetEnvironmentVariable(variable, token);
        var config = new HttpConfiguration { Port = FreePort(), TokenEnvironmentVariable = variable, AllowedOrigins = ["https://allowed.example"] };
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(McpHost).Assembly.FullName });
            // Invalid-resource/cancellation cases intentionally produce SDK warnings; the
            // assertions below inspect their wire results directly.
            builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
            McpHost.AddDiagnostics(builder.Services, backend, sessions);
            await using var app = McpHost.BuildHttpHost(builder, config);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await app.StartAsync(timeout.Token);
            try
            {
                var transport = new HttpClientTransport(new()
                {
                    Endpoint = new Uri(config.Endpoint), TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
                });
                await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
                    await run(client, config, token, timeout.Token);
                // Disposing one client must not stop a shared HTTP server.
                await using var reconnect = await McpClient.CreateAsync(new HttpClientTransport(new()
                {
                    Endpoint = new Uri(config.Endpoint), TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
                }), cancellationToken: timeout.Token);
                Check.That((await reconnect.ListToolsAsync(cancellationToken: timeout.Token)).Count == 10, "HTTP reconnect after clean disconnect failed.");
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task AssertSafeFailureAsync(McpClient client, string expectedStatus, CancellationToken ct)
    {
        var result = await client.CallToolAsync("get_system_info", cancellationToken: ct);
        Check.That(result.IsError == true, "Backend failure/oversized output was reported as success.");
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        Check.That(!text.Contains("sensitive internal path") && text.Length < 2048, "Backend internals escaped safe error boundary.");
        var error = JsonSerializer.Deserialize<SafeError>(text, Check.Json)!;
        Check.That(error.Status == expectedStatus, $"Expected {expectedStatus}; received {error.Status}.");
        Check.Utc(error.TimestampUtc);
    }
}
