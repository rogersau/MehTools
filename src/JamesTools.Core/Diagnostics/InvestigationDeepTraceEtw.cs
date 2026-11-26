using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32.SafeHandles;

// Absolute QPC is required to align evidence with the recording's shared clock.
#pragma warning disable CS0618

namespace JamesTools.Core.Diagnostics;

internal sealed class InvestigationDeepTracePlatform : IInvestigationDeepTracePlatform
{
    public bool HasTraceAccess => OperatingSystem.IsWindows() && TraceEventSession.IsElevated() == true;
    public long ProcessStartUtcTicks { get { using var p = Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime().Ticks; } }
    public IInvestigationDeepTraceWatchdog ArmWatchdog(string name, int seconds) => InvestigationDeepTraceOwnership.Arm(name, seconds);
    public IInvestigationDeepTraceSession CreateSession(string name, string? executablePath, string? rawDirectory)
        => new InvestigationDeepTraceEtw(name, executablePath, rawDirectory);
}

/// <summary>
/// TraceEvent 4.0.0 APIs checked against its shipped XML and Microsoft's PerfView sources.
/// https://github.com/microsoft/perfview/blob/main/src/TraceEvent/TraceEventSession.cs
/// https://learn.microsoft.com/windows/win32/api/evntrace/nf-evntrace-controltracew
/// https://learn.microsoft.com/windows-hardware/drivers/usbcon/how-to-capture-a-usb-event-trace
/// </summary>
internal sealed class InvestigationDeepTraceEtw : IInvestigationDeepTraceSession
{
    private readonly string _name;
    private readonly string? _executable;
    private readonly TraceEventSession _session;
    private readonly bool _raw;
    private ETWTraceEventSource? _source;
    private InvestigationDeepTrace? _sink;
    private long _finalLoss;
    private uint _logBuffersLost, _realtimeBuffersLost;
    private bool _stopped;
    private readonly object _control = new();

