using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JamesTools.Core.Diagnostics;

/// <summary>Read-only documented NVML calls. Each optional export/sensor fails independently.</summary>
internal sealed class NvidiaTelemetry : IDisposable
{
    private IntPtr _library, _device;
    private Shutdown? _shutdown;
    private readonly Dictionary<string, Delegate?> _exports = new();
    private readonly HashSet<string> _unsupported = new();
    private readonly Func<string, Type, Delegate?>? _resolveExport;
    public Dictionary<string, string> Status { get; } = new();
    public bool Available => _device != IntPtr.Zero;
    public string? Name { get; private set; }
    public string? Driver { get; private set; }
    public NvidiaTelemetry(Func<string, Type, Delegate?>? resolveExport = null)
    {
        _resolveExport = resolveExport;
        foreach (var path in resolveExport is not null ? [] : new[] { Path.Combine(Environment.SystemDirectory, "nvml.dll"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll") })
            try { if (NativeLibrary.TryLoad(path, out _library)) break; } catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or FileLoadException) { }
        if (_library == IntPtr.Zero && resolveExport is null) return;
        var init = Export<Init>("nvmlInit_v2");
        if (init is null || init() != 0) return;
        _shutdown = Export<Shutdown>("nvmlShutdown");
        var handle = Export<Device>("nvmlDeviceGetHandleByIndex_v2");
        if (handle is null || handle(0, out _device) != 0) { _device = IntPtr.Zero; return; }
        var name = new StringBuilder(96);
        if (Export<DeviceString>("nvmlDeviceGetName")?.Invoke(_device, name, 96) == 0) Name = name.ToString();
        var driver = new StringBuilder(96);
        if (Export<SystemString>("nvmlSystemGetDriverVersion")?.Invoke(driver, 96) == 0) Driver = driver.ToString();
    }
    private T? Export<T>(string name) where T : Delegate
    {
        if (_exports.TryGetValue(name, out var cached)) return (T?)cached;
        var result = _resolveExport is not null ? (T?)_resolveExport(name, typeof(T)) :
            _library != IntPtr.Zero && NativeLibrary.TryGetExport(_library, name, out var pointer) ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : null;
        _exports[name] = result; return result;
    }
    private bool Accept(string key, int? code)
    {
        Status[key] = code == 0 ? "Available" : code is null ? "Unavailable: NVML export absent" : $"Unavailable: NVML status {code}";
        if (code is null or 3 or 4 or 6) _unsupported.Add(key);
        return code == 0;
    }
    public double? SampleUtilization()
    {
        var value = new Utilization();
        return Available && Export<GetUtilization>("nvmlDeviceGetUtilizationRates")?.Invoke(_device, out value) == 0 && value.Gpu <= 100 ? value.Gpu : null;
    }
    public Dictionary<string, double?> Sample()
    {
        var result = new[] { "gpuPowerW", "gpuPowerLimitW", "gpuConfiguredPowerLimitW", "gpuFanPercent", "gpuFanRpm", "gpuPState", "gpuPercent", "vramUsedMiB", "vramCapacityMiB", "gpuClockMHz", "gpuMemoryClockMHz", "gpuTemperatureC", "gpuMemoryTemperatureC", "gpuClockReasons", "gpuThermalLimited", "gpuPowerLimited", "gpuConfiguredClockLimited", "gpuIdle", "gpuPowerPercentOfLimit" }.ToDictionary(key => key, _ => (double?)null);
        if (!Available) return result;
        void UInt(string key, string function, double divisor = 1)
        {
            result[key] = null;
            if (_unsupported.Contains(key)) return;
            uint value = 0;
            if (Accept(key, Export<GetUInt>(function)?.Invoke(_device, out value))) result[key] = value / divisor;
        }
        UInt("gpuPowerW", "nvmlDeviceGetPowerUsage", 1000);
        UInt("gpuPowerLimitW", "nvmlDeviceGetEnforcedPowerLimit", 1000);
        UInt("gpuConfiguredPowerLimitW", "nvmlDeviceGetPowerManagementLimit", 1000);
        UInt("gpuFanPercent", "nvmlDeviceGetFanSpeed");
        UInt("gpuPState", "nvmlDeviceGetPerformanceState");
        if (result["gpuPState"] > 15) result["gpuPState"] = null;
        var util = new Utilization();
        result["gpuPercent"] = Accept("gpuPercent", Export<GetUtilization>("nvmlDeviceGetUtilizationRates")?.Invoke(_device, out util)) && util.Gpu <= 100 ? util.Gpu : null;
        var mem = new Memory();
        if (Accept("vramUsedMiB", Export<GetMemory>("nvmlDeviceGetMemoryInfo")?.Invoke(_device, out mem)))
        { result["vramUsedMiB"] = mem.Used / 1048576.0; result["vramCapacityMiB"] = mem.Total / 1048576.0; }
        foreach (var (key, domain) in new[] { ("gpuClockMHz", 0u), ("gpuMemoryClockMHz", 2u) })
        {
            uint value = 0;
            result[key] = Accept(key, Export<GetClock>("nvmlDeviceGetClockInfo")?.Invoke(_device, domain, out value)) ? value : null;
        }
        uint temp = 0;
        result["gpuTemperatureC"] = Accept("gpuTemperatureC", Export<GetClock>("nvmlDeviceGetTemperature")?.Invoke(_device, 0, out temp)) ? temp : null;
        var fan = new FanSpeed { Version = 12u | (1u << 24), Fan = 0 };
        result["gpuFanRpm"] = Accept("gpuFanRpm", Export<GetFan>("nvmlDeviceGetFanSpeedRPM")?.Invoke(_device, ref fan)) ? fan.Speed : null;
        ulong reasons = 0;
        var reasonApi = Export<GetReasons>("nvmlDeviceGetCurrentClocksEventReasons") ?? Export<GetReasons>("nvmlDeviceGetCurrentClocksThrottleReasons");
        if (Accept("gpuClockReasons", reasonApi?.Invoke(_device, out reasons)))
        {
            result["gpuClockReasons"] = reasons;
            result["gpuThermalLimited"] = (reasons & (0x20 | 0x40)) != 0 ? 1 : 0;
            result["gpuPowerLimited"] = (reasons & (0x4 | 0x80)) != 0 ? 1 : 0;
            result["gpuConfiguredClockLimited"] = (reasons & 0x2) != 0 ? 1 : 0;
            result["gpuIdle"] = (reasons & 0x1) != 0 ? 1 : 0;
        }
        // Documented field ID 82 is memory temperature, not guaranteed to be a junction sensor.
        var field = new FieldValue { FieldId = 82 };
        if (!_unsupported.Contains("gpuMemoryTemperatureC"))
        {
            var code = Export<GetFields>("nvmlDeviceGetFieldValues")?.Invoke(_device, 1, ref field);
            if (Accept("gpuMemoryTemperatureC", code == 0 ? field.Return : code))
                result["gpuMemoryTemperatureC"] = field.Type switch { 0 => field.Double, 1 or 2 => field.UInt, 3 => field.ULong, 4 => field.Long, _ => null };
        }
        result["gpuPowerPercentOfLimit"] = result.GetValueOrDefault("gpuPowerW") is { } watts && result.GetValueOrDefault("gpuPowerLimitW") is > 0 and var limit ? 100 * watts / limit : null;
        foreach (var key in result.Keys.ToArray()) if (result[key] is { } value && (!double.IsFinite(value) || value < 0)) result[key] = null;
        return result;
    }
    public void Dispose()
    {
        _device = IntPtr.Zero; _shutdown?.Invoke(); _shutdown = null;
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct FanSpeed { public uint Version, Fan, Speed; }
    // C unsigned long is 32 bits on Windows; the value union occupies 8 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct FieldValue
    {
        [FieldOffset(0)] public uint FieldId;
        [FieldOffset(4)] public uint Scope;
        [FieldOffset(8)] public long Timestamp;
        [FieldOffset(16)] public long Latency;
        [FieldOffset(24)] public int Type;
        [FieldOffset(28)] public int Return;
        [FieldOffset(32)] public double Double;
        [FieldOffset(32)] public uint UInt;
        [FieldOffset(32)] public ulong ULong;
        [FieldOffset(32)] public long Long;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Device(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUInt(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetClock(IntPtr device, uint kind, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetReasons(IntPtr device, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUtilization(IntPtr device, out Utilization value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMemory(IntPtr device, out Memory value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFan(IntPtr device, ref FanSpeed value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFields(IntPtr device, int count, ref FieldValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceString(IntPtr device, StringBuilder value, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SystemString(StringBuilder value, uint length);
}
