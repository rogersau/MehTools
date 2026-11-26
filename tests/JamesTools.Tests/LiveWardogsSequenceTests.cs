using JamesTools.Core.Scanning;

internal static class LiveWardogsSequenceTests
{
    private sealed class Source(HudFrame?[] frames) : ILatestHudFrameSource
    {
        public int Index;
        public ValueTask<HudFrame?> LatestAsync(CancellationToken token) => ValueTask.FromResult(frames[Math.Min(Index++, frames.Length - 1)]);
    }
    private sealed class Ocr(Func<HudFrame, OcrRow[]> rows, bool fail = false) : IHudOcr
    {
        public bool Disposed; public int Calls; private int active;
        public async Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, CancellationToken token)
        {
            if (Interlocked.Increment(ref active) != 1) throw new Exception("Overlapping OCR");
            try { Calls++; await Task.Yield(); token.ThrowIfCancellationRequested(); if (fail) throw new IOException("Fake worker exit"); return rows(frame); }
            finally { Interlocked.Decrement(ref active); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    private static byte[] Pixels(int width, double seconds) { var pixels = new byte[width * 32]; pixels[0] = seconds > 0 ? (byte)1 : (byte)0; return pixels; }
    private static HudFrame Frame(double seconds, long epoch = 1, int width = 32) => new("selected", epoch, TimeSpan.FromSeconds(seconds), width, 32, width, Pixels(width, seconds));
    private static async Task<LiveWardogsSnapshot> Run(HudFrame?[] frames, Func<HudFrame, OcrRow[]> rows, LiveWardogsOptions? options = null)
    {
        var source = new Source(frames); var ocr = new Ocr(rows); using var stop = new CancellationTokenSource(); LiveWardogsSnapshot? result = null;
        try { await new LiveWardogsDetector(source, ocr, options, new LiveTestClock()).RunAsync(s => { result = s; if (source.Index >= frames.Length) stop.Cancel(); }, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (!ocr.Disposed) throw new Exception("OCR not cleaned up"); return result!;
    }
    public static async Task Run()
    {
        OcrRow[] kill = [new("KILL fixture", .1)];
        var s = await Run([Frame(0), Frame(.5), Frame(1), Frame(1.5), Frame(2)], f => f.Timestamp == TimeSpan.Zero ? [] : kill);
        if (s.Events.Length != 1 || s.OcrCalls != 3 || s.Unchanged != 2) throw new Exception("Pending confirmation or unchanged gate failed");
        if (s.Kills != 1 || s.Assists != 0 || s.Events[0].Timestamp != TimeSpan.FromSeconds(.5)) throw new Exception("Lifetime count or first-observed time failed");
        s = await Run([Frame(0), Frame(0), Frame(-0.0), Frame(.5)], _ => []);
        if (s.Stale != 2) throw new Exception("Repeated frame accepted");
        s = await Run([Frame(0), Frame(.5), Frame(1, 2), Frame(1.5, 2), Frame(2, 2, 64), Frame(2.5, 2, 64), Frame(10, 2, 64)], _ => kill);
        if (s.Events.Length != 0) throw new Exception("Reset joined baseline events");
        s = await Run([Frame(0), Frame(.5), Frame(1), Frame(1.5)], f => f.Timestamp == TimeSpan.Zero ? [] : [new("KILL ASSIST fixture", .1)]);
        if (s.Events.Length != 1 || s.Events[0].Kind != WardogsEventKind.Assist) throw new Exception("Assist classification failed");
        if (s.Kills != 0 || s.Assists != 1) throw new Exception("Assist was counted as a kill");
        s = await Run([Frame(0), Frame(.5), Frame(1), Frame(1.5), Frame(2)], f => f.Timestamp.TotalSeconds switch
        {
            0 => [],
            .5 or 1 => [new("KILL +$2,000", .1)],
            _ => [new("KILL 250XP", .1)]
        }, new(2, 1, .25));
        if (s.Kills != 1 || s.Events.Length != 1) throw new Exception("Cash-to-XP transition counted as another kill");
        // An identical blank image still ages disappearing rows at the sample rate.
        var disappearing = new[] { Frame(0), Frame(.5), Frame(1), Frame(1.5), Frame(2), Frame(2.5), Frame(3), Frame(3.5) };
        for (int i = 0; i < disappearing.Length; i++) disappearing[i].Gray[0] = (byte)(i is 1 or 2 or 6 or 7 ? 1 : 0);
        s = await Run(disappearing, f => f.Gray[0] == 1 ? kill : []);
        if (s.Kills != 2) throw new Exception("Unchanged blank frames prevented row retirement");
        s = await Run([Frame(0), Frame(.5), Frame(1), Frame(1.5), Frame(2), Frame(2.5), Frame(3), Frame(3.5)], f => f.Timestamp.TotalSeconds is .5 or 1 or 3 or 3.5 ? kill : [], new(2, 1, .25));
        if (s.Events.Length != 2) throw new Exception("Disappearance did not allow a fresh notification");
        var ocr = new Ocr(_ => [], true);
        try { await new LiveWardogsDetector(new Source([Frame(0)]), ocr).RunAsync(_ => { }, default); throw new Exception("Expected OCR failure"); } catch (IOException) { }
        if (!ocr.Disposed) throw new Exception("Failure did not dispose OCR");
        ocr = new Ocr(_ => []); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await new LiveWardogsDetector(new Source([Frame(0)]), ocr).RunAsync(_ => { }, canceled.Token); } catch (OperationCanceledException) { }
        if (!ocr.Disposed || ocr.Calls != 0) throw new Exception("Precancel cleanup failed");
        // Retention remains bounded over a long session, without wall-clock waits.
        var many = Enumerable.Range(0, 1601).Select(i => (HudFrame?)Frame(i * .5)).ToArray();
        s = await Run(many, f => ((int)(f.Timestamp.TotalSeconds * 2) % 5) is 1 or 2 ? kill : [], new(2, 1, .25));
        if (s.Events.Length != 256 || s.Events[0].Id <= 1) throw new Exception("History retention is not bounded");
        if (s.Kills != 320 || s.Headshots != 0) throw new Exception("Lifetime totals were truncated with history");
        var clock = new LiveTestClock(); ocr = new Ocr(_ => { clock.Advance(TimeSpan.FromSeconds(.5)); return []; });
        using var throttledStop = new CancellationTokenSource(); LiveWardogsSnapshot? throttled = null;
        var detector = new LiveWardogsDetector(new Source([Frame(0)]), ocr, timeProvider: clock);
        Task? duplicate = null;
        try { await detector.RunAsync(snap => { throttled = snap; duplicate = detector.RunAsync(_ => { }, default); throttledStop.Cancel(); }, throttledStop.Token); }
        catch (OperationCanceledException) when (throttledStop.IsCancellationRequested) { }
        try { await duplicate!; throw new Exception("Accepted concurrent start"); } catch (InvalidOperationException) { }
        if (throttled?.Throttled != true || !ocr.Disposed) throw new Exception("Soft budget or stop failed");
    }
}
