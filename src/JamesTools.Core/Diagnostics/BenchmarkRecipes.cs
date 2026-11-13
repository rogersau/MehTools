using System.Diagnostics;
using System.Text.Json;
using JamesTools.Core.Sessions;

namespace JamesTools.Core.Diagnostics;

public sealed record BenchmarkRecipe(Guid Id, string GameKey, string Name, string Instructions, string Settings, int WarmupSeconds, int CaptureSeconds, int Runs)
{
    public BenchmarkRecipe Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(GameKey) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 ||
            Instructions is null || Settings is null || Instructions.Length > 4000 || Settings.Length > 4000 || WarmupSeconds is < 0 or > 300 || CaptureSeconds is < 10 or > 600 || Runs is < 1 or > 10)
            throw new ArgumentException("Recipe requires a game, name (1–80), scene/settings (up to 4000 each), warm-up (0–300), capture (10–600), runs (1–10).");
        return this with { GameKey = SessionLibraryStore.NormalizeExecutable(GameKey), Name = Name.Trim() };
    }
}
public sealed record BenchmarkRun(Guid Id, int Index, string Status = "Ready", string? Reason = null, string? ReportPath = null,
    double ActualSeconds = 0, Guid? SessionId = null, string? Target = null, ReportMachineSummary? Summary = null);
public sealed record BenchmarkSet(Guid Id, BenchmarkRecipe Recipe, DateTime StartedUtc, BenchmarkRun[] Runs);
public sealed record BenchmarkData(int Version, BenchmarkRecipe[] Recipes, BenchmarkSet[] Sets);

/// <summary>Transactional small local store. Failed writes leave the last in-memory snapshot intact.</summary>
public sealed class BenchmarkStore
{
    readonly string path;
    private BenchmarkData data = new(1, [], []);
    private bool recoveredBackup;
    public BenchmarkData Data => Clone(data);
    public string? Notice { get; private set; }
    public BenchmarkStore(string directory)
    {
        path = Path.Combine(directory, "benchmarks.json");
        if (!File.Exists(path)) return;
        try { data = Read(path); }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            Notice = "Benchmark store could not be read: " + ex.Message;
            if (File.Exists(path + ".bak")) { data = Read(path + ".bak"); recoveredBackup = true; }
            else throw new IOException(Notice, ex);
        }
        if (data.Sets.Any(s => s.Runs.Any(r => r.Status is "Warming" or "Capturing")))
            Save(data with { Sets = data.Sets.Select(s => s with { Runs = s.Runs.Select(r => r.Status is "Warming" or "Capturing" ? r with { Status = "Incomplete", Reason = "Application interrupted before run finished." } : r).ToArray() }).ToArray() });
    }
    static BenchmarkData Read(string file)
    {
        return Validate(JsonSerializer.Deserialize<BenchmarkData>(File.ReadAllText(file)) ?? throw new JsonException("Empty benchmark store."));
    }
    static BenchmarkData Clone(BenchmarkData value) => value with { Recipes = value.Recipes.ToArray(), Sets = value.Sets.Select(s => s with { Runs = s.Runs.ToArray() }).ToArray() };
    static BenchmarkData Validate(BenchmarkData data)
    {
        if (data.Version != 1 || data.Recipes is null || data.Sets is null) throw new JsonException("Unsupported benchmark store.");
        if (data.Recipes.Any(r => r is null) || data.Sets.Any(s => s is null || s.Recipe is null || s.Runs is null || s.Runs.Any(r => r is null)))
            throw new JsonException("Invalid benchmark entries.");
        foreach (var r in data.Recipes.Concat(data.Sets.Select(s => s.Recipe)))
        {
            if (r.Instructions is null || r.Settings is null) throw new JsonException("Invalid recipe context.");
            r.Validate();
        }
        if (data.Recipes.Select(r => r.Id).Distinct().Count() != data.Recipes.Length || data.Sets.Any(s => s.Id == Guid.Empty) ||
            data.Sets.Select(s => s.Id).Distinct().Count() != data.Sets.Length ||
            data.Sets.SelectMany(s => s.Runs).Select(r => r.Id).Distinct().Count() != data.Sets.Sum(s => s.Runs.Length))
            throw new JsonException("Duplicate or empty benchmark identity.");
        foreach (var s in data.Sets)
        {
            if (s.StartedUtc == default || s.Runs.Length != s.Recipe.Runs || s.Runs.Select(r => r.Index).Distinct().Count() != s.Runs.Length ||
                s.Runs.Any(r => r.Id == Guid.Empty || r.Index < 1 || r.Index > s.Recipe.Runs || !double.IsFinite(r.ActualSeconds) || r.ActualSeconds < 0 ||
                    r.Status is not ("Ready" or "Warming" or "Capturing" or "Completed" or "Incomplete")))
                throw new JsonException("Invalid benchmark run metadata.");
        }
        return data with { Recipes=data.Recipes.Select(r=>r.Validate()).ToArray(), Sets=data.Sets.Select(s=>s with { Recipe=s.Recipe.Validate(), Runs=s.Runs.ToArray() }).ToArray() };
    }
    public void Save(BenchmarkData next)
    {
        next = Validate(next);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, recoveredBackup ? path + ".corrupt-" + Guid.NewGuid().ToString("N") : path + ".bak"); else File.Move(temp, path);
            data = Clone(next);
            recoveredBackup = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Put(BenchmarkSet set) => Save(Data with { Sets = Data.Sets.Where(s => s.Id != set.Id).Append(set).ToArray() });
}

