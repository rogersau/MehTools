using JamesTools.Core.Scanning;

internal static class LiveWardogsTests
{
    private sealed class Source : ILatestHudFrameSource
    {
        public int Index;
        public ValueTask<HudFrame?> LatestAsync(CancellationToken token) => ValueTask.FromResult<HudFrame?>(new("fake", 1, TimeSpan.FromSeconds(Index++), 32, 32, 32, new byte[1024]));
    }
    private sealed class Ocr : IHudOcr
    {
        public int Calls; public bool Disposed;
        public Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, CancellationToken token)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<OcrRow>>(Calls switch { 1 or 2 => [new("KILL old", .1, 90)], 3 => [], 4 => [new("KILL new", .2, 90)], _ => [new("HEADSHOT KILL new", .2, 90)] });
        }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    public static async Task Run()
    {
        var source = new Source(); var ocr = new Ocr(); using var stop = new CancellationTokenSource();
        LiveWardogsSnapshot? last = null;
        try
        {
            await new LiveWardogsDetector(source, ocr, new(4, 1, .25, 4), new LiveTestClock()).RunAsync(s => { last = s; if (s.OcrCalls == 6) stop.Cancel(); }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (!ocr.Disposed || last is null || last.Events.Length != 1 || last.Events[0].Kind != WardogsEventKind.Headshot) throw new Exception("Baseline/confirmation/refinement/cleanup failed.");
        if (last.Kills != 1 || last.Headshots != 1) throw new Exception("Headshot refinement double-counted a kill.");
        try { new HudFrame("fake", 0, TimeSpan.Zero, 2048, 32, 32, []).Validate(); throw new Exception("Accepted oversized frame"); } catch (InvalidDataException) { }
    }
    private sealed class ReplaySource(List<HudFrame> frames) : ILatestHudFrameSource
    {
        public int Index;
        public ValueTask<HudFrame?> LatestAsync(CancellationToken token) => ValueTask.FromResult<HudFrame?>(frames[Math.Min(Index++, frames.Count - 1)]);
    }
    public static async Task Replay(string helper, string dll, string data, string[] rawFiles)
    {
        await using var ocr = await PersistentHudOcr.StartAsync(helper, dll, data);
        var latencies = new List<double>(); var wall = System.Diagnostics.Stopwatch.StartNew();
        foreach (string file in rawFiles)
        {
            byte[] pixels = await File.ReadAllBytesAsync(file);
            var frame = new HudFrame("replay", 1, TimeSpan.FromSeconds(latencies.Count), 550, 240, 550, pixels);
            var timer = System.Diagnostics.Stopwatch.StartNew(); var rows = await ocr.ReadAsync(frame, default); latencies.Add(timer.Elapsed.TotalMilliseconds);
            Console.WriteLine($"Replay {latencies.Count}: {rows.Count} rows; event rows {rows.Count(r => r.Text.Contains("KILL", StringComparison.OrdinalIgnoreCase))}");
        }
        double directWallMilliseconds = wall.Elapsed.TotalMilliseconds; double directCpuMilliseconds = ocr.WorkerCpuMilliseconds;
        var replayFrames = new List<HudFrame> { new("replay", 1, TimeSpan.Zero, 550, 240, 550, new byte[550 * 240]) };
        foreach (string file in rawFiles)
        {
            byte[] pixels = await File.ReadAllBytesAsync(file);
            for (int i = 0; i < 2; i++) replayFrames.Add(new("replay", 1, TimeSpan.FromSeconds(replayFrames.Count * .5), 550, 240, 550, pixels));
        }
        var source = new ReplaySource(replayFrames); using var stop = new CancellationTokenSource(); LiveWardogsSnapshot? snapshot = null;
        try { await new LiveWardogsDetector(source, ocr, timeProvider: new LiveTestClock()).RunAsync(s => { snapshot = s; if (source.Index >= replayFrames.Count) stop.Cancel(); }, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Console.WriteLine($"Same-engine loop replay: samples={snapshot!.Samples}, OCR={snapshot.OcrCalls}, events={snapshot.Events.Length}, kinds={string.Join(',', snapshot.Events.Select(e => e.Kind))}; virtual pacing, no live throughput claim.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ocr.StartupMilliseconds, Count = latencies.Count, Width = 550, Height = 240, WallMilliseconds = directWallMilliseconds, WorkerCpuMilliseconds = directCpuMilliseconds, OcrMilliseconds = latencies, Throughput = latencies.Count / (directWallMilliseconds / 1000) }));
    }
}
