using System.Runtime.InteropServices;

namespace JamesTools.Core.Platform;

internal static class HdrDetection
{
    public static bool IsHdrActive() => HdrStatus.IsHdrActive();
}

public static class HdrStatus
{
    public static bool IsHdrActive() => TryIsHdrActive() ?? false;

    /// <summary>Returns null when Windows could not provide an authoritative active-display HDR state.</summary>
    public static bool? TryIsHdrActive()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            uint pathCount = 0, modeCount = 0;
            var err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (err != 0 || pathCount == 0) return null;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err != 0) return null;
            bool active = false;
            for (int i = 0; i < pathCount; i++)
            {
                var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                info.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                info.header.adapterId = paths[i].targetInfo.adapterId;
                info.header.id = paths[i].targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref info) == 0)
                {
                    bool supported = (info.value & 1) != 0; // advancedColorSupported
                    bool enabled = (info.value & 2) != 0;   // advancedColorEnabled
                    if (supported && enabled) active = true;
                }
                else return null;
            }
            return active;
        }
        catch { return null; }
    }

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public bool targetAvailable; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    // wingdi.h: 16-byte header followed by a 48-byte union (the largest
    // member is DISPLAYCONFIG_TARGET_MODE / DISPLAYCONFIG_VIDEO_SIGNAL_INFO).
    // We only query paths, but Windows still writes the entire mode array.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] private ulong unionAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }
}