/// <summary>One explicit invocation per run. Clock and delay are injectable; UI ticks never advance phases.</summary>
public sealed class BenchmarkRunner(Func<double>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    readonly Func<double> now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    int busy;
    public string Phase { get; private set; } = "Ready";
    public double Remaining { get; private set; }
    public double Elapsed { get; private set; }
    public bool Busy => busy != 0;
    public async Task<(bool Complete, string? Reason)> RunAsync(BenchmarkRecipe recipe, Func<CancellationToken, Task> start,
        Func<Task> stop, Func<CancellationToken, Task<bool>> valid, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0) throw new InvalidOperationException("A benchmark run is already active.");
        bool started = false;
        try
        {
            recipe.Validate();
            ct.ThrowIfCancellationRequested();
            if (!await TargetValid()) return (false, "Game exited or observed target changed.");
            Elapsed=0; Remaining=recipe.WarmupSeconds;
            Phase = "Warming";
            await Period(recipe.WarmupSeconds, false);
            ct.ThrowIfCancellationRequested();
            if (!await TargetValid()) return (false, "Game exited or observed target changed.");
            Phase = "Capturing";
            started = true; // Stop must also clean up a partially successful start.
            await start(ct);
            await Period(recipe.CaptureSeconds, true);
            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "Cancelled by user."); }
        catch (Exception ex) { return (false, ex.Message); }
        finally
        {
            Phase = "Finishing"; Remaining = 0;
            try { if (started) await stop(); }
            finally { Phase = "Ready"; Remaining = 0; Interlocked.Exchange(ref busy, 0); }
        }
        async Task Period(int seconds, bool capture)
        {
            double startTime = now(), previous = startTime, nextValidation = startTime+1;
            while (now() - startTime < seconds)
            {
                Remaining = Math.Max(0, seconds - (now() - startTime));
                Elapsed = now() - startTime;
                await wait(TimeSpan.FromSeconds(Math.Min(0.25, Remaining)), ct);
                var current = now();
                if (current - previous > 3) throw new InvalidOperationException("Scheduling interruption exceeded three seconds; repeat this run.");
                previous = current;
                if (current >= nextValidation || current-startTime >= seconds)
                {
                    if (!await TargetValid()) throw new InvalidOperationException("Game exited or observed target changed.");
                    if (now() - current > 3) throw new InvalidOperationException("Target validation interrupted scheduling for over three seconds.");
                    nextValidation = now()+1;
                }
            }
            Elapsed=now()-startTime;
            if (capture && now() - startTime > seconds + 3) throw new InvalidOperationException("Capture exceeded its timing tolerance.");
        }
        async Task<bool> TargetValid() => await valid(ct).WaitAsync(TimeSpan.FromSeconds(3),ct);
    }
}

public static class BenchmarkResults
{
    public static string Describe(BenchmarkSet set)
    {
        static string V(double? n) => n?.ToString("0.##") ?? "unavailable";
        var lines = set.Runs.Select(r => $"Run {r.Index}: {r.Status} {r.Reason}\n  {r.ActualSeconds:0.##}s; FPS {V(r.Summary?.FpsAverage)}, frame ms {V(r.Summary?.FrameMsAverage)}, CPU {V(r.Summary?.CpuAverage)}%, GPU {V(r.Summary?.GpuAverage)}%, RAM {V(r.Summary?.MemoryAverageMiB)} MiB; samples {r.Summary?.SystemSampleCount}, frames {r.Summary?.FrameCount}\n  {r.ReportPath}").ToList();
        var completed = set.Runs.Where(r => r.Status == "Completed" && r.Summary?.SchemaVersion == 2 &&
            r.Summary.DurationSeconds is double duration && double.IsFinite(duration) && Math.Abs(duration-set.Recipe.CaptureSeconds) <= 3).ToArray();
        if (completed.Select(r => r.Summary!.NetworkConfiguration).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            lines.Add("Aggregate unavailable: captures used different network configurations.");
            return string.Join(Environment.NewLine, lines);
        }
        var framesComparable = completed.Length >= 2 && completed.All(r => r.Summary is { FrameCount: > 0, FrameStreamCount: 1, FpsAverage: > 0, FrameMsAverage: > 0 } &&
            !string.IsNullOrWhiteSpace(r.Summary.FrameStream) && !string.IsNullOrWhiteSpace(r.Summary.FpsMethod)) &&
            completed.Select(r=>r.Summary!.FrameStream).Distinct(StringComparer.Ordinal).Count()==1 &&
            completed.Select(r=>r.Summary!.FpsMethod).Distinct(StringComparer.Ordinal).Count()==1;
        var metrics = new List<(string Name, Func<ReportMachineSummary, double?> Get)> { ("CPU %", s => s.CpuAverage), ("GPU %", s => s.GpuAverage), ("RAM MiB", s => s.MemoryAverageMiB) };
        if (framesComparable) { metrics.Add(("FPS",s=>s.FpsAverage)); metrics.Add(("Frame time ms",s=>s.FrameMsAverage)); }
        else lines.Add("FPS/frame-time aggregation unavailable: at least two completed timed runs with the same FPS method and one identical frame stream are required. Missing or mixed streams are not combined.");
        foreach (var metric in metrics)
        {
            var values = completed.Where(r => r.Summary != null).Select(r => metric.Get(r.Summary!)).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).Order().ToArray();
            if (values.Length < 2) continue;
            var median = (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
            lines.Add($"{metric.Name}: typical (median) {median:0.##}; min–max {values.First():0.##}–{values.Last():0.##} ({values.Length} completed runs). Descriptive only; scene/settings are user declared.");
        }
        return string.Join("\n", lines);
    }
}
