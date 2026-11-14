using JamesTools.Core.Configuration;
using JamesTools.Core.Scanning;
internal static class LiveCaptureTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Live capture lifecycle assertion failed."); }
    private sealed class Source : ILatestHudFrameSource, IDisposable
    {
        public bool Disposed;
        public ValueTask<HudFrame?> LatestAsync(CancellationToken token) => throw new IOException("capture failed");
        public void Dispose() => Disposed = true;
    }
    private sealed class Ocr : IHudOcr
    {
        public bool Disposed;
        public Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, CancellationToken token) => Task.FromResult<IReadOnlyList<OcrRow>>([]);
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    public static async Task Run()
    {
        var lifecycle = new LiveCaptureLifecycle();
        Check(!lifecycle.Observe(true, false, false)); Check(lifecycle.Epoch == 0);
        Check(lifecycle.Observe(true, true, false)); long epoch = lifecycle.Epoch;
        Check(lifecycle.Size(1920, 1080)); Check(lifecycle.Epoch > epoch); epoch = lifecycle.Epoch;
        Check(!lifecycle.Size(1920, 1080)); Check(lifecycle.Epoch == epoch);
        Check(lifecycle.Accept(TimeSpan.FromSeconds(1))); Check(!lifecycle.Accept(TimeSpan.FromSeconds(1)));
        Check(!lifecycle.Observe(true, false, false)); Check(!lifecycle.Accept(TimeSpan.FromSeconds(2)));
        Check(lifecycle.Observe(true, true, false)); Check(lifecycle.Epoch > epoch); epoch = lifecycle.Epoch;
        Check(!lifecycle.Observe(true, true, true)); Check(lifecycle.Observe(true, true, false)); Check(lifecycle.Epoch > epoch);
        Check(lifecycle.Size(2560, 1440)); Check(lifecycle.Accept(TimeSpan.FromSeconds(3))); Check(!lifecycle.Accept(TimeSpan.FromSeconds(2)));
        try { lifecycle.Observe(false, true, false); throw new Exception("identity loss accepted"); } catch (InvalidOperationException) { }
        try { lifecycle.Observe(true, true, false); throw new Exception("identity loss silently recovered"); } catch (InvalidOperationException) { }
        var source = new Source(); var ocr = new Ocr(); using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = LiveWardogsRun.RunAsync(source, async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return ocr; }, _ => { }, new(), cts.Token);
        await entered.Task; cts.Cancel();
        try { await run; throw new Exception("init cancellation ignored"); } catch (OperationCanceledException) { }
        Check(source.Disposed);
        source = new();
        try { await LiveWardogsRun.RunAsync(source, _ => Task.FromResult<IHudOcr>(ocr), _ => { }, new(), CancellationToken.None); throw new Exception("capture error ignored"); } catch (IOException) { }
        Check(source.Disposed && ocr.Disposed);
        source = new(); ocr = new(); using var late = new CancellationTokenSource();
        try { await LiveWardogsRun.RunAsync(source, _ => { late.Cancel(); return Task.FromResult<IHudOcr>(ocr); }, _ => { }, new(), late.Token); } catch (OperationCanceledException) { }
        Check(source.Disposed && ocr.Disposed);
        var settings = new AppSettings(); var copy = settings.Snapshot(); copy.LiveWardogs.X = .1; Check(settings.LiveWardogs.X != copy.LiveWardogs.X);
        var store = new SettingsStore("unused"); var imported = store.Deserialize("{\"LiveWardogs\":{\"Rate\":99,\"Width\":-1}}"); Check(imported.LiveWardogs.IsValid);
        Check(store.Deserialize("{}").LiveWardogs.IsValid);
    }
}
