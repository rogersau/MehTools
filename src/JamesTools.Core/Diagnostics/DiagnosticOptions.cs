namespace JamesTools.Core.Diagnostics;

/// <summary>Local recording preferences. No setting is inferred from game files.</summary>
public sealed class DiagnosticOptions
{
    public bool AdvancedHardFaultTracing { get; set; }
    public bool FastGpuPolling { get; set; }
    public bool WriteRawExports { get; set; } = true;
    public bool InvestigateStutter { get; set; }
    public bool DeepInvestigation { get; set; }
    public bool DeepUsbTracing { get; set; }
    public int InvestigationDurationSeconds { get; set; } = 60;
    public double WarmupSeconds { get; set; }
    public double StutterThresholdMs { get; set; } = 33.333;
    public double TargetFps { get; set; } = 60;
    public RecordingConditions Conditions { get; set; } = new();
    public DiagnosticOptions Copy() => new()
    {
        AdvancedHardFaultTracing = AdvancedHardFaultTracing,
        FastGpuPolling = FastGpuPolling,
        WriteRawExports = WriteRawExports,
        InvestigateStutter = InvestigateStutter,
        DeepInvestigation = DeepInvestigation,
        DeepUsbTracing = DeepUsbTracing,
        InvestigationDurationSeconds = InvestigationDurationSeconds,
        WarmupSeconds = WarmupSeconds,
        StutterThresholdMs = StutterThresholdMs,
        TargetFps = TargetFps,
        Conditions = (Conditions ?? new()) with { }
    };
    public void Validate()
    {
        if (InvestigationDurationSeconds is < 1 or > 300)
            throw new ArgumentException("Investigation duration must be 1–300 seconds; 30–90 seconds is recommended.");
        if (DeepInvestigation && InvestigationDurationSeconds < 30)
            throw new ArgumentException("Deep Investigation duration must be 30–300 seconds. You can stop earlier at any time.");
        if (DeepInvestigation && !InvestigateStutter || DeepUsbTracing && !DeepInvestigation)
            throw new ArgumentException("Deep tracing requires Stutter Investigator; USB ETW requires Deep Investigation.");
        if (!double.IsFinite(WarmupSeconds) || WarmupSeconds is < 0 or > 3600 ||
            !double.IsFinite(StutterThresholdMs) || StutterThresholdMs is < 1 or > 10000 ||
            !double.IsFinite(TargetFps) || TargetFps is < 1 or > 1000)
            throw new ArgumentException("Warm-up must be 0–3600 s, stutter threshold 1–10000 ms and target FPS 1–1000.");
        Conditions ??= new();
        foreach (var property in typeof(RecordingConditions).GetProperties())
            if (property.GetValue(Conditions) is string value && value.Length > 4000) throw new ArgumentException("Recording condition fields must be at most 4000 characters.");
    }
}

public sealed record RecordingConditions
{
    public string GameVersion { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string RenderScale { get; set; } = "";
    public string GraphicsPreset { get; set; } = "";
    public string RayTracing { get; set; } = "";
    public string Upscaling { get; set; } = "";
    public string FrameGeneration { get; set; } = "";
    public string FpsCap { get; set; } = "";
    public string VSync { get; set; } = "";
    public string Vrr { get; set; } = "";
    public string RefreshRate { get; set; } = "";
    public string Scene { get; set; } = "";
    public string BackgroundWorkload { get; set; } = "";
    public string AmbientTemperature { get; set; } = "";
    public string Notes { get; set; } = "";
}
