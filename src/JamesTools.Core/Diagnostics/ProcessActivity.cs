using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JamesTools.Core.Diagnostics;

internal sealed record ProcessActivityCounters(int Pid, long StartTicks, double Seconds, ulong? ReadBytes, ulong? WriteBytes,
    ulong? ReadOperations, ulong? WriteOperations, uint? PageFaults);
internal sealed record ProcessActivityRate(double StartSeconds, double EndSeconds, double? ReadMiBps, double? WriteMiBps,
    double? ReadOperationsPerSecond, double? WriteOperationsPerSecond, double? PageFaultsPerSecond);

internal sealed class ProcessActivity
{
    private Dictionary<int, ProcessActivityCounters> _previous = new();
    public static ProcessActivityCounters Read(Process process, long start, double seconds)
    {
        using var handle = OpenProcess(0x1000, false, process.Id);
        var ioOk = GetProcessIoCounters(handle, out var io);
        var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        var memOk = GetProcessMemoryInfo(handle, ref memory, memory.Size);
        return new(process.Id, start, seconds, ioOk ? io.ReadBytes : null, ioOk ? io.WriteBytes : null,
            ioOk ? io.ReadOperations : null, ioOk ? io.WriteOperations : null, memOk ? memory.PageFaults : null);
    }
    public ProcessActivityRate Update(IReadOnlyList<ProcessActivityCounters> readings, double end)
    {
        var rates = new List<ProcessActivityRate>();
        foreach (var r in readings)
        {
            if (!_previous.TryGetValue(r.Pid, out var p) || p.StartTicks != r.StartTicks || r.Seconds <= p.Seconds) continue;
            var seconds = r.Seconds - p.Seconds;
            double? Delta(ulong? now, ulong? prior) => now is { } n && prior is { } b && n >= b ? (n - b) / seconds : null;
            rates.Add(new(p.Seconds, r.Seconds, Delta(r.ReadBytes, p.ReadBytes) / 1048576,
                Delta(r.WriteBytes, p.WriteBytes) / 1048576, Delta(r.ReadOperations, p.ReadOperations),
                Delta(r.WriteOperations, p.WriteOperations), Delta(r.PageFaults, p.PageFaults)));
        }
        _previous = readings.ToDictionary(r => r.Pid);
        // Do not silently report partial totals when a matched process lacks a baseline or access.
        double? Sum(Func<ProcessActivityRate, double?> get) => rates.Count == readings.Count && rates.Count > 0 && rates.All(r => get(r) is not null) ? rates.Sum(r => get(r)!.Value) : null;
        return new(rates.Count > 0 ? rates.Min(r => r.StartSeconds) : end, end, Sum(r => r.ReadMiBps), Sum(r => r.WriteMiBps),
            Sum(r => r.ReadOperationsPerSecond), Sum(r => r.WriteOperationsPerSecond), Sum(r => r.PageFaultsPerSecond));
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    { public uint Size, PageFaults; public nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile; }
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("psapi.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
}
