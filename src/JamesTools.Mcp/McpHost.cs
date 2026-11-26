using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JamesTools.Core.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JamesTools.Mcp;

public static class McpHost
{
    /// <summary>The caller owns injected services; registration never disposes these instances.</summary>
    public static IMcpServerBuilder AddDiagnostics(IServiceCollection services, IDiagnosticBackend backend, IDiagnosticSessions sessions)
    {
        services.AddSingleton(backend);
        services.AddSingleton(sessions);
        services.TryAddSingleton<DiagnosticAccess>();
        ToolRegistration.AddTools(services);
        var requestPolicy = new RequestPolicy();
        return services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "MehTools", Version = "1.0.0" };
            options.InitializationTimeout = TimeSpan.FromSeconds(15);
            options.ServerInstructions = "Windows diagnostic evidence only. Consult meh://capabilities. Null measurements indicate missing evidence; inspect statuses and limitations. All timestamps are UTC.";
        }).WithResources<DiagnosticResources>()
          .WithRequestFilters(filters =>
          {
              filters.AddCallToolFilter(next => async (context, ct) =>
              {
                  try
                  {
                      RequestPolicy.ValidateArguments(context.Params);
                      var result = await next(context, ct);
                      if (result.IsError == true)
                      {
                          // Preserve only our explicitly safe structured errors. SDK binding failures
                          // are otherwise collapsed to the same bounded public input error.
                          var safe = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                          if (!RequestPolicy.IsSafeError(safe)) return RequestPolicy.ToolError(DiagnosticAccess.Error("invalid_input", "Tool input could not be processed."));
                      }
                      RequestPolicy.CheckSize(result);
                      return result;
                  }
                  catch (Exception ex) { return RequestPolicy.ToolError(DiagnosticAccess.ToSafeException(ex, ct)); }
              });
              filters.AddReadResourceFilter(next => async (context, ct) =>
              {
                  try
                  {
                      if (!RequestPolicy.Resources.Contains(context.Params.Uri)) throw DiagnosticAccess.Error("unsupported", "Unknown diagnostic resource.");
                      var result = await next(context, ct); RequestPolicy.CheckSize(result); return result;
                  }
                  catch (Exception ex) { throw DiagnosticAccess.ToSafeException(ex, ct); }
              });
          })
          .WithMessageFilters(filters =>
          {
              filters.AddIncomingFilter(next => async (context, ct) =>
              {
                  // Cancellation notifications must still pass when operation capacity is reached.
                  if (context.JsonRpcMessage is not JsonRpcRequest) { await next(context, ct); return; }
                  if (!requestPolicy.Dispatch.Wait(0)) throw new McpProtocolException("unavailable: request capacity reached", McpErrorCode.InternalError);
                  try { await next(context, ct); }
                  finally { requestPolicy.Dispatch.Release(); }
              });
              filters.AddOutgoingFilter(next => async (context, ct) =>
              {
                  RequestPolicy.CheckSize(context.JsonRpcMessage);
                  await next(context, ct);
              });
          });
    }

    public static IHost BuildStdioHost(HostApplicationBuilder builder, Stream input, Stream output)
    {
        ConfigureLogging(builder.Logging);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        // This transport is the SDK stdio implementation with bounded streams around it.
        builder.Services.AddMcpServer().WithStreamServerTransport(new BoundedInputStream(input), new BoundedOutputStream(output));
        return builder.Build();
    }

    public static WebApplication BuildHttpHost(WebApplicationBuilder builder, HttpConfiguration config)
    {
        // The explicit MCP config is the sole listener authority. In particular, ignore
        // ASPNETCORE_URLS and inherited Kestrel endpoints from environment/appsettings.
        builder.Configuration.Sources.Clear();
        ConfigurationStore.Validate(new() { Http = config });
        var policy = new HttpAccessPolicy(config, Environment.GetEnvironmentVariable(config.TokenEnvironmentVariable));
        if (config.CertificatePath is not null)
        {
            builder.Services.AddSingleton(provider =>
            {
                var password = config.CertificatePasswordEnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(config.CertificatePasswordEnvironmentVariable);
                // Windows Schannel needs a user key container; EphemeralKeySet cannot perform
                // server TLS on supported Windows hosts. Do not persist or export the imported key.
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(config.CertificatePath, password, X509KeyStorageFlags.UserKeySet);
                if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                {
                    certificate.Dispose();
                    throw new McpConfigurationException("HTTPS certificate is not currently valid or lacks a private key.");
                }
                return certificate;
            });
        }
        ConfigureLogging(builder.Logging);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = BoundedInputStream.MaximumRequestBytes;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(config.BindAddress, config.Port, listen =>
            {
                if (config.CertificatePath is not null)
                {
                    // Factory-created singleton is disposed with the application's service provider.
                    listen.UseHttps(options.ApplicationServices.GetRequiredService<X509Certificate2>());
                }
            });
        });
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateless;
        });
        var app = builder.Build();
        var requests = new SemaphoreSlim(8, 8);
        app.Use(async (context, next) =>
        {
            var status = policy.Authorize(context.Request);
            if (status != 200)
            {
                context.Response.StatusCode = status;
                if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            if (!requests.Wait(0)) { context.Response.StatusCode = 429; return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var originalAbort = context.RequestAborted;
            var originalBody = context.Request.Body;
            context.RequestAborted = timeout.Token;
            try
            {
                // Buffer authenticated bodies only; reject chunked oversize requests as well.
                if (context.Request.ContentLength > BoundedInputStream.MaximumRequestBytes) { context.Response.StatusCode = 413; return; }
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await originalBody.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    if (body.Length + read > BoundedInputStream.MaximumRequestBytes) { context.Response.StatusCode = 413; return; }
                    body.Write(buffer, 0, read);
                }
                body.Position = 0;
                context.Request.Body = body;
                await next(context);
            }
            catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 408; else context.Abort(); }
            catch (BadHttpRequestException ex) { if (!context.Response.HasStarted) context.Response.StatusCode = ex.StatusCode; else context.Abort(); }
            finally { context.Request.Body = originalBody; context.RequestAborted = originalAbort; requests.Release(); }
        });
        app.MapMcp("/mcp");
        return app;
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        logging.SetMinimumLevel(LogLevel.Warning);
    }
}

