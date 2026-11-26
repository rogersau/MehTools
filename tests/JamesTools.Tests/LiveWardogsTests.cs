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
            return Task.FromResult<IReadOnlyList<OcrRow>>(Calls switch { 1 or 2 => [new("KILL old", .1)], 3 => [], 4 => [new("KILL new", .2)], _ => [new("HEADSHOT KILL new", .2)] });
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
}
