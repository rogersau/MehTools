using System.Runtime.InteropServices;

namespace JamesTools.Core.Platform;

public sealed record VibranceDisplay(int Index, int Percent, string? DeviceName = null);

/// <summary>Uses NVAPI's display vibrance interface. Constructing and reading never change a display.</summary>
public sealed class NvidiaVibrance : IDisposable
{
    private const int NvOk = 0;
    private const int NvidiaDeviceNotFound = -6;
    private const int EndEnumeration = -7;
    private const int MaxDisplays = 64;
    private readonly object gate = new();
    private IntPtr library;
    private NvUnload? unload;
    private NvEnumDisplay? enumerate;
    private NvDvc? get;
    private NvDvc? set;
    private NvGetAssociatedDisplayName? getDisplayName;
    private bool initialized;
    private bool disposed;

    public NvidiaVibrance()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("NVIDIA vibrance requires 64-bit Windows.");
        if (!NativeLibrary.TryLoad("nvapi64.dll", out library))
            throw new InvalidOperationException("The NVIDIA driver interface (nvapi64.dll) was not found.");

        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var queryAddress))
                throw new InvalidOperationException("The NVIDIA driver does not expose its NVAPI interface.");
            var query = Marshal.GetDelegateForFunctionPointer<NvQueryInterface>(queryAddress);
            var initialize = Resolve<NvInitialize>(query, 0x0150E828);
            unload = Resolve<NvUnload>(query, 0xD22BDD7E);
            enumerate = Resolve<NvEnumDisplay>(query, 0x9ABDD40D);
            get = Resolve<NvDvc>(query, 0x0E45002D);
            set = Resolve<NvDvc>(query, 0x4A82C2B1);
            var displayNameAddress = query(0x22A78B05); // NvAPI_GetAssociatedNvidiaDisplayName, NVIDIA NVAPI reference
            getDisplayName = displayNameAddress == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<NvGetAssociatedDisplayName>(displayNameAddress);
            var status = initialize();
            if (status == NvidiaDeviceNotFound)
                throw new InvalidOperationException("No NVIDIA device was found.");
            Check(status, "initialize NVIDIA driver interface");
            initialized = true;
        }
        catch
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
            throw;
        }
    }

    public IReadOnlyList<VibranceDisplay> Read()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ReadDisplays().Select(x => new VibranceDisplay(x.Index, PercentOf(x.Info), x.DeviceName)).ToArray();
        }
    }

    /// <summary>Sets one freshly resolved NVIDIA display matching its Windows device identity.</summary>
    public VibranceDisplay SetDisplay(string deviceName, int percent)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("A Windows display identity is required.", nameof(deviceName));
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        lock (gate)
        {
            ThrowIfDisposed();
            var display = ReadDisplays().SingleOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (display is null) throw new InvalidOperationException($"NVIDIA display '{deviceName}' is unavailable or could not be mapped.");
            var info = display.Info;
            info.Current = (int)Math.Round(info.Minimum + ((double)info.Maximum - info.Minimum) * percent / 100.0);
            try
            {
                Check(set!(display.Handle, 0, ref info), $"set vibrance on {deviceName}");
                var verify = NewInfo();
                Check(get!(display.Handle, 0, ref verify), $"verify vibrance on {deviceName}");
                ValidateRange(verify, display.Index);
                var actual = PercentOf(verify);
                if (Math.Abs(actual - percent) > 1)
                    throw new InvalidOperationException($"Vibrance readback on {deviceName} was {actual}%, expected {percent}%.");
                return new VibranceDisplay(display.Index, actual, display.DeviceName);
            }
            catch (Exception ex)
            {
                var rollback = display.Info;
                var restored = set!(display.Handle, 0, ref rollback) == NvOk;
                throw new InvalidOperationException(ex.Message + (restored ? " Original value restored." : " Rollback failed; check NVIDIA settings."), ex);
            }
        }
    }

    public IReadOnlyList<VibranceDisplay> SetAll(int percent)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Vibrance must be between 0 and 100 percent.");

        lock (gate)
        {
            ThrowIfDisposed();
            // Read all displays first so a read failure cannot leave only some displays changed.
            var displays = ReadDisplays();
            var result = new List<VibranceDisplay>(displays.Count);
            var changed = new List<DisplayInfo>();
            try
            {
                foreach (var display in displays)
                {
                    var info = display.Info;
                    info.Current = (int)Math.Round(info.Minimum + ((double)info.Maximum - info.Minimum) * percent / 100.0);
                    Check(set!(display.Handle, 0, ref info), $"set vibrance on display {display.Index}");
                    changed.Add(display);
                    info = NewInfo();
                    Check(get!(display.Handle, 0, ref info), $"verify vibrance on display {display.Index}");
                    ValidateRange(info, display.Index);
                    var actual = PercentOf(info);
                    if (Math.Abs(actual - percent) > 1)
                        throw new InvalidOperationException($"Vibrance readback on display {display.Index} was {actual}%, expected {percent}%.");
                    result.Add(new VibranceDisplay(display.Index, actual));
                }
            }
            catch (Exception ex)
            {
                var rollbackFailed = false;
                foreach (var display in changed.AsEnumerable().Reverse())
                {
                    var original = display.Info;
                    if (set!(display.Handle, 0, ref original) != NvOk) rollbackFailed = true;
                }
                throw new InvalidOperationException(ex.Message + (rollbackFailed
                    ? " Some displays could not be restored; check NVIDIA settings."
                    : " Previous display values were restored."), ex);
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            if (initialized)
            {
                unload!();
                initialized = false;
            }
            if (library != IntPtr.Zero)
            {
                NativeLibrary.Free(library);
                library = IntPtr.Zero;
            }
        }
    }

    private List<DisplayInfo> ReadDisplays()
    {
        var displays = new List<DisplayInfo>();
        for (uint index = 0; index < MaxDisplays; index++)
        {
            var status = enumerate!(index, out var handle);
            if (status == EndEnumeration)
                break;
            if (status == NvidiaDeviceNotFound)
                throw new InvalidOperationException("No NVIDIA device was found.");
            Check(status, $"enumerate NVIDIA display {index}");
            var info = NewInfo();
            Check(get!(handle, 0, ref info), $"read vibrance on display {index}");
            ValidateRange(info, (int)index);
            string? deviceName = null;
            try
            {
                var buffer = new byte[64];
                if (getDisplayName is not null && getDisplayName(handle, buffer) == NvOk)
                    deviceName = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            }
            catch { }
            displays.Add(new DisplayInfo((int)index, handle, info, deviceName));
        }

        if (displays.Count == 0)
            throw new InvalidOperationException("No NVIDIA-controlled displays were found.");
        return displays;
    }

    private static DvcInfo NewInfo() => new() { Version = (uint)Marshal.SizeOf<DvcInfo>() | 0x10000 };

    private static void ValidateRange(DvcInfo info, int index)
    {
        if (info.Maximum <= info.Minimum || info.Current < info.Minimum || info.Current > info.Maximum)
            throw new InvalidOperationException($"NVIDIA returned an invalid vibrance range or value for display {index}.");
    }

    private static int PercentOf(DvcInfo info) => (int)Math.Round(100.0 * (info.Current - (double)info.Minimum) / (info.Maximum - (double)info.Minimum));

    private static T Resolve<T>(NvQueryInterface query, uint id) where T : Delegate
    {
        var address = query(id);
        if (address == IntPtr.Zero)
            throw new InvalidOperationException($"NVIDIA driver interface 0x{id:X8} is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void Check(int status, string operation)
    {
        if (status != NvOk)
            throw new InvalidOperationException($"Could not {operation} (NVAPI status {status}).");
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(NvidiaVibrance));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DvcInfo
    {
        public uint Version;
        public int Current;
        public int Minimum;
        public int Maximum;
        public int Default;
    }

    private sealed record DisplayInfo(int Index, IntPtr Handle, DvcInfo Info, string? DeviceName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NvQueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvInitialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvUnload();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvEnumDisplay(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvDvc(IntPtr handle, uint outputId, ref DvcInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvGetAssociatedDisplayName(IntPtr handle, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] displayName);
}
