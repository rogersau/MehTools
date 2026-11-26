using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using JamesTools.Core.Recording;

namespace JamesTools.Core.Scanning;

internal sealed record WardogsFrame(int Width, int Height, byte[] Gray);

/// <summary>Reads FFmpeg's bounded grayscale PGM frames from one sequential decode.</summary>
internal sealed class WardogsFrameStream : IAsyncDisposable
{
    private readonly Process process;
    private readonly BufferedStream output;
    private readonly Task<string> stderr;
    private readonly TimeSpan timeout;
    private readonly byte[] header = new byte[64];
    private bool completed;

    public WardogsFrameStream(string ffmpeg, string source, string filter, int count, int decodeThreads, TimeSpan timeout)
    {
        this.timeout = timeout;
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] args = ["-nostdin", "-v", "error", "-threads", decodeThreads.ToString(CultureInfo.InvariantCulture), "-filter_threads", "1", "-protocol_whitelist", "file,pipe",
            "-format_whitelist", "matroska,webm,mov,avi", "-noautorotate", "-i", source, "-map", "0:V:0", "-vf", filter,
            "-frames:v", count.ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough", "-threads", "1", "-c:v", "pgm", "-f", "image2pipe", "pipe:1"];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        process = Process.Start(info) ?? throw new IOException("FFmpeg failed to start.");
        output = new BufferedStream(process.StandardOutput.BaseStream, 65536);
        stderr = MediaTools.DrainOutputAsync(process.StandardError, 2000);
    }

    public async Task<WardogsFrame?> ReadAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var signature = await ReadHeaderLineAsync(deadline.Token);
            if (signature is null)
            {
                await FinishAsync(token);
                return null;
            }
            var dimensions = (await ReadHeaderLineAsync(deadline.Token))?.Split(' ');
            if (signature != "P5" || dimensions is not { Length: 2 }
                || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) || width is < 1 or > 2048
                || !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) || height is < 1 or > 2048
                || await ReadHeaderLineAsync(deadline.Token) != "255")
                throw new InvalidDataException("FFmpeg returned an invalid grayscale frame header.");

            var pixels = new byte[width * height];
            await output.ReadExactlyAsync(pixels, deadline.Token);
            return new(width, height, pixels);
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("FFmpeg returned an incomplete grayscale frame.", ex); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Timed out waiting for FFmpeg to decode a frame."); }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken token)
    {
        for (int i = 0; i < header.Length; i++)
        {
            if (await output.ReadAsync(header.AsMemory(i, 1), token) == 0)
                return i == 0 ? null : throw new EndOfStreamException();
            if (header[i] == '\n') return Encoding.ASCII.GetString(header, 0, i);
        }
        throw new InvalidDataException("FFmpeg frame header exceeded its size limit.");
    }

    public async Task FinishAsync(CancellationToken token)
    {
        if (completed) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var error = await stderr.WaitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new IOException("FFmpeg could not scan the recording: " + error);
            completed = true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Timed out waiting for FFmpeg to finish decoding."); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (completed) return;
            output.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            output.Dispose();
            process.Dispose();
        }
    }
}
