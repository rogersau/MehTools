using System.Text;
using System.Text.Json;

namespace JamesTools.Core.Diagnostics;

/// <summary>Buffered local raw exports; failure disables export without ending the recording.</summary>
internal sealed class DiagnosticRawWriter : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _frames, _hardware, _faults;
    public string? DirectoryPath { get; private set; }
    public string? Error { get; private set; }
    public DiagnosticRawWriter()
    {
        try
        {
            DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamesTools", "Diagnostics", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(DirectoryPath);
            _frames = Open("frames.csv"); _hardware = Open("hardware.jsonl"); _faults = Open("hard-faults.jsonl");
            _frames.WriteLine(FrameTelemetry.CsvHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.GetType().Name; Dispose(); }
    }
    private StreamWriter Open(string name) => new(new FileStream(Path.Combine(DirectoryPath!, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536), new UTF8Encoding(false), 65536);
    public void Frame(FrameTelemetry frame) => Write(() => _frames?.WriteLine(frame.ToCsv()));
    public void Fault(HardFaultObservation fault) => Write(() => _faults?.WriteLine(JsonSerializer.Serialize(fault)));
    public void Hardware(DiagnosticHardwareSample sample) => Write(() => _hardware?.WriteLine(JsonSerializer.Serialize(sample)));
    public void Finish(string report, string summaryJson, object markers)
    {
        Dispose();
        Write(() => { if (DirectoryPath is not null) { File.WriteAllText(Path.Combine(DirectoryPath, "report.md"), report); File.WriteAllText(Path.Combine(DirectoryPath, "summary.json"), summaryJson); File.WriteAllText(Path.Combine(DirectoryPath, "markers.json"), JsonSerializer.Serialize(markers)); } });
    }
    private void Write(Action action)
    {
        lock (_gate) try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { Error = ex.GetType().Name; Dispose(); }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            var writers = new[] { _frames, _hardware, _faults };
            _frames = null; _hardware = null; _faults = null;
            foreach (var writer in writers) try { writer?.Dispose(); } catch (IOException ex) { Error = ex.GetType().Name; }
        }
    }
}

public sealed record DiagnosticHardwareSample(double CollectionStartSeconds, double CollectionEndSeconds,
    double CounterWindowStartSeconds, double? ProcessWindowStartSeconds, double? GpuCollectionStartSeconds,
    double? GpuCollectionEndSeconds, IReadOnlyDictionary<string, double?> Values)
{
    public double? CpuWindowStartSeconds { get; init; }
    public double? ProcessWindowEndSeconds { get; init; }
}
