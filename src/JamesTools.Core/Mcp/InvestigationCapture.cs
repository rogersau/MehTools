using System.Diagnostics;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;

namespace JamesTools.Core.Mcp;

public sealed record StutterInvestigationOptions(int? ProcessId = null, bool DeepInvestigation = false,
    bool DeepUsbTracing = false, double StutterThresholdMs = 33.333)
{
    public void Validate(int duration)
    {
        if (duration is < 1 or > 300 || ProcessId is <= 0 || DeepUsbTracing && !DeepInvestigation ||
            DeepInvestigation && duration < 30 ||
            !double.IsFinite(StutterThresholdMs) || StutterThresholdMs is < 1 or > 10000)
            throw new ArgumentException("Investigations require 1..300 seconds (Deep: 30..300), an accessible running PID or system-only scope, and a 1..10000 ms threshold. Deep USB requires explicit deep mode.");
    }
}

// A capability of the existing session service, not a second session owner.
public interface IStutterInvestigations
{
    Task<DiagnosticSession> StartInvestigationAsync(int duration, StutterInvestigationOptions options, CancellationToken cancellationToken);
}

/// <summary>Minimal injection seam; lifetime belongs to DiagnosticSessionService, never an MCP request.</summary>
public interface IInvestigationCapture
{
    InvestigationReport Snapshot();
    Task<InvestigationReport> StopAsync();
}

public interface IInvestigationCaptureFactory
{
    // Validation must not start collectors. The opaque target never comes from an MCP path argument.
    string? ValidateTarget(int? processId);
    Task<IInvestigationCapture> StartAsync(string? validatedTarget, int duration, StutterInvestigationOptions options, CancellationToken cancellationToken);
}

public sealed class PerformanceInvestigationCaptureFactory : IInvestigationCaptureFactory
{
    public string? ValidateTarget(int? processId)
    {
        if (processId is null) return null;
        if (processId <= 0) throw new ArgumentException("A running PID is required.");
        using var process = Process.GetProcessById(processId.Value);
        var path = ProcessSnapshot.GetImagePath(processId.Value);
        if (process.HasExited || string.IsNullOrWhiteSpace(path)) throw new UnauthorizedAccessException();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new NotSupportedException();
        return path;
    }

    public async Task<IInvestigationCapture> StartAsync(string? validatedTarget, int duration, StutterInvestigationOptions options, CancellationToken cancellationToken)
    {
        options.Validate(duration);
        // Check again after durable acceptance: an application may exit or the PID may be reused.
        if (!string.Equals(ValidateTarget(options.ProcessId), validatedTarget, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        var presentMon = PresentMonLocator.Resolve(null);
        // Installed MCP hosts live in a sibling subdirectory of the desktop's bundled tools.
        if (presentMon is null && Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { } parent)
        {
            foreach (var sibling in new[] { Path.Combine(parent.FullName, "PresentMon", "PresentMon.exe"),
                         Path.Combine(parent.FullName, "tools", "PresentMon", "PresentMon.exe") })
                if (File.Exists(sibling)) { presentMon = PresentMonLocator.Resolve(sibling); break; }
        }
        var logger = await PerformanceLogger.StartAsync(validatedTarget, presentMon, null, new NetworkProbeOptions(false, []),
            new DiagnosticOptions { InvestigateStutter = true, DeepInvestigation = options.DeepInvestigation,
                DeepUsbTracing = options.DeepUsbTracing, InvestigationDurationSeconds = duration, StutterThresholdMs = options.StutterThresholdMs,
                WriteRawExports = false }, cancellationToken).ConfigureAwait(false);
        return new Capture(logger, options.StutterThresholdMs);
    }

    sealed class Capture(PerformanceLogger logger, double threshold) : IInvestigationCapture
    {
        public InvestigationReport Snapshot()
        {
            if (logger.CompletedInvestigation is { } completed) return completed;
            var live = logger.GetInvestigationSnapshot();
            var report = InvestigationAnalysis.Analyze(live.Frames, live.Events, logger.GetInvestigationDevices(), live.Hardware,
                live.Collectors.Append(new InvestigationCollectorStatus("MCP live window", "partial", "Live analysis uses only the latest 1000 frames/events; final report uses the logger's retained recording.")).ToArray(),
                live.DurationSeconds, threshold);
            return report with { Limitations = report.Limitations.Append("Provisional live window; final recording statistics are available after stop.").ToArray() };
        }

        public async Task<InvestigationReport> StopAsync()
        {
            await logger.StopAsync().ConfigureAwait(false);
            return logger.CompletedInvestigation ?? Snapshot();
        }
    }
}
