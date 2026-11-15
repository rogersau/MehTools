using System.Diagnostics;
using System.Management;
using JamesTools.Core.Diagnostics;

namespace JamesTools.Core.Mcp;

internal static class WindowsInventory
{
    private sealed record Query(string Category, string Class, string[] Fields, int Limit);
    private static readonly Query[] Queries =
    [
        new("cpu", "Win32_Processor", ["Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed"], 8),
        new("gpu", "Win32_VideoController", ["Name", "VideoProcessor", "DriverVersion", "DriverDate"], 16),
        new("memory", "Win32_PhysicalMemory", ["Manufacturer", "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType"], 64),
        new("board", "Win32_BaseBoard", ["Manufacturer", "Product", "Version"], 8),
        new("bios", "Win32_BIOS", ["Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate"], 8),
        new("storage", "Win32_DiskDrive", ["Model", "Size", "InterfaceType", "MediaType", "FirmwareRevision", "Status"], 64),
        new("network", "Win32_NetworkAdapter", ["ProductName", "Manufacturer", "AdapterType", "Speed", "NetConnectionStatus"], 64),
        new("drivers", "Win32_PnPSignedDriver", ["DeviceClass", "DriverProviderName", "DriverVersion", "DriverDate", "IsSigned"], 256),
        new("windows", "Win32_OperatingSystem", ["Caption", "Version", "BuildNumber", "OSArchitecture", "LastBootUpTime"], 1)
    ];

    internal static SystemInventory Read()
    {
        var components = new List<InventoryComponent>();
        var warnings = new List<string>();
        var budget = Stopwatch.StartNew();
        foreach (var query in Queries)
        {
            if (budget.Elapsed > TimeSpan.FromSeconds(10))
            {
                components.Add(new(query.Category, query.Category, new Dictionary<string, string?>(), new("unavailable", "Inventory time budget exceeded.")));
                continue;
            }
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"), new ObjectQuery($"SELECT {string.Join(',', query.Fields)} FROM {query.Class}"),
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(1), ReturnImmediately = true, Rewindable = false, BlockSize = 1 });
                using var rows = searcher.Get();
                var count = 0;
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        if (budget.Elapsed > TimeSpan.FromSeconds(10) || count >= query.Limit)
                        { warnings.Add($"{query.Category}: inventory truncated by row/time limit."); break; }
                        count++;
                        var properties = query.Fields.ToDictionary(f => f, f => row[f] is { } value ? WindowsBackendSupport.Text(WindowsBackendSupport.Number(value)) : (string?)null);
                        properties["units"] = query.Category switch
                        {
                            "memory" => "Capacity=bytes; Speed/ConfiguredClockSpeed=MT/s (SMBIOS reported)",
                            "cpu" => "MaxClockSpeed=MHz",
                            "storage" => "Size=bytes",
                            "network" => "Speed=bits/s",
                            _ => "Dates are WMI DMTF values including UTC offset."
                        };
                        var name = properties.GetValueOrDefault("Name") ?? properties.GetValueOrDefault("Model") ?? properties.GetValueOrDefault("ProductName") ?? query.Category + " " + count;
                        components.Add(new(query.Category, name, properties, new("available")));
                    }
                }
                if (count == 0) components.Add(new(query.Category, query.Category, new Dictionary<string, string?>(), new("unavailable", "No WMI records returned.")));
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                components.Add(new(query.Category, query.Category, new Dictionary<string, string?>(), new(WindowsBackendSupport.Status(ex), "Windows inventory query could not complete.")));
            }
        }
        try
        {
            var topology = CpuTopology.Read().Take(1024).ToDictionary(p => p.Key, p => p.Value);
            components.Add(new("cpu", "Processor topology", topology.ToDictionary(p => p.Key, p => (string?)p.Value),
                new(topology.Count > 0 ? "available" : "unavailable")));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EntryPointNotFoundException)
        { warnings.Add("CPU topology: " + WindowsBackendSupport.Status(ex)); }
        components.Add(new("cpu", "Runtime instruction capabilities", InstructionSets(), new("available",
            "Runtime IsSupported includes operating system support; this is not a complete CPUID inventory.")));
        warnings.Add("Static inventory is cached for this backend lifetime. WMI status is provider evidence, not a hardware health test. Identifiers, serials, MAC/IP addresses and user names are not queried.");
        return new(1, DateTimeOffset.UtcNow, components, warnings);
    }

    internal static IReadOnlyDictionary<string, string?> InstructionSets() => new Dictionary<string, string?>
    {
        ["x86"] = System.Runtime.Intrinsics.X86.X86Base.IsSupported.ToString(),
        ["sse"] = System.Runtime.Intrinsics.X86.Sse.IsSupported.ToString(),
        ["sse2"] = System.Runtime.Intrinsics.X86.Sse2.IsSupported.ToString(),
        ["sse3"] = System.Runtime.Intrinsics.X86.Sse3.IsSupported.ToString(),
        ["ssse3"] = System.Runtime.Intrinsics.X86.Ssse3.IsSupported.ToString(),
        ["sse41"] = System.Runtime.Intrinsics.X86.Sse41.IsSupported.ToString(),
        ["sse42"] = System.Runtime.Intrinsics.X86.Sse42.IsSupported.ToString(),
        ["avx"] = System.Runtime.Intrinsics.X86.Avx.IsSupported.ToString(),
        ["avx2"] = System.Runtime.Intrinsics.X86.Avx2.IsSupported.ToString(),
        ["avx512f"] = System.Runtime.Intrinsics.X86.Avx512F.IsSupported.ToString(),
        ["fma"] = System.Runtime.Intrinsics.X86.Fma.IsSupported.ToString(),
        ["aes"] = System.Runtime.Intrinsics.X86.Aes.IsSupported.ToString(),
        ["bmi1"] = System.Runtime.Intrinsics.X86.Bmi1.IsSupported.ToString(),
        ["bmi2"] = System.Runtime.Intrinsics.X86.Bmi2.IsSupported.ToString(),
        ["arm_advsimd"] = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported.ToString(),
        ["arm_aes"] = System.Runtime.Intrinsics.Arm.Aes.IsSupported.ToString()
    };
}
