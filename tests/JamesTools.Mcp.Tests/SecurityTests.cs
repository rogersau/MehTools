using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using JamesTools.Mcp;

namespace JamesTools.Mcp.Tests;

internal static class SecurityTests
{
    private const int MaximumRequestBytes = 64 * 1024;
    internal static async Task ConfigurationAsync()
    {
        void Reject(HttpConfiguration http)
        {
            try { ConfigurationStore.Validate(new() { Http = http }); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Unsafe HTTP configuration accepted.");
        }
        Reject(new() { Address = "0.0.0.0" });
        Reject(new() { Address = "example.com" });
        Reject(new() { Port = 80 });
        Reject(new() { AllowedHosts = ["*.example.com"] });
        Reject(new() { AllowedHosts = ["https://example.com"] });
        Reject(new() { AllowedOrigins = ["https://example.com/path"] });
        Reject(new() { AllowedOrigins = ["https://user:password@example.com"] });
        Reject(new() { TokenEnvironmentVariable = "NOT A VARIABLE" });
        foreach (var token in new string?[] { null, "", "short", new('x', 4097), new(' ', 32) })
        {
            try { _ = new HttpAccessPolicy(new(), token); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Unsafe HTTP bearer token accepted.");
        }
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        ConfigurationStore.Save(path, new() { Enabled = true });
        Check.That(ConfigurationStore.Load(path).Enabled, "Configuration round trip failed.");
        var configText = await File.ReadAllTextAsync(path);
        Check.That(!configText.Contains("bindAddress") && !configText.Contains("endpoint"), "Computed HTTP properties leaked into persisted configuration.");
        await AdministrationAsync(path);
        File.WriteAllText(path, "{\"enabled\":true,\"unknown\":123}");
        try { _ = ConfigurationStore.Load(path); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Unknown configuration members accepted.");
    }

    private static async Task AdministrationAsync(string path)
    {
        foreach (var command in new[] { "--disable", "--status", "--enable", "--status" })
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { typeof(McpHost).Assembly.Location, "--config", path, command }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MCP administration command.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            Check.That(process.ExitCode == 0, command + " failed: " + await stderr);
            var result = JsonDocument.Parse(await stdout).RootElement;
            Check.That(result.GetProperty("schemaVersion").GetInt32() == 1, "Administrative response schema version.");
            if (command == "--status")
            {
                Check.That(result.GetProperty("enabled").GetBoolean() == ConfigurationStore.Load(path).Enabled, "Status enablement disagrees with saved configuration.");
                Check.That(result.GetProperty("hosts").GetArrayLength() == 0, "Status falsely reported a host for a fresh test configuration.");
            }
            else Check.That(ConfigurationStore.Load(path).Enabled == (command == "--enable"), "Administrative command did not persist enablement.");
        }
    }

    internal static async Task HttpAsync(HttpConfiguration config, string token, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        async Task StatusAsync(HttpStatusCode expected, string? bearer, string? host = null, string? origin = null, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (host is not null) request.Headers.Host = host;
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            request.Content = content ?? new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) when (content is ChunkedContent && expected == HttpStatusCode.RequestEntityTooLarge)
            {
                // Kestrel can terminate an over-budget chunked upload before HttpClient has
                // finished writing it. This is a rejected request; the next assertion checks
                // that a fresh, valid connection still succeeds.
                Console.WriteLine("OBSERVED oversized chunked HTTP upload rejected by connection close: " + ex.HttpRequestError);
                return;
            }
            catch (HttpRequestException ex) { throw new InvalidOperationException($"HTTP expected {(int)expected}; content={content?.GetType().Name ?? "JSON"}", ex); }
            using var ownedResponse = response;
            Check.That(response.StatusCode == expected, $"HTTP security expected {(int)expected}, got {(int)response.StatusCode} (host={host}, origin={origin}).");
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            Check.That(body.Length <= EndpointTests.MaximumResponseBytes, "HTTP error exceeded response budget.");
            Check.That(!Encoding.UTF8.GetString(body).Contains(token, StringComparison.Ordinal), "HTTP response disclosed bearer token.");
        }
        await StatusAsync(HttpStatusCode.Unauthorized, null);
        await StatusAsync(HttpStatusCode.Unauthorized, new string('z', 64));
        await StatusAsync(HttpStatusCode.Forbidden, token, host: "attacker.example");
        await StatusAsync(HttpStatusCode.Forbidden, token, host: "127.0.0.1:1");
        await StatusAsync(HttpStatusCode.Forbidden, token, origin: "https://attacker.example");
        await StatusAsync(HttpStatusCode.Forbidden, token, origin: "null");
        await StatusAsync(HttpStatusCode.Forbidden, token, origin: "https://allowed.example.attacker.example");
        await StatusAsync(HttpStatusCode.OK, token, origin: "https://allowed.example");
        await StatusAsync(HttpStatusCode.RequestEntityTooLarge, token, content: new StringContent(new string('x', MaximumRequestBytes + 1), Encoding.UTF8, "application/json"));
        await StatusAsync(HttpStatusCode.RequestEntityTooLarge, token, content: new ChunkedContent());
        await StatusAsync(HttpStatusCode.OK, token);
    }

    private sealed class ChunkedContent : HttpContent
    {
        internal ChunkedContent() => Headers.ContentType = new("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            Array.Fill(buffer, (byte)'x');
            for (var i = 0; i < 9; i++) await stream.WriteAsync(buffer);
        }
    }
}