    internal InvestigationDeepTraceEtw(string name, string? executablePath, string? rawDirectory)
    {
        _name = name;
        _executable = string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFullPath(executablePath);
        _raw = rawDirectory is not null;
        string? file = _raw ? PrivateRawFile(rawDirectory!) : null;
        // Never restart an existing session, even in the astronomically unlikely event of a GUID collision.
        _session = new TraceEventSession(name, file!, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
        { StopOnDispose = false, BufferSizeMB = 16 };
        if (_raw) _session.CircularBufferMB = 256;
    }

    public void Start(InvestigationDeepTrace sink, bool usb)
    {
        _sink = sink;
        var flags = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread |
            KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Dispatcher |
            KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.Driver |
            KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt |
            KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit |
            KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit |
            KernelTraceEventParser.Keywords.MemoryHardFaults;
        // EnableKernelProvider(Profile) changes the machine-wide profile interval (and Stop resets it).
        // Enable sampling separately on our handle, preserving the existing interval and all foreign sessions.
        _session.EnableKernelProvider(flags);
        bool sampling = false, stacks = false;
        try { InvestigationDeepTraceNative.EnableSampling(_name, flags); sampling = true; }
        catch (Exception ex) { sink.SetStatus("CPU", "unavailable", "Sampling enable failed (" + ex.GetType().Name + "); other channels remain active."); }
        if (sampling)
        {
            try { InvestigationDeepTraceNative.EnableSampleStacks(_name); stacks = true; }
            catch (Exception ex) { sink.SetStatus("Stacks", "unavailable", "Stack enable failed (" + ex.GetType().Name + "); CPU addresses remain available."); }
        }
        if (_raw)
        {
            InvestigationDeepTraceNative.EnableRealtime(_name);
            sink.SetStatus("Raw ETL", "local-only", "Private local circular ETL, capped at 256 MiB; older buffers may be overwritten. Contains sensitive system data; excluded from summaries.");
        }
        // Session.Source rejects file-backed sessions; the documented update above adds real-time delivery.
        _source = new ETWTraceEventSource(_name, TraceEventSourceType.Session);
        // Own bounded thread/module maps. TraceEvent's default file-name/history tracking is intentionally disabled.
        var kernel = new KernelTraceEventParser(_source, KernelTraceEventParser.ParserTrackingOptions.None);
        kernel.ProcessStart += e => TrackProcess(e.ProcessID);
        kernel.ProcessDCStart += e => TrackProcess(e.ProcessID, true);
        kernel.ProcessStop += e => sink.ProcessStop(e.ProcessID);
        kernel.ThreadStart += e => sink.ThreadStart(e.ProcessID, e.ThreadID);
        kernel.ThreadDCStart += e => sink.ThreadStart(e.ProcessID, e.ThreadID);
        kernel.ThreadStop += e => sink.ThreadStop(e.ThreadID);
        kernel.ThreadCSwitch += e => sink.Switch(e.TimeStampQPC, e.ProcessorNumber, e.OldThreadID, e.NewThreadID,
            (int)e.OldThreadState, e.OldThreadWaitReason.ToString());
        kernel.DispatcherReadyThread += e => sink.Ready(e.TimeStampQPC, e.AwakenedThreadID);
        kernel.ImageLoad += e => Image(e);
        kernel.ImageDCStart += e => Image(e);
        kernel.ImageUnload += e => Image(e, true);
        kernel.PerfInfoSample += e => sink.Sample(e.TimeStampQPC, e.ProcessID, e.ThreadID, e.InstructionPointer, e.ExecutingDPC || e.ExecutingISR);
        kernel.StackWalkStack += e =>
        {
            if (!sink.WantsProcess(e.ProcessID, e.ThreadID)) return;
            var ips = new ulong[Math.Min(32, e.FrameCount)];
            for (int i = 0; i < ips.Length; i++) ips[i] = e.InstructionPointer(i);
            sink.Stack(e.EventTimeStampQPC, e.ProcessID, e.ThreadID, ips, e.FrameCount > ips.Length);
        };
        kernel.PerfInfoDPC += e => sink.Activity(e.TimeStampQPC, "DPC", "DPC", null, null, e.ElapsedTimeMSec, sink.Location(0, e.Routine, e.TimeStampQPC));
        kernel.PerfInfoISR += e => sink.Activity(e.TimeStampQPC, "ISR", "ISR", null, null, e.ElapsedTimeMSec, sink.Location(0, e.Routine, e.TimeStampQPC));
        kernel.DiskIODriverMajorFunctionCall += e => sink.Activity(e.TimeStampQPC, "Driver", "MajorFunctionCall", null, null, location: sink.Location(0, e.RoutineAddr, e.TimeStampQPC));
        kernel.DiskIODriverCompletionRoutine += e => sink.Activity(e.TimeStampQPC, "Driver", "CompletionRoutine", null, null, location: sink.Location(0, e.Routine, e.TimeStampQPC));
        kernel.DiskIORead += e => Disk(e, "Read");
        kernel.DiskIOWrite += e => Disk(e, "Write");
        kernel.FileIORead += e => File(e, "ReadRequest");
        kernel.FileIOWrite += e => File(e, "WriteRequest");
        kernel.FileIOOperationEnd += e => sink.Activity(e.TimeStampQPC, "File", "OperationEnd", ProcessForThread(e.ProcessID, e.ThreadID), e.ThreadID);
        kernel.MemoryHardFault += e => sink.Activity(e.TimeStampQPC, "Memory", "HardFault", ProcessForThread(e.ProcessID, e.ThreadID), e.ThreadID, e.ElapsedTimeMSec);
        sink.SetStatus("Scheduling", "partial", "Context switches, ready latency, wait-until-ready and execution intervals from paired events. Initial/final partial intervals omitted; loss invalidates open pairs. Execution includes interrupt time; not pure application CPU time.");
        if (sampling) sink.SetStatus("CPU", "partial", "Statistical CPU samples at the existing system interval, module+offset or unknown IP; no duration extrapolation or function attribution. Sampling interval unchanged.");
        if (stacks) sink.SetStatus("Stacks", "partial", "CPU stack addresses requested; delivery/platform restrictions may prevent stacks. Symbols uncollected; no registry changes or symbol downloads.");
        sink.SetStatus("DPC/ISR", "partial", "System-wide routine addresses and measured durations; driver attribution only when an observed image range contains the address.");
        sink.SetStatus("Disk/File/Memory", "partial", "Disk completion durations, file request/end counts and hard faults. File paths and payloads excluded; file request durations are not inferred.");
        SeedProcesses();

        var providers = new Dictionary<Guid, (string Category, string Name)>();
        EnableProvider("Microsoft-Windows-DxgKrnl", "GPU", providers);
        sink.SetStatus("GPU", providers.Values.Any(p => p.Category == "GPU") ? "partial" : "unavailable",
            "DxgKrnl provider event metadata/counts when emitted; no reconstructed GPU queue latency, GPU utilization or vendor-specific driver causality.");
        if (usb)
        {
            foreach (var name in new[] { "Microsoft-Windows-USB-USBHUB3", "Microsoft-Windows-USB-UCX", "Microsoft-Windows-USB-USBXHCI", "Microsoft-Windows-USB-USBHUB", "Microsoft-Windows-USB-USBPORT" })
                EnableProvider(name, "USB", providers);
            sink.SetStatus("USB", providers.Values.Any(p => p.Category == "USB") ? "partial" : "unavailable",
                "Explicit USB providers requested at informational level; manifest event IDs/names include reset/failure activity when emitted. No device identity or transfer payload exported; not a complete USB protocol trace.");
        }
        // Generic provider metadata only. No arbitrary ETW payload or formatted message enters portable data.
        void ProviderEvent(TraceEvent e)
        {
            if (!providers.TryGetValue(e.ProviderGuid, out var provider)) return;
            string type = provider.Name + "/" + ((int)e.ID).ToString(CultureInfo.InvariantCulture) + "/" + (int)e.Opcode;
            string metadata = "event=" + e.EventName + ";level=" + e.Level;
            sink.Activity(e.TimeStampQPC, provider.Category, type, e.ProcessID >= 0 ? e.ProcessID : null,
                e.ThreadID >= 0 ? e.ThreadID : null, location: metadata);
        }
        _source.Dynamic.All += _ => { }; // Decode registered manifests when available; no payload is exported.
        _source.AllEvents += ProviderEvent; // One callback per event, including events with unknown schemas.
    }

    private void EnableProvider(string name, string category, Dictionary<Guid, (string Category, string Name)> providers)
    {
        try
        {
            Guid id = TraceEventProviders.GetProviderGuidByName(name);
            if (id == Guid.Empty) { _sink!.SetStatus(name, "unavailable", "Provider is not registered; channel not collected."); return; }
            _session.EnableProvider(id, TraceEventLevel.Informational, ulong.MaxValue);
            providers[id] = (category, name);
            _sink!.SetStatus(name, "partial", "Enable requested; event delivery depends on installed driver/provider and manifest. No events does not establish inactivity.");
        }
        catch (Exception ex) { _sink!.SetStatus(name, "unavailable", "Provider enable failed (" + ex.GetType().Name + ")."); }
    }
    private void Image(ImageLoadTraceData e, bool unload = false) => _sink!.Image(e.ProcessID, e.ImageBase, (ulong)Math.Max(0, e.ImageSize), e.FileName, unload, e.TimeStampQPC);
    private void Disk(DiskIOTraceData e, string type) => _sink!.Activity(e.TimeStampQPC, "Disk", type, ProcessForThread(e.ProcessID, e.ThreadID), e.ThreadID, e.ElapsedTimeMSec);
    private void File(FileIOReadWriteTraceData e, string type) => _sink!.Activity(e.TimeStampQPC, "File", type, ProcessForThread(e.ProcessID, e.ThreadID), e.ThreadID);
    private int ProcessForThread(int pid, int tid) => _sink!.ProcessForThread(pid, tid);
    private void TrackProcess(int pid, bool rundown = false)
    {
        if (_executable is null) { _sink!.ProcessStart(pid, true, rundown); return; }
        using var process = TryGetProcess(pid);
        bool selected = false;
        try { selected = process is not null && string.Equals(process.MainModule?.FileName, _executable, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        _sink!.ProcessStart(pid, selected, rundown);
    }
    private static Process? TryGetProcess(int pid)
    { try { return System.Diagnostics.Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }
    private void SeedProcesses()
    {
        int inaccessible = 0;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (_executable is not null && !string.Equals(process.MainModule?.FileName, _executable, StringComparison.OrdinalIgnoreCase)) continue;
                    _sink!.ProcessStart(process.Id, true);
                    foreach (ProcessThread thread in process.Threads)
                        using (thread) _sink.ThreadStart(process.Id, thread.Id);
                    // Module snapshots only support address range attribution; no function names are queried.
                    foreach (ProcessModule module in process.Modules)
                        _sink.Image(process.Id, (ulong)module.BaseAddress.ToInt64(), (ulong)module.ModuleMemorySize, module.ModuleName, validFromQpc: Stopwatch.GetTimestamp());
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { inaccessible++; }
            }
        }
        _sink!.SetStatus("Process/module inventory", "partial", "Exact executable path matching; process/thread/module rundown plus best-effort startup snapshot. Unreadable/exited processes: " + inaccessible + ". Missing/reused identities are not inferred.");
    }

    private static string PrivateRawFile(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
            throw new ArgumentException("Raw ETL requires a local fixed drive.", nameof(directory));
        Directory.CreateDirectory(root);
        for (DirectoryInfo? ancestor = new(root); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Raw ETL directory cannot traverse reparse points.", nameof(directory));
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        var child = new DirectoryInfo(Path.Combine(root, "deep-" + Guid.NewGuid().ToString("N")));
        child.Create(security);
        return Path.Combine(child.FullName, "capture.etl");
    }

    public long EventsLost
    {
        get
        {
            lock (_control)
            {
                if (_stopped) return Math.Max(_finalLoss, _source?.EventsLost ?? 0);
                var p = InvestigationDeepTraceNative.Query(_name);
                UpdateStatistics(p);
                return Math.Max(_finalLoss, _source?.EventsLost ?? 0);
            }
        }
    }
    public void Process() => _source!.Process();
    public void Stop()
    {
        lock (_control)
        {
            if (_stopped) return;
            var final = InvestigationDeepTraceNative.Stop(_name);
            UpdateStatistics(final);
            _stopped = true;
        }
    }
    private void UpdateStatistics(InvestigationDeepTraceNative.Properties p)
    {
        _finalLoss = Math.Max(_finalLoss, p.EventsLost);
        if (p.LogBuffersLost > _logBuffersLost || p.RealTimeBuffersLost > _realtimeBuffersLost) _sink?.InvalidateScheduling();
        _logBuffersLost = Math.Max(_logBuffersLost, p.LogBuffersLost); _realtimeBuffersLost = Math.Max(_realtimeBuffersLost, p.RealTimeBuffersLost);
        _sink?.SetStatus("ETW buffers", _logBuffersLost != 0 || _realtimeBuffersLost != 0 ? "incomplete" : "observed",
            $"Log buffers lost: {_logBuffersLost}; real-time buffers lost: {_realtimeBuffersLost}. Buffer counts are separate from event-loss counts.");
    }
    public void Dispose()
    {
        // Called after the consumer drain. This object never attaches to or stops a foreign session.
        _source?.Dispose();
        _session.Dispose();
    }
}

internal static class InvestigationDeepTraceNative
{
    // WNODE_HEADER is 48 bytes on both x86 and x64; LoggerThreadId in EVENT_TRACE_PROPERTIES is pointer-sized.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Wnode
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Properties
    {
        public Wnode Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong traceHandle, string instanceName, nint properties, uint controlCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Privilege { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct StackEvent { public Guid EventGuid; public byte Type; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref Privilege state, uint bufferLength, out Privilege previous, out uint returnLength);
    [DllImport("advapi32.dll", EntryPoint = "TraceSetInformation", ExactSpelling = true)]
    private static extern uint SetFlags(ulong handle, int informationClass, ref ulong flags, uint length);
    [DllImport("advapi32.dll", EntryPoint = "TraceSetInformation", ExactSpelling = true)]
    private static extern uint SetStack(ulong handle, int informationClass, ref StackEvent stack, uint length);

    internal static void EnableSampling(string name, KernelTraceEventParser.Keywords flags)
        => WithProfilePrivilege(() =>
        {
            ulong bits = (ulong)flags | (ulong)KernelTraceEventParser.Keywords.Profile;
            uint result = SetFlags(Query(name).Wnode.HistoricalContext, 4, ref bits, sizeof(ulong));
            if (result != 0) throw new Win32Exception((int)result);
        });
    private static void WithProfilePrivilege(Action action)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, 0x28, out var token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeSystemProfilePrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var enable = new Privilege { Count = 1, Luid = luid, Attributes = 2 };
            bool adjusted = AdjustTokenPrivileges(token, false, ref enable, (uint)Marshal.SizeOf<Privilege>(), out var previous, out _);
            int error = Marshal.GetLastPInvokeError();
            if (!adjusted || error == 1300) throw new Win32Exception(error);
            try
            {
                action();
            }
            finally
            {
                if (!AdjustTokenPrivileges(token, false, ref previous, (uint)Marshal.SizeOf<Privilege>(), out _, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }
    internal static void EnableSampleStacks(string name)
        => WithProfilePrivilege(() =>
        {
            // PerfInfo event class / Sample opcode, identical to TraceEvent's internal SetStackTraceIds(Profile).
            var stack = new StackEvent { EventGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc"), Type = 0x2e };
            uint result = SetStack(Query(name).Wnode.HistoricalContext, 3, ref stack, (uint)Marshal.SizeOf<StackEvent>());
            if (result != 0) throw new Win32Exception((int)result);
        });

    internal static Properties Query(string name) => Control(name, 0);
    internal static Properties Stop(string name) => Control(name, 1);
    internal static void EnableRealtime(string name)
    {
        var p = Query(name);
        // Preserve system EnableFlags; changing only LogFileMode enables simultaneous disk/realtime delivery.
        p.LogFileMode = 0x100; p.LogFileNameOffset = 0; p.LoggerNameOffset = 0; p.Wnode.Flags = 0x20000;
        Control(name, 2, p);
    }
    private static Properties Control(string name, uint code, Properties? update = null)
    {
        int size = Marshal.SizeOf<Properties>(), total = size + 4096;
        nint buffer = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.Copy(new byte[total], 0, buffer, total);
            var p = update ?? new Properties { LoggerNameOffset = (uint)size, LogFileNameOffset = (uint)(size + 2048) };
            p.Wnode.BufferSize = (uint)total;
            Marshal.StructureToPtr(p, buffer, false);
            uint error = ControlTraceW(0, name, buffer, code);
            if (code == 1 && error == 4201) return default; // Already absent is successful cleanup.
            if (error != 0 && !(code == 1 && error == 234)) throw new Win32Exception((int)error);
            return Marshal.PtrToStructure<Properties>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