public sealed class HttpAccessPolicy
{
    private readonly byte[] _tokenDigest;
    private readonly IReadOnlySet<string> _hosts;
    private readonly HashSet<string> _origins;
    private readonly int _port;

    public HttpAccessPolicy(HttpConfiguration config, string? token)
    {
        if (token is null || Encoding.UTF8.GetByteCount(token) is < 32 or > 4096 || token.Any(c => c < 0x21 || c > 0x7e))
            throw new McpConfigurationException("HTTP requires a bearer token of 32–4096 visible ASCII bytes from the configured environment variable.");
        _tokenDigest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _hosts = config.GetAllowedHosts();
        _origins = new(config.AllowedOrigins, StringComparer.Ordinal);
        _port = config.Port;
    }

    public int Authorize(HttpRequest request)
    {
        // Ignore forwarded headers entirely. The terminating proxy must preserve an allowlisted Host.
        var host = request.Host.Host.Trim('[', ']');
        if (!_hosts.Contains(host) || (request.Host.Port is { } port && port != _port && port != 443)) return 403;
        var origin = request.Headers.Origin;
        if (origin.Count > 1 || (origin.Count == 1 && !_origins.Contains(origin[0]!))) return 403;
        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1) return 401;
        var value = authorization[0];
        if (value is null || value.Length > 4103 || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return 401;
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(value[7..]));
        return CryptographicOperations.FixedTimeEquals(candidate, _tokenDigest) ? 200 : 401;
    }
}

internal sealed class RequestPolicy
{
    public static IReadOnlySet<string> Resources { get; } = new HashSet<string>(["meh://inventory", "meh://sensors", "meh://capabilities", "meh://report/latest", "meh://sessions", "meh://investigations"], StringComparer.Ordinal);
    public SemaphoreSlim Dispatch { get; } = new(8, 8);
    private static readonly IReadOnlyDictionary<string, string[]> Arguments = new Dictionary<string, string[]>
    {
        ["get_system_info"] = [], ["get_live_metrics"] = [], ["run_diagnostics"] = ["category"],
        ["get_historical_metrics"] = ["fromUtc", "toUtc", "metrics", "limit", "comparePrevious"],
        ["inspect_game"] = ["game"], ["get_event_logs"] = ["fromUtc", "toUtc", "source", "severity", "eventIds", "category", "limit"],
        ["get_health_report"] = [], ["start_diagnostic_session"] = ["duration", "profile", "samplingIntervalSeconds"],
        ["get_diagnostic_session"] = ["id"], ["stop_diagnostic_session"] = ["id"],
        ["start_stutter_investigation"] = ["duration", "processId", "deepInvestigation", "deepUsbTracing", "stutterThresholdMs"],
        ["stop_stutter_investigation"] = ["id"], ["get_investigation_report"] = ["id"],
        ["list_investigation_devices"] = ["id", "offset", "limit"],
        ["get_recent_device_events"] = ["id", "offset", "limit", "source", "category"],
        ["get_performance_spikes"] = ["id", "offset", "limit"], ["get_stutter_correlations"] = ["id", "offset", "limit"],
        ["compare_investigation_sessions"] = ["beforeId", "afterId", "changeDescription"]
    };

    public static void ValidateArguments(CallToolRequestParams parameters)
    {
        if (!Arguments.TryGetValue(parameters.Name, out var allowed)) throw DiagnosticAccess.Error("unsupported", "Unknown diagnostic tool.");
        if (parameters.Arguments?.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)) == true) throw new ArgumentException();
    }

    public static bool IsSafeError(string? text)
    {
        if (text is null || text.Length > 2048) return false;
        try
        {
            var error = JsonSerializer.Deserialize<SafeError>(text, DiagnosticAccess.Json);
            return error?.SchemaVersion == 1 && error.Status is "invalid_input" or "unsupported" or "permission_denied" or "unavailable" or "timeout" or "cancelled" or "failure";
        }
        catch (JsonException) { return false; }
    }

    public static CallToolResult ToolError(McpException exception) => new() { IsError = true, Content = [new TextContentBlock { Text = exception.Message }] };
    public static void CheckSize<T>(T result)
    {
        using var sink = new BoundedBuffer(DiagnosticAccess.MaximumPayloadBytes - 8192);
        JsonSerializer.Serialize(sink, result, DiagnosticAccess.Json);
    }
}
