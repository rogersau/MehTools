using System.Text;
using System.Text.Json;
using JamesTools.Core.Mcp;
using JamesTools.Core.Diagnostics;
using Json.Schema;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace JamesTools.Mcp.Tests;

internal static class EndpointTests
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal static readonly string[] Tools =
    ["get_system_info", "get_live_metrics", "run_diagnostics", "inspect_game", "get_event_logs", "get_health_report",
     "start_diagnostic_session", "get_diagnostic_session", "stop_diagnostic_session", "get_historical_metrics",
     "start_stutter_investigation", "stop_stutter_investigation", "list_investigation_devices", "get_recent_device_events",
     "get_performance_spikes", "get_stutter_correlations", "get_investigation_report", "compare_investigation_sessions"];

    internal static async Task RunAsync(McpClient client, bool hardware, CancellationToken ct, Guid? fullSessionId = null)
    {
        SchemaFailures.Value = [];
        ValidationFailures.Value = [];
        Check.That(client.ServerInfo is not null && client.ServerCapabilities.Tools is not null && client.ServerCapabilities.Resources is not null,
            "Initialization must advertise tools/resources and server identity.");
        await Check.ThrowsAsync<McpException>(() => client.ReadResourceAsync("meh://report/latest", cancellationToken: ct).AsTask());
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Schemas.Value = tools.ToDictionary(t => t.Name, t => JsonSchema.FromText(t.ProtocolTool.OutputSchema?.GetRawText() ?? throw new InvalidOperationException(t.Name + ": missing output schema")));
        Check.That(tools.Select(t => t.Name).Order().SequenceEqual(Tools.Order()), "Expected all diagnostic tools: " + string.Join(", ", tools.Select(t => t.Name)));
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.InputSchema;
            Check.That(schema.GetProperty("type").GetString() == "object", $"{tool.Name}: object input schema required.");
            Check.That(tool.ProtocolTool.OutputSchema is { } output && output.ValueKind == JsonValueKind.Object,
                $"{tool.Name}: advertised output schema required.");
            Check.That(!string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name}: description required.");
        }

        var inventory = Check.Read<SystemInventory>(await CallAsync(client, "get_system_info", [], ct));
        Check.That(inventory.SchemaVersion == 1 && inventory.Components.Count > 0, "System inventory must contain components.");
        Check.Utc(inventory.TimestampUtc);
        var metrics = Check.Read<MetricsSnapshot>(await CallAsync(client, "get_live_metrics", [], ct));
        VerifyMetrics(metrics);
        if (!hardware)
        {
            foreach (var status in new[] { "unsupported", "permission_denied", "unavailable", "failure" })
                Check.That(metrics.Metrics.Any(m => m.Status == status && m.Value is null), $"Fixture {status} sensor did not survive transport.");
        }
        else
        {
            foreach (var name in new[] { "cpu.utilization", "memory.physical_used" })
                Check.That(metrics.Metrics.Any(m => m.Name == name), "Live normalization lost useful metric " + name);
            Console.WriteLine("OBSERVED hardware sensors: " + string.Join(", ", metrics.Metrics.GroupBy(m => m.Status).Select(g => $"{g.Key}={g.Count()}")));
        }
        var diagnostic = Check.Read<DiagnosticResult>(await CallAsync(client, "run_diagnostics", new() { ["category"] = "cpu" }, ct));
        Check.That(diagnostic.SchemaVersion == 1 && !string.IsNullOrEmpty(diagnostic.Status), "Diagnostic status is required.");
        Check.Utc(diagnostic.TimestampUtc);
        var game = Check.Read<GameInspection>(await CallAsync(client, "inspect_game", new() { ["game"] = "fixture-game" }, ct));
        Check.That(game.SchemaVersion == 1 && !string.IsNullOrEmpty(game.Status), "Inspection needs explicit availability.");
        Check.Utc(game.TimestampUtc);
        Check.That(game.Components.All(c => !string.IsNullOrWhiteSpace(c.RuntimeStatus)), "Installed components need separate runtime status.");
        Check.That(game.Components.Where(c => c.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).All(c => c.RuntimeStatus != "active"), "An installed DLL alone must not establish runtime activation.");
        var events = Check.Read<EventLogResult>(await CallAsync(client, "get_event_logs", new() { ["limit"] = 5 }, ct));
        Check.That(events.SchemaVersion == 1 && !string.IsNullOrEmpty(events.Status), "Event status/schema required.");
        Check.Utc(events.TimestampUtc);
        Check.That(events.Events.Count <= 5, "Event results exceed requested bound.");
        foreach (var item in events.Events) Check.Utc(item.TimestampUtc);
        var health = Check.Read<HealthReport>(await CallAsync(client, "get_health_report", [], ct));
        Check.That(health.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(health.Assessment), "Health assessment required.");
        Check.Utc(health.TimestampUtc);
        var started = Check.Read<DiagnosticSession>(await CallAsync(client, "start_diagnostic_session", new() { ["duration"] = 10, ["profile"] = "general", ["samplingIntervalSeconds"] = 1 }, ct));
        try
        {
            Check.That(started.Id != Guid.Empty, "Session id required.");
            Check.That(started.SchemaVersion == 1, "Session schema version.");
            Check.Utc(started.StartedUtc);
            var current = Check.Read<DiagnosticSession>(await CallAsync(client, "get_diagnostic_session", new() { ["id"] = started.Id.ToString() }, ct));
            Check.That(current.Id == started.Id, "Session lookup returned a different session.");
            foreach (var sample in current.Samples) VerifyMetrics(sample);
        }
        finally
        {
            var stopped = Check.Read<DiagnosticSession>(await CallAsync(client, "stop_diagnostic_session", new() { ["id"] = started.Id.ToString() }, ct));
            Check.That(stopped.EndedUtc is not null && stopped.Status is "stopped" or "completed", "Stop did not terminate session.");
            Check.Utc(stopped.EndedUtc!.Value);
        }
        var history = Check.Read<HistoricalMetrics>(await CallAsync(client, "get_historical_metrics", new() { ["limit"] = 5, ["comparePrevious"] = true }, ct));
        Check.That(history.SchemaVersion == 1 && history.Samples.Count <= 5, "History must respect requested limit.");
        foreach (var sample in history.Samples) VerifyMetrics(sample);

        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        Check.That(resources.Select(r => r.Uri).Order().SequenceEqual(new[] { "meh://inventory", "meh://sensors", "meh://capabilities", "meh://report/latest", "meh://sessions", "meh://investigations" }.Order()), "Static diagnostics resources do not match the documented contract.");
        foreach (var resource in resources)
        {
            var response = await client.ReadResourceAsync(resource.Uri, cancellationToken: ct);
            Check.That(response.Contents.Count > 0 && Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(response, Check.Json)) <= MaximumResponseBytes,
                $"Resource empty or oversized: {resource.Uri}");
        }
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: ct);
        Check.That(templates is not null, "Resource template listing failed.");
        await Check.ThrowsAsync<McpException>(() => client.ReadResourceAsync("file:///C:/Windows/win.ini", cancellationToken: ct).AsTask());
        await Check.ThrowsAsync<McpException>(() => client.ReadResourceAsync("meh://inventory/../../secret", cancellationToken: ct).AsTask());

        var maximum = Check.Read<DiagnosticSession>(await CallAsync(client, "start_diagnostic_session", new() { ["duration"] = 3600, ["profile"] = "gaming", ["samplingIntervalSeconds"] = 5 }, ct));
        try
        {
            var bounded = Check.Read<DiagnosticSession>(await CallAsync(client, "get_diagnostic_session", new() { ["id"] = maximum.Id.ToString() }, ct));
            Check.That(bounded.DurationSeconds == 3600 && bounded.Samples.Count <= 40, "Maximum-duration session exceeded snapshot bound.");
        }
        finally { _ = await CallAsync(client, "stop_diagnostic_session", new() { ["id"] = maximum.Id.ToString() }, ct); }

        if (fullSessionId is { } wideId)
        {
            var wide = Check.Read<DiagnosticSession>(await CallAsync(client, "get_diagnostic_session", new() { ["id"] = wideId.ToString() }, ct));
            Check.That(wide.DurationSeconds == 3600 && wide.Samples.Count == 40, "Full retained-window fixture was not returned intact.");
            Check.That(JsonSerializer.SerializeToUtf8Bytes(wide, Check.Json).Length <= 300 * 1024, "Maximum-duration full window exceeds domain budget.");
            Check.That(wide.Statistics.All(s => s.Count == 0 && s.Average is null), "Unsupported full-window fixture acquired fabricated statistics.");
        }

        await ErrorAsync(client, "no_such_tool", [], ct);
        await ErrorAsync(client, "get_system_info", new() { ["path"] = "C:\\Windows" }, ct);
        await DeniedGameAsync(client, "../../Windows/System32", ct);
        await DeniedGameAsync(client, "C:\\Windows\\System32\\cmd.exe", ct);
        await ErrorAsync(client, "run_diagnostics", new() { ["category"] = "powershell -Command whoami" }, ct);
        await ErrorAsync(client, "run_diagnostics", new() { ["category"] = 12 }, ct);
        await ErrorAsync(client, "run_diagnostics", [], ct);
        await ErrorAsync(client, "start_diagnostic_session", new() { ["duration"] = -1, ["profile"] = "general" }, ct);
        await ErrorAsync(client, "start_diagnostic_session", new() { ["duration"] = int.MaxValue, ["profile"] = "general" }, ct);
        await ErrorAsync(client, "start_diagnostic_session", new() { ["duration"] = "5", ["profile"] = "general" }, ct);
        await ErrorAsync(client, "get_diagnostic_session", new() { ["id"] = "../session" }, ct);
        await ErrorAsync(client, "get_diagnostic_session", new() { ["id"] = Guid.Empty.ToString() }, ct);
        await ErrorAsync(client, "get_event_logs", new() { ["limit"] = int.MaxValue }, ct);
        await ErrorAsync(client, "get_historical_metrics", new() { ["limit"] = -1 }, ct);
        await ErrorAsync(client, "get_historical_metrics", new() { ["metrics"] = new[] { "../../secrets" } }, ct);
        await ErrorAsync(client, "get_historical_metrics", new() { ["fromUtc"] = "2026-01-01T00:00:00Z", ["toUtc"] = "2026-01-09T00:00:00Z" }, ct);
        await ErrorAsync(client, "get_event_logs", new() { ["fromUtc"] = "2026-01-01T00:00:00+01:00" }, ct);
        await ErrorAsync(client, "get_event_logs", new() { ["eventIds"] = new[] { -1 } }, ct);
        await ErrorAsync(client, "get_event_logs", new() { ["severity"] = "custom xpath" }, ct);
        await ErrorAsync(client, "start_diagnostic_session", new() { ["duration"] = 5, ["profile"] = "unknown" }, ct);
        await ErrorAsync(client, "start_diagnostic_session", new() { ["duration"] = 5, ["profile"] = "general", ["samplingIntervalSeconds"] = 0 }, ct);
        await ErrorAsync(client, "start_diagnostic_session", new() { ["duration"] = 5, ["profile"] = "general", ["samplingIntervalSeconds"] = 31 }, ct);
        await ErrorAsync(client, "start_stutter_investigation", new() { ["duration"] = 301 }, ct);
        await ErrorAsync(client, "start_stutter_investigation", new() { ["duration"] = 5, ["deepUsbTracing"] = true }, ct);
        await ErrorAsync(client, "start_stutter_investigation", new() { ["duration"] = 5, ["executablePath"] = "C:/arbitrary.exe" }, ct);
        await ErrorAsync(client, "start_stutter_investigation", new() { ["duration"] = 5, ["presentMonPath"] = "C:/arbitrary.exe" }, ct);
        await ErrorAsync(client, "start_stutter_investigation", new() { ["duration"] = 5, ["outputPath"] = "C:/arbitrary" }, ct);
        await ErrorAsync(client, "get_recent_device_events", new() { ["id"] = Guid.NewGuid().ToString(), ["limit"] = 201 }, ct);
        if (!hardware) await InvestigationsAsync(client, ct);
        // An invalid request must leave the connection usable.
        _ = await CallAsync(client, "get_system_info", [], ct);
        var failures = SchemaFailures.Value!.Distinct().Concat(ValidationFailures.Value!).ToArray();
        Check.That(failures.Length == 0, string.Join("; ", failures));
    }

    static async Task InvestigationsAsync(McpClient client, CancellationToken ct)
    {
        var a = Check.Read<DiagnosticSession>(await CallAsync(client, "start_stutter_investigation", new() { ["duration"] = 30,
            ["processId"] = 4242, ["deepInvestigation"] = true, ["deepUsbTracing"] = true }, ct));
        // The normal get/stop tools own investigation sessions too. Final stop waits for startup and report persistence.
        a = Check.Read<DiagnosticSession>(await CallAsync(client, "stop_diagnostic_session", new() { ["id"] = a.Id.ToString() }, ct));
        var report = Check.Read<InvestigationReport>(await CallAsync(client, "get_investigation_report", new() { ["id"] = a.Id.ToString() }, ct));
        Check.That(report.Metrics.FrameCount == 3600 && report.Events.Count == 0 && report.Spikes.Count == 0 && report.Correlations.Single().EventCount == 600, "Report summary lost original statistics.");
        var events = Check.Read<InvestigationPage<InvestigationEvent>>(await CallAsync(client, "get_recent_device_events", new() { ["id"] = a.Id.ToString(), ["limit"] = 2, ["category"] = "USB" }, ct));
        Check.That(events.Items.Count == 2 && events.NextOffset == 2 && events.Coverage.EventsOmittedByLimit > 0, "Transport paging/omissions failed.");
        _ = await CallAsync(client, "get_recent_device_events", new() { ["id"] = a.Id.ToString(), ["offset"] = events.NextOffset, ["limit"] = 200 }, ct);
        _ = await CallAsync(client, "list_investigation_devices", new() { ["id"] = a.Id.ToString() }, ct);
        _ = await CallAsync(client, "get_performance_spikes", new() { ["id"] = a.Id.ToString(), ["limit"] = 200 }, ct);
        _ = await CallAsync(client, "get_stutter_correlations", new() { ["id"] = a.Id.ToString() }, ct);
        var full = Check.Read<DiagnosticSession>(await CallAsync(client, "get_diagnostic_session", new() { ["id"] = a.Id.ToString() }, ct));
        Check.That(full.Investigation!.Events.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(full).Length <= 300 * 1024, "Bounded full report unavailable.");
        var b = Check.Read<DiagnosticSession>(await CallAsync(client, "start_stutter_investigation", new() { ["duration"] = 20 }, ct));
        _ = await CallAsync(client, "stop_stutter_investigation", new() { ["id"] = b.Id.ToString() }, ct);
        _ = await CallAsync(client, "stop_stutter_investigation", new() { ["id"] = b.Id.ToString() }, ct);
        _ = await CallAsync(client, "compare_investigation_sessions", new() { ["beforeId"] = a.Id.ToString(), ["afterId"] = b.Id.ToString(), ["changeDescription"] = "One controlled local change" }, ct);
    }

    internal static void VerifyMetrics(MetricsSnapshot snapshot)
    {
        Check.That(snapshot.SchemaVersion == 1, "Metrics schema version.");
        Check.Utc(snapshot.TimestampUtc);
        foreach (var metric in snapshot.Metrics)
        {
            Check.Utc(metric.TimestampUtc);
            Check.That(!string.IsNullOrWhiteSpace(metric.Name) && (!string.IsNullOrWhiteSpace(metric.Unit) || metric.Name == "telemetry" && metric.Unit == "") && !string.IsNullOrWhiteSpace(metric.Source), "Metric identity/unit/source required.");
            Check.That(metric.Status is "available" or "unsupported" or "permission_denied" or "unavailable" or "failure", "Unexpected metric status: " + metric.Status);
            Check.That(metric.Value is null || double.IsFinite(metric.Value.Value), "Metric contains NaN or infinity.");
            if (metric.Status != "available") Check.That(metric.Value is null && !string.IsNullOrWhiteSpace(metric.Reason), "Missing sensor became zero or lost its reason: " + metric.Name);
            else Check.That(metric.Value is not null, "Available reading has no value: " + metric.Name);
        }
    }

    internal static async Task<JsonElement> CallAsync(McpClient client, string name, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: ct);
        Check.That(result.IsError != true, $"{name} returned error: {JsonSerializer.Serialize(result, Check.Json)}");
        Check.That(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, Check.Json)) <= MaximumResponseBytes, $"{name}: response exceeds 1 MiB.");
        Check.That(result.StructuredContent is not null, $"{name}: structured content required.");
        var structured = JsonSerializer.SerializeToElement(result.StructuredContent, Check.Json);
        if (Schemas.Value?.TryGetValue(name, out var schema) == true)
        {
            var evaluation = schema.Evaluate(structured, new() { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                var details = JsonSerializer.SerializeToElement(evaluation, Check.Json).GetProperty("details").EnumerateArray()
                    .Where(d => d.TryGetProperty("errors", out var errors) && (errors.TryGetProperty("type", out _) || errors.TryGetProperty("required", out _) || errors.TryGetProperty("enum", out _)))
                    .Take(8).Select(d => d.GetProperty("instanceLocation").GetString() + " " + d.GetProperty("errors").GetRawText());
                var failure = name + ": " + string.Join(" | ", details);
                SchemaFailures.Value?.Add(failure[..Math.Min(failure.Length, 1800)]);
            }
        }
        return structured;
    }

    private static async Task DeniedGameAsync(McpClient client, string game, CancellationToken ct)
    {
        try
        {
            var result = await client.CallToolAsync("inspect_game", new Dictionary<string, object?> { ["game"] = game }, cancellationToken: ct);
            if (result.IsError == true) return;
            Check.That(result.StructuredContent is not null, "Denied game request lacks explicit result.");
            var inspection = Check.Read<GameInspection>(JsonSerializer.SerializeToElement(result.StructuredContent, Check.Json));
            Check.That(inspection.Status is "permission_denied" or "unsupported", "Uncatalogued or unsafe game path was accepted: " + game);
        }
        catch (McpException) { }
    }

    internal static async Task ErrorAsync(McpClient client, string name, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        try
        {
            var result = await client.CallToolAsync(name, arguments, cancellationToken: ct);
            if (result.IsError != true)
            {
                ValidationFailures.Value?.Add($"{name} accepted invalid arguments: {JsonSerializer.Serialize(arguments)}");
                if (name == "start_diagnostic_session" && result.StructuredContent is not null)
                {
                    var accidental = Check.Read<DiagnosticSession>(JsonSerializer.SerializeToElement(result.StructuredContent, Check.Json));
                    _ = await client.CallToolAsync("stop_diagnostic_session", new Dictionary<string, object?> { ["id"] = accidental.Id.ToString() }, cancellationToken: ct);
                }
            }
            Check.That(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, Check.Json)) <= MaximumResponseBytes, "Oversized error response.");
        }
        catch (McpException) { /* JSON-RPC validation/unknown-tool error is also permitted. */ }
    }

    private static readonly AsyncLocal<Dictionary<string, JsonSchema>?> Schemas = new();
    private static readonly AsyncLocal<List<string>?> SchemaFailures = new();
    private static readonly AsyncLocal<List<string>?> ValidationFailures = new();
}
