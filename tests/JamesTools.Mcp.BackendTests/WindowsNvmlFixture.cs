using System.Reflection;
using JamesTools.Core.Diagnostics;

internal static class WindowsNvmlFixture
{
    private static int _initializations, _shutdowns;

    internal static void VerifySharedOwnership()
    {
        _initializations = _shutdowns = 0;
        using var telemetry = new NvidiaTelemetry(Resolve);
        using (var sampler = new LiveHardwareSampler(telemetry))
            if (_initializations != 1) throw new InvalidOperationException("Shared sampler initialized NVML again.");
        if (_shutdowns != 0 || !telemetry.Available) throw new InvalidOperationException("Shared sampler disposed the owner's NVML instance.");
        telemetry.Dispose();
        telemetry.Dispose();
        if (_shutdowns != 1) throw new InvalidOperationException("NVML shutdown must occur exactly once.");
    }

    private static Delegate? Resolve(string name, Type type)
    {
        var method = name switch { "nvmlInit_v2" => nameof(Initialize), "nvmlShutdown" => nameof(Shutdown), "nvmlDeviceGetHandleByIndex_v2" => nameof(Device), _ => null };
        return method is null ? null : Delegate.CreateDelegate(type, typeof(WindowsNvmlFixture).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!);
    }
    private static int Initialize() { _initializations++; return 0; }
    private static int Shutdown() { _shutdowns++; return 0; }
    private static int Device(uint index, out IntPtr device) { device = new IntPtr(1); return 0; }
}
