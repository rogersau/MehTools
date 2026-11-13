using System.Globalization;
using System.Text;
using System.Text.Json;
namespace JamesTools.Core.Diagnostics;

public sealed record HardwareReading(double Seconds, double? Cpu = null, double? Gpu = null, double? Memory = null,
    double? Pages = null, double? Read = null, double? Write = null, double? Latency = null);
public sealed record RollingCapture(Guid? SessionId, double Trigger, int Before, int After,
    IReadOnlyList<HardwareReading> Samples, string Reason)
{
    public string Report()
    {
        var start = Math.Min(Trigger, Samples.FirstOrDefault()?.Seconds ?? Trigger);
        var end = Math.Max(Trigger, Samples.LastOrDefault()?.Seconds ?? Trigger);
        string F(double? n) => n?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        double? Avg(Func<HardwareReading, double?> select) { var v = Samples.Select(select).Where(x => x.HasValue).Select(x => x!.Value).ToArray(); return v.Length == 0 ? null : v.Average(); }
        var b = new StringBuilder("# What just happened?\n- Report format: 2\n");
        b.AppendLine($"Requested pre/post: {Before}/{After} seconds; actual pre/post: {F(Trigger-start)}/{F(end-Trigger)} seconds.");
        b.AppendLine("Completion: " + Reason);
        b.AppendLine("System-wide measurements only. No frame/FPS data or causal/bottleneck conclusions. Missing values are unavailable; no interpolation. Short history indicates warm-up or count retention limits. Times mark collection points; rate counters describe preceding sampling intervals, not instantaneous readings. Counter baselines reset after game changes and gaps over 2.5 seconds.");
        b.AppendLine($"Gaps over 2.5 seconds: {Samples.Zip(Samples.Skip(1)).Count(x => x.Second.Seconds-x.First.Seconds > 2.5)}; readings with unavailable counters: {Samples.Count(x => x.Cpu is null || x.Gpu is null || x.Memory is null || x.Pages is null || x.Read is null || x.Write is null || x.Latency is null)}.");
        b.AppendLine("## Machine summary (JSON)\n```json");
        b.AppendLine(JsonSerializer.Serialize(new { schemaVersion=2, durationSeconds=end-start, systemSampleCount=Samples.Count, cpuAvg=Avg(x=>x.Cpu), gpuAvg=Avg(x=>x.Gpu), memUsedAvgMiB=Avg(x=>x.Memory) }));
        b.AppendLine("```\n## Markers"); b.AppendLine($"- {F(Trigger-start)} s — What just happened? (trigger)");
        b.AppendLine("## Sample timeline (CSV)\n```csv\nsample_start_s,sample_end_s,cpu_pct,gpu_pct,ram_used_mib,pages_input_per_s,disk_read_mib_per_s,disk_write_mib_per_s,disk_latency_ms");
        foreach(var s in Samples) b.AppendLine(string.Join(",", new double?[]{s.Seconds-start,s.Seconds-start,s.Cpu,s.Gpu,s.Memory,s.Pages,s.Read,s.Write,s.Latency}.Select(F)));
        b.AppendLine("```"); return b.ToString();
    }
}

/// <summary>Pure state machine. Caller supplies monotonic seconds; no Windows dependencies.</summary>
public sealed class RollingDiagnosticBuffer
{
    public const int MaximumSamples = 720;
    public const double MaximumSampleGapSeconds = 2.5;
    private readonly Queue<HardwareReading> _ring = new();
    private List<HardwareReading>? _pending;
    private string? _identity;
    private Guid? _owner;
    private double _trigger;
    private int _before=60, _after=15;
    public bool Capturing => _pending is not null;
    public int Count => _ring.Count;
    public RollingCapture? Configure(string? identity, Guid? owner, int before, int after)
    {
        if(before is <10 or >300 || after is <0 or >60) throw new ArgumentOutOfRangeException(nameof(before));
        RollingCapture? result=null;
        if(identity != _identity || owner != _owner || before != _before || after != _after) { result=Finish("Invalidated: game/session, watching, or settings changed; partial capture."); _ring.Clear(); }
        _identity=identity; _owner=owner; _before=before; _after=after; return result;
    }
    public bool Trigger(double now)
    {
        if(!double.IsFinite(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
        if(_identity is null || Capturing || _ring.Count == 0) return false;
        var latest = _ring.Last().Seconds;
        if (now < latest || now-latest > MaximumSampleGapSeconds) return false;
        _trigger=now; _pending=_ring.Where(x=>x.Seconds>=now-_before && x.Seconds<=now).ToList(); return true;
    }
    public RollingCapture? Add(HardwareReading sample)
    {
        if (!double.IsFinite(sample.Seconds) || sample.Seconds < 0) throw new ArgumentOutOfRangeException(nameof(sample));
        if(_identity is null) return null;
        if(_ring.TryPeek(out _) && _ring.Last().Seconds>=sample.Seconds) throw new ArgumentException("Monotonic samples required.");
        static double? Valid(double? n, double max = double.MaxValue) => n is double value && double.IsFinite(value) && value >= 0 && value <= max ? value : null;
        sample = sample with { Cpu=Valid(sample.Cpu,100), Gpu=Valid(sample.Gpu,100), Memory=Valid(sample.Memory), Pages=Valid(sample.Pages), Read=Valid(sample.Read), Write=Valid(sample.Write), Latency=Valid(sample.Latency) };
        _ring.Enqueue(sample);
        while(_ring.Count>MaximumSamples || (_ring.TryPeek(out var first) && first.Seconds<sample.Seconds-_before)) _ring.Dequeue();
        if(_pending is not null && sample.Seconds>_trigger && sample.Seconds<=_trigger+_after && _pending.Count<MaximumSamples) _pending.Add(sample);
        return Advance(sample.Seconds);
    }
    public RollingCapture? Advance(double now)
    {
        if (!double.IsFinite(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
        return Capturing && now>=_trigger+_after ? Finish("Post window elapsed.") : null;
    }
    public RollingCapture? Finish(string reason)
    {
        if(_pending is null) return null;
        var result=new RollingCapture(_owner,_trigger,_before,_after,_pending.ToArray(),reason); _pending=null; return result;
    }
}
