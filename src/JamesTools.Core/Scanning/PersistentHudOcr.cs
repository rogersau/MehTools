using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace JamesTools.Core.Scanning;

public static class HudWorkerProtocol
{
    public const int Limit = 1500000;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > Limit) throw new InvalidDataException("Protocol payload too large.");
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, token); await stream.WriteAsync(data, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > Limit) throw new InvalidDataException("Invalid protocol size.");
        byte[] data = new byte[size]; await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty protocol payload.");
    }
}
public sealed record HudWorkerReply(OcrRow[] Rows, double CpuMilliseconds);

public sealed class PersistentHudOcr : IHudOcr
{
    private readonly Process process;
    private readonly SemaphoreSlim serial = new(1);
    private readonly Task stderr;
    private string error = "";
    private int disposed;
    private readonly object cleanupLock = new();
    private Task? cleanup;
    private TimeSpan requestTimeout = TimeSpan.FromSeconds(3);
    private double startupCpu;
    public double StartupMilliseconds { get; private set; }
    public double WorkerCpuMilliseconds { get; private set; }
    private PersistentHudOcr(Process process)
    {
        this.process = process;
        stderr = Task.Run(async () => { char[] buffer = new char[1024]; int count; while ((count = await process.StandardError.ReadAsync(buffer)) > 0) { if (error.Length < 2048) error += new string(buffer, 0, Math.Min(count, 2048 - error.Length)); } }); // Retain at most 2 KiB for explicit caller diagnostics; never persist it.
    }
    public static async Task<PersistentHudOcr> StartAsync(string helperExe, string nativeDll, string tessdata, CancellationToken token = default, TimeSpan? startupTimeout = null, TimeSpan? requestTimeout = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(helperExe) || !File.Exists(helperExe) || !Path.IsPathFullyQualified(nativeDll) || !File.Exists(nativeDll) || !Path.IsPathFullyQualified(tessdata) || !File.Exists(Path.Combine(tessdata, "eng.traineddata"))) throw new ArgumentException("Explicit existing helper, native DLL and tessdata paths required.");
        if (startupTimeout is { } st && (st.TotalSeconds < .05 || st.TotalSeconds > 30) || requestTimeout is { } rt && (rt.TotalSeconds < .05 || rt.TotalSeconds > 30)) throw new ArgumentException("Invalid worker deadlines.");
        var start = Stopwatch.StartNew();
        var info = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(nativeDll); info.ArgumentList.Add(tessdata);
        info.Environment["OMP_THREAD_LIMIT"] = "1"; info.Environment["OMP_NUM_THREADS"] = "1";
        var worker = new PersistentHudOcr(Process.Start(info) ?? throw new IOException("Worker failed to start."));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(startupTimeout ?? TimeSpan.FromSeconds(10));
            if (await HudWorkerProtocol.ReadAsync<string>(worker.process.StandardOutput.BaseStream, deadline.Token) != "wardogs-ocr-1") throw new InvalidDataException("Invalid worker handshake.");
            worker.startupCpu = worker.process.TotalProcessorTime.TotalMilliseconds; worker.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3); worker.StartupMilliseconds = start.Elapsed.TotalMilliseconds; return worker;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { await worker.DisposeAsync(); throw new TimeoutException("OCR worker startup timed out."); }
        catch (EndOfStreamException ex) { await worker.DisposeAsync(); throw new IOException("OCR worker failed to initialize: " + worker.error, ex); }
        catch { await worker.DisposeAsync(); throw; }
    }
    public async Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, CancellationToken token)
    {
        frame.Validate(); if (!await serial.WaitAsync(0, token)) throw new InvalidOperationException("An OCR request is already active.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(requestTimeout);
            try
            {
                await HudWorkerProtocol.WriteAsync(process.StandardInput.BaseStream, frame, deadline.Token);
                var reply = await HudWorkerProtocol.ReadAsync<HudWorkerReply>(process.StandardOutput.BaseStream, deadline.Token);
                if (reply.Rows is null || reply.Rows.Length > 256 || !double.IsFinite(reply.CpuMilliseconds) || reply.CpuMilliseconds < 0
                    || reply.Rows.Any(r => r is null || r.Text is null || r.Text.Length > 4096 || !double.IsFinite(r.Y) || r.Y is < 0 or > 1 || !double.IsFinite(r.Confidence) || r.Confidence is < 0 or > 100)) throw new InvalidDataException("Invalid OCR response.");
                WorkerCpuMilliseconds = Math.Max(0, reply.CpuMilliseconds - startupCpu); return reply.Rows;
            }
            catch (EndOfStreamException ex) { await DisposeAsync(); throw new IOException("OCR worker exited: " + error, ex); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { await DisposeAsync(); throw new TimeoutException("OCR request timed out; the worker has been stopped."); }
            catch { await DisposeAsync(); throw; }
        }
        finally { serial.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (cleanupLock) return new(cleanup ??= StopAsync());
    }
    private async Task StopAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        try {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        finally { process.Dispose(); }
    }
}
