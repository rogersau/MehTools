namespace JamesTools.Core.Scanning;

/// <summary>Identity loss is terminal. Focus transitions and original-size changes reset baselines.</summary>
public sealed class LiveCaptureLifecycle
{
    public long Epoch { get; private set; }
    public bool Active { get; private set; }
    private bool lost;
    private int width, height;
    private TimeSpan last = TimeSpan.MinValue;
    public bool Observe(bool identityValid, bool foreground, bool minimized)
    {
        if (!identityValid) lost = true;
        if (lost) throw new InvalidOperationException("Selected target closed or changed identity. Select it again and Start.");
        bool active = foreground && !minimized;
        if (active != Active) { Epoch++; Active = active; }
        return Active;
    }
    public bool Size(int w, int h)
    {
        if (w == width && h == height) return false;
        width = w; height = h; Epoch++; return true;
    }
    public bool Accept(TimeSpan timestamp)
    {
        if (!Active || timestamp <= last || timestamp < TimeSpan.Zero) return false;
        last = timestamp; return true;
    }
}

/// <summary>One explicit run; initialization cancellation and all terminal paths await owned cleanup.</summary>
public static class LiveWardogsRun
{
    public static async Task RunAsync(ILatestHudFrameSource source, Func<CancellationToken, Task<IHudOcr>> initialize, Action<LiveWardogsSnapshot> publish, LiveWardogsOptions options, CancellationToken token)
    {
        IHudOcr? ocr = null;
        try
        {
            token.ThrowIfCancellationRequested();
            ocr = await initialize(token);
            token.ThrowIfCancellationRequested();
            var detector = new LiveWardogsDetector(source, ocr, options);
            ocr = null;
            await detector.RunAsync(publish, token);
        }
        finally
        {
            try { if (ocr != null) await ocr.DisposeAsync(); }
            finally { if (source is IAsyncDisposable asyncSource) await asyncSource.DisposeAsync(); else if (source is IDisposable disposable) disposable.Dispose(); }
        }
    }
}
