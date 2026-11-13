using System.Runtime.InteropServices;
namespace JamesTools.Core.Diagnostics;
public sealed class RollingHardwareSource : IDisposable
{
    private readonly LiveHardwareSampler _hardware = new();
    private readonly DiagnosticCounters _counters = new(includeCores: false);
    public HardwareReading Sample(Func<double> seconds)
    {
        var h=_hardware.Sample(); var c=_counters.Sample();
        var m=new Memory { Length=(uint)Marshal.SizeOf<Memory>() };
        double? used=GlobalMemoryStatusEx(ref m) ? (m.Total-m.Available)/1048576.0 : null;
        return new(seconds(),h.Cpu,h.Gpu,used,c.PagesInputPerSecond,c.DiskReadMiBPerSecond,c.DiskWriteMiBPerSecond,c.DiskLatencyMs);
    }
    public void Dispose() { _counters.Dispose(); _hardware.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public uint Length, Load; public ulong Total,Available,Page,AvailablePage,Virtual,AvailableVirtual,Extended; }
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
}
