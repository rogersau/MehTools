using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JamesTools.Core.Mcp;
using JamesTools.Mcp;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;

namespace JamesTools.Mcp.Tests;

internal static class TlsTests
{
    internal static async Task RunAsync()
    {
        using var directory = new TestDirectory();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var variable = "MEHTOOLS_MCP_TLS_" + Guid.NewGuid().ToString("N");
        var passwordVariable = variable + "_PFX";
        var pfx = Path.Combine(directory.Path, "ephemeral.pfx");
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
        Environment.SetEnvironmentVariable(variable, token);
        Environment.SetEnvironmentVariable(passwordVariable, password);
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var config = new HttpConfiguration
            {
                Port = port, TokenEnvironmentVariable = variable,
                CertificatePath = pfx, CertificatePasswordEnvironmentVariable = passwordVariable
            };
            using var backend = new FixtureBackend();
            await using var sessions = new DiagnosticSessionService(backend, directory.Path);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(McpHost).Assembly.FullName });
            McpHost.AddDiagnostics(builder.Services, backend, sessions);
            await using var host = McpHost.BuildHttpHost(builder, config);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StartAsync(timeout.Token);
            try
            {
                // Trust applies only to this client and exactly this ephemeral certificate.
                // Hostname validation still applies; no OS certificate store is modified.
                var validated = 0;
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, actual, _, errors) =>
                    {
                        Interlocked.Increment(ref validated);
                        return actual is not null && actual.RawData.AsSpan().SequenceEqual(certificate.RawData)
                            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
                    }
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                foreach (var supplied in new string?[] { null, new('z', 64) })
                {
                    using var unauthorized = new HttpRequestMessage(HttpMethod.Post, config.Endpoint)
                    {
                        Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json")
                    };
                    if (supplied is not null) unauthorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", supplied);
                    using var rejection = await http.SendAsync(unauthorized, timeout.Token);
                    Check.That(rejection.StatusCode == HttpStatusCode.Unauthorized, "HTTPS did not reject missing/wrong bearer credentials.");
                }
                var transport = new HttpClientTransport(new()
                {
                    Endpoint = new Uri(config.Endpoint), TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
                }, http, ownsHttpClient: false);
                await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                Check.That(client.ServerInfo?.Name == "MehTools" && (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count == EndpointTests.Tools.Length, "HTTPS initialization/tool discovery failed.");
                var result = Check.Read<SystemInventory>(await EndpointTests.CallAsync(client, "get_system_info", [], timeout.Token));
                Check.That(result.Components.Single().Name == "Fixture CPU", "HTTPS invocation did not reach the injected backend.");
                Check.That(validated > 0, "HTTPS did not validate the pinned test certificate.");
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }
}
