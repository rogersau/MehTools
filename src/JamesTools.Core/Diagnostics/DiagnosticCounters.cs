using System.Globalization;
using System.Runtime.InteropServices;

namespace JamesTools.Core.Diagnostics;

/// <summary>Optional Windows performance counters. A missing counter never becomes a zero reading.</summary>
internal sealed class DiagnosticCounters : IDisposable
{
    private IntPtr _query;
    private readonly List<(string Name, IntPtr Handle)> _cores = new();
    private readonly IntPtr _pagesInput, _diskRead, _diskWrite, _diskLatency;
    private bool _primed;
    private bool _currentValid;
    private double? _previousCollectionSeconds;
    public double? WindowStartSeconds { get; private set; }
    private readonly IntPtr _gpuDedicated, _gpuShared;
    private readonly List<(string Name, IntPtr Handle)> _frequencies = new();
    public IReadOnlyDictionary<string, double?> Frequencies { get; private set; } = new Dictionary<string, double?>();

    public DiagnosticCounters(bool includeCores = true)
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0) return;
        // Numeric instances avoid localized names and wildcard expansion, including on multi-group PCs.
        for (ushort group = 0; includeCores && group < GetActiveProcessorGroupCount(); group++)
            for (var cpu = 0; cpu < GetActiveProcessorCount(group); cpu++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{group},{cpu}");
                var handle = Add($@"\Processor Information({name})\% Processor Time");
                if (handle != IntPtr.Zero) _cores.Add((name, handle));
                var frequency = Add($@"\Processor Information({name})\Processor Frequency");
                if (frequency != IntPtr.Zero) _frequencies.Add((name, frequency));
            }
        _gpuDedicated = Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _gpuShared = Add(@"\GPU Process Memory(*)\Shared Usage");
        _pagesInput = Add(@"\Memory\Pages Input/sec");
        _diskRead = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _diskLatency = Add(@"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer");
    }

    private IntPtr Add(string path) => PdhAddEnglishCounterW(_query, path, 0, out var counter) == 0 ? counter : IntPtr.Zero;

    public CounterSample Sample(double? recordingSeconds = null)
    {
        _currentValid = false;
        Frequencies = new Dictionary<string, double?>();
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return new([], null, null, null, null);
        WindowStartSeconds = _previousCollectionSeconds;
        _previousCollectionSeconds = recordingSeconds;
        if (!_primed) { _primed = true; return new([], null, null, null, null); }
        _currentValid = true;
        Frequencies = _frequencies.ToDictionary(f => f.Name, f => Read(f.Handle));
        var cores = new List<CoreReading>();
        foreach (var (name, handle) in _cores)
            if (Read(handle) is { } value) cores.Add(new(name, Math.Clamp(value, 0, 100)));
        return new(cores, Read(_pagesInput), Read(_diskRead) / 1048576, Read(_diskWrite) / 1048576, Read(_diskLatency) * 1000);
    }

    private static double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0 ||
            value.Status > 1 || !double.IsFinite(value.Value) || value.Value < 0) return null;
        return value.Value;
    }

    public (double? DedicatedMiB, double? SharedMiB) ProcessGpuMemory(IReadOnlySet<int> pids)
        => _currentValid ? (ArraySum(_gpuDedicated, pids) / 1048576, ArraySum(_gpuShared, pids) / 1048576) : (null, null);

    private static double? ArraySum(IntPtr counter, IReadOnlySet<int> pids)
    {
        if (counter == IntPtr.Zero || pids.Count == 0) return null;
        uint bytes = 0, count = 0;
        const uint moreData = 0x800007D2;
        if (PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out count, IntPtr.Zero) != moreData || bytes is 0 or > 1048576) return null;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out count, buffer) != 0) return null;
            var stride = Marshal.SizeOf<CounterItem>();
            if ((long)count * stride > bytes) return null;
            double sum = 0; var found = false;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                var name = Marshal.PtrToStringUni(item.Name) ?? "";
                if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                var end = name.IndexOf('_', 4);
                if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out var pid) || !pids.Contains(pid)) continue;
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Value) || item.Value.Value < 0) return null;
                sum += item.Value.Value; found = true;
            }
            return found ? sum : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    public static (double? UsedMiB, double? LimitMiB) ReadCommit()
    {
        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref info, info.Size)) return (null, null);
        return ((double)info.CommitTotal * info.PageSize / 1048576, (double)info.CommitLimit * info.PageSize / 1048576);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, nuint userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}

internal sealed record CoreReading(string Name, double Percent);
internal sealed record CounterSample(IReadOnlyList<CoreReading> Cores, double? PagesInputPerSecond,
    double? DiskReadMiBPerSecond, double? DiskWriteMiBPerSecond, double? DiskLatencyMs);
