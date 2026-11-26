using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace JamesTools.Core.Diagnostics;

/// <summary>
/// Windows API references (reviewed 2026-11-16):
/// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification
/// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ns-cfgmgr32-cm_notify_filter
/// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ns-cfgmgr32-cm_notify_event_data
/// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ne-cfgmgr32-cm_notify_action
/// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/porting-from-setupapi-to-cfgmgr32
/// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw
/// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-driverversion
/// CM all-instance notifications supply enumerate/start/remove, not every property change. WMI
/// configuration notifications and Core Audio property notifications supplement them. None proves a reset.
/// </summary>
internal sealed class InvestigationWindowsDeviceSource : IInvestigationDeviceSource
{
    internal static readonly string[] UtilityNames = ["GameBar.exe", "GameBarFTServer.exe", "RTSS.exe", "RTSSHooksLoader64.exe", "Discord.exe", "steamwebhelper.exe", "NVIDIA Overlay.exe", "NVIDIA Share.exe", "obs64.exe", "Overwolf.exe", "iCUE.exe", "Razer Synapse.exe", "lghub.exe", "ArmouryCrate.exe", "MSIAfterburner.exe", "HWiNFO64.exe"];
    internal static readonly string[] DriverProviders = ["Microsoft-Windows-Kernel-PnP", "Microsoft-Windows-UserPnp", "Microsoft-Windows-DriverFrameworks-UserMode", "Microsoft-Windows-USB-USBHUB3", "Microsoft-Windows-USB-USBXHCI", "USBHUB3", "USBXHCI"];
    private readonly List<nint> registrations = [];
    private readonly List<EventLogWatcher> logWatchers = [];
    private readonly List<ManagementEventWatcher> wmiWatchers = [];
    private readonly Dictionary<string, string> interfaceMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Native.NotifyCallback cmCallback;
    private Action<InvestigationDeviceSignal> notify = _ => { };
    private Action<InvestigationCollectorStatus> status = _ => { };
    private AudioNotifications? audioCallback;
    private IDeviceEnumerator? audio;
    private bool audioRegistered;
    private int disposed;
    private GCHandle subscriptionRoot;
    private long callbackTicks, callbackFailures, callbackCount;
    private const int MaxDevices = 4096;

    public InvestigationWindowsDeviceSource() { cmCallback = CmNotify; }

    public void Start(Action<InvestigationDeviceSignal> notify, Action<InvestigationCollectorStatus> status)
    {
        this.notify = notify;
        this.status = status;
        if (!OperatingSystem.IsWindows()) { status(new("Windows devices", "unavailable", "Windows APIs required")); return; }
        // CM and IMMDeviceEnumerator do not own managed delegate/client lifetimes.
        subscriptionRoot = GCHandle.Alloc(this);
        RegisterCm(0, 1, "CM interfaces");
        RegisterCm(2, 2, "CM instances");
        TryStart("Core Audio", () =>
        {
            EnsureAudio();
            audioCallback = new AudioNotifications(this);
            Marshal.ThrowExceptionForHR(audio!.RegisterEndpointNotificationCallback(audioCallback));
            audioRegistered = true;
        });
        StartLogs();
        StartWmi("WMI configuration", "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 1", e =>
            Emit(new(InvestigationDeviceSignalKind.Configuration, "WMI configuration", "Configuration changed") { Utc = WmiUtc(e) }));
        var filter = string.Join(" OR ", UtilityNames.Select(x => "ProcessName = '" + x + "'"));
        foreach (var start in new[] { true, false })
        {
            var name = start ? "WMI process start" : "WMI process stop";
            // Extrinsic trace events: no WITHIN query and no process polling. WMI can deny these to
            // a standard account; report failure, without requesting elevation or falling back to ETW.
            // https://learn.microsoft.com/en-us/previous-versions/windows/desktop/krnlprov/win32-processstarttrace
            // https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-devicechangeevent
            StartWmi(name, "SELECT * FROM Win32_Process" + (start ? "Start" : "Stop") + "Trace WHERE " + filter, e =>
            {
                var utility = e["ProcessName"] as string;
                if (utility is null || !UtilityNames.Contains(utility, StringComparer.OrdinalIgnoreCase)) return;
                Emit(new(InvestigationDeviceSignalKind.Process, name, start ? "Utility started" : "Utility stopped")
                { Utc = WmiUtc(e), ProcessName = utility, ProcessId = checked((int)Convert.ToUInt32(e["ProcessID"])) });
            });
        }
    }

    private void RegisterCm(uint type, uint flags, string name)
    {
        TryStart(name, () =>
        {
            var filter = new Native.NotifyFilter { Size = 416, FilterType = type, Flags = flags };
            var result = Native.CM_Register_Notification(ref filter, 0, cmCallback, out var registration);
            if (result != 0) throw new Win32Exception(unchecked((int)result));
            registrations.Add(registration);
        });
    }

    private void TryStart(string name, Action action)
    {
        try { action(); status(new(name, "active", "Event-driven subscription; upstream delivery latency/loss unknown")); }
        catch (Exception ex) { status(new(name, "unavailable", "Subscription failed: " + ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + ")")); }
    }

    private uint CmNotify(nint handle, nint context, uint action, nint data, uint length)
    {
        Callback(() =>
        {
            if (data == 0 || length < 10) return;
            var type = (uint)Marshal.ReadInt32(data);
            var offset = type == 0 ? 24 : 8;
            if (type is not (0 or 2) || length <= offset) return;
            var chars = (int)Math.Min((length - (uint)offset) / 2, 4096);
            var key = Marshal.PtrToStringUni(data + offset, chars)?.Split('\0')[0];
            if (string.IsNullOrEmpty(key)) return;
            var eventType = action switch { 0 => "Interface arrived", 1 => "Interface removed", 7 => "Device enumerated", 8 => "Device started or reconfigured", 9 => "Device removed", _ => null };
            if (eventType is null) return;
            Emit(new(type == 0 ? InvestigationDeviceSignalKind.Interface : InvestigationDeviceSignalKind.Device,
                type == 0 ? "CM interfaces" : "CM instances", eventType) { Key = key, Removed = action is 1 or 9 });
        });
        return 0;
    }

    private void Callback(Action action)
    {
        var begin = Stopwatch.GetTimestamp();
        try { if (Volatile.Read(ref disposed) == 0) action(); }
        catch { Interlocked.Increment(ref callbackFailures); }
        finally
        {
            Interlocked.Add(ref callbackTicks, Stopwatch.GetTimestamp() - begin);
            Interlocked.Increment(ref callbackCount);
        }
    }
    // Read on the consumer/UI thread. Audio callbacks must never wait for inventory/status locks.
    public InvestigationCollectorStatus CallbackStatus => new("Native callbacks", Interlocked.Read(ref callbackFailures) > 0 ? "partial" : "observed",
        "Callback count: " + Interlocked.Read(ref callbackCount) + "; callback decode failures: " + Interlocked.Read(ref callbackFailures) + "; unknown upstream loss cannot be measured")
        { EventsDropped = Interlocked.Read(ref callbackFailures), CollectionMilliseconds = Interlocked.Read(ref callbackTicks) * 1000d / Stopwatch.Frequency };
    private void Emit(InvestigationDeviceSignal signal) { if (Volatile.Read(ref disposed) == 0) notify(signal); }

    private void StartWmi(string name, string query, Action<ManagementBaseObject> received)
    {
        ManagementEventWatcher? watcher = null;
        var started = 0;
        try
        {
            watcher = new ManagementEventWatcher(new ManagementScope(@"\\.\root\CIMV2"), new WqlEventQuery(query));
            watcher.EventArrived += (_, e) => Callback(() => { using var item = e.NewEvent; received(item); });
            watcher.Stopped += (_, e) =>
            {
                if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref started) != 0) status(new(name, "stopped", "WMI subscription stopped; upstream loss unknown; status " + e.Status));
            };
            watcher.Start();
            Volatile.Write(ref started, 1);
            wmiWatchers.Add(watcher);
            status(new(name, "active", "WMI extrinsic event subscription; provider UTC, clock skew and upstream loss unknown" + (name.Contains("process") ? "; utility allowlist only" : "")));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref started, 0);
            watcher?.Dispose();
            status(new(name, "unavailable", "Subscription failed: " + ex.GetType().Name + (ex is ManagementException management ? " (" + management.ErrorCode + ")" : " (0x" + ex.HResult.ToString("X8") + ")")));
        }
    }

    private static DateTimeOffset? WmiUtc(ManagementBaseObject item)
    {
        try { return DateTimeOffset.FromFileTime(checked((long)Convert.ToUInt64(item["TIME_CREATED"]))); }
        catch { return null; }
    }

    private void StartLogs()
    {
        // Subscribe only to existing enabled channels. Never enable analytic/debug logs. Future
        // records only, so the session cannot mistakenly attribute an old boot error to this capture.
        // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogwatcher
        // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventrecordwritteneventargs.eventexception
        string[] channels = ["System", "Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-DriverFrameworks-UserMode/Operational", "Microsoft-Windows-USB-USBHUB3/Operational", "Microsoft-Windows-USB-USBXHCI/Operational", "Microsoft-Windows-UserPnp/DeviceInstall"];
        foreach (var channel in channels)
        {
            EventLogWatcher? watcher = null;
            var sourceName = "EventLog " + channel;
            try
            {
                using var config = new EventLogConfiguration(channel);
                if (!config.IsEnabled) { status(new(sourceName, "unavailable", "Channel disabled; not enabled by collector")); continue; }
                var query = channel == "System"
                    ? "*[System[(Level=1 or Level=2 or Level=3) and (Provider[@Name='Microsoft-Windows-Kernel-PnP'] or Provider[@Name='Microsoft-Windows-UserPnp'] or Provider[@Name='Microsoft-Windows-DriverFrameworks-UserMode'] or Provider[@Name='Microsoft-Windows-USB-USBHUB3'] or Provider[@Name='Microsoft-Windows-USB-USBXHCI'] or Provider[@Name='USBHUB3'] or Provider[@Name='USBXHCI'])]]"
                    : "*[System[(Level=1 or Level=2 or Level=3)]]";
                watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, query), null, false);
                watcher.EventRecordWritten += (_, e) => Callback(() =>
                {
                    using var record = e.EventRecord;
                    if (e.EventException is { } exception)
                    {
                        status(new(sourceName, "degraded", "Subscription delivery error: " + exception.GetType().Name + "; number of lost records unknown"));
                        return;
                    }
                    if (record is null) return;
                    // Deliberately omit message/XML/properties: they may contain names, paths, raw
                    // instance IDs or serials. Provider event ID and local record number remain useful.
                    Emit(new(InvestigationDeviceSignalKind.Log, sourceName, "Driver or PnP warning/error")
                    { Utc = record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime()) : null, EventId = record.Id, RecordId = record.RecordId, Provider = record.ProviderName });
                });
                watcher.Enabled = true;
                logWatchers.Add(watcher);
                status(new(sourceName, "active", "Enabled channel warning/error subscription; no messages/paths; identity unavailable; provider UTC and unknown delivery latency/loss"));
            }
            catch (Exception ex)
            {
                watcher?.Dispose();
                status(new(sourceName, "unavailable", "Channel unavailable or access denied: " + ex.GetType().Name));
            }
        }
    }

    public string? ResolveKey(string key, bool deviceInterface)
    {
        if (!deviceInterface) return key;
        if (interfaceMap.TryGetValue(key, out var known)) return known;
        var property = Native.InstanceId;
        var bytes = new byte[8192];
        uint size = (uint)bytes.Length;
        if (Native.CM_Get_Device_Interface_PropertyW(key, ref property, out var type, bytes, ref size, 0) != 0 || type != 18) return null;
        var id = Decode(bytes, size);
        if (string.IsNullOrEmpty(id)) return null;
        if (interfaceMap.Count < MaxDevices) interfaceMap[key] = id;
        return id;
    }

    public IReadOnlyList<InvestigationRawDevice> ReadInventory()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var list = new List<InvestigationRawDevice>();
        var set = Native.SetupDiGetClassDevsW(0, null, 0, 2 | 4); // PRESENT | ALLCLASSES
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; index <= MaxDevices; index++)
            {
                var info = Native.DevInfo.Create();
                if (!Native.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) throw new Win32Exception(error); // NO_MORE_ITEMS
                    break;
                }
                if (index == MaxDevices) { status(new("Inventory capacity", "partial", "Inventory capped at 4096 devnodes") { EventsDropped = 1 }); break; }
                var item = ReadNode(set, ref info);
                if (item is not null) list.Add(item);
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        try
        {
            EnsureAudio();
            Marshal.ThrowExceptionForHR(audio!.EnumAudioEndpoints(2, 15, out var endpoints));
            try
            {
                Marshal.ThrowExceptionForHR(endpoints.GetCount(out var count));
                for (uint i = 0; i < Math.Min(count, 512); i++)
                {
                    Marshal.ThrowExceptionForHR(endpoints.Item(i, out var endpoint));
                    try { list.Add(ReadAudio(endpoint)); }
                    finally { Marshal.ReleaseComObject(endpoint); }
                }
                if (count > 512) status(new("Audio inventory", "partial", "Audio inventory capped at 512 endpoints") { EventsDropped = count - 512 });
                else status(new("Audio inventory", "available", "Endpoint inventory read successfully; opaque endpoint IDs pseudonymized; PnP association only where exposed"));
            }
            finally { Marshal.ReleaseComObject(endpoints); }
        }
        catch (Exception ex) { status(new("Audio inventory", "unavailable", "Endpoint enumeration failed: " + ex.GetType().Name)); }
        return list;
    }

    public InvestigationRawDevice? ReadDevice(string key)
    {
        if (key.StartsWith("audio:", StringComparison.OrdinalIgnoreCase))
        {
            EnsureAudio();
            if (audio!.GetDevice(key[6..], out var endpoint) < 0) return null;
            try { return ReadAudio(endpoint); }
            finally { Marshal.ReleaseComObject(endpoint); }
        }
        var set = Native.SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) return null;
        try
        {
            var info = Native.DevInfo.Create();
            return Native.SetupDiOpenDeviceInfoW(set, key, 0, 0, ref info) ? ReadNode(set, ref info) : null;
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static InvestigationRawDevice? ReadNode(nint set, ref Native.DevInfo info)
    {
        var key = NodeId(info.DevInst);
        if (key is null) return null;
        string? parent = null, hub = null, controller = null;
        var node = info.DevInst;
        var visited = new HashSet<uint> { node };
        for (var i = 0; i < 32 && Native.CM_Get_Parent(out var next, node, 0) == 0 && visited.Add(next); i++)
        {
            var ancestor = NodeId(next);
            parent ??= ancestor;
            var service = NodeRegistry(next, 5); // CM_DRP_SERVICE
            if (service is not null)
            {
                if (service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase) || service.Equals("USBHUB", StringComparison.OrdinalIgnoreCase)) hub ??= ancestor;
                if (new[] { "USBXHCI", "USBEHCI", "USBOHCI", "USBUHCI" }.Contains(service, StringComparer.OrdinalIgnoreCase)) controller ??= ancestor;
            }
            node = next;
        }
        var powerData = RegistryBytes(set, ref info, 30); // SPDRP_DEVICE_POWER_DATA / CM_POWER_DATA
        string? power = null;
        if (powerData.Length >= 12)
        {
            var state = BitConverter.ToUInt32(powerData, 4);
            var capabilities = BitConverter.ToUInt32(powerData, 8);
            power = "CM power: most recently reported D" + (state is >= 1 and <= 4 ? (state - 1).ToString() : "?") + "; capabilities=0x" + capabilities.ToString("X8") + "; policy unavailable";
        }
        return new(key)
        {
            // User-editable SPDRP_FRIENDLYNAME intentionally not read.
            Name = RegistryString(set, ref info, 0), Class = RegistryString(set, ref info, 7), Manufacturer = RegistryString(set, ref info, 11),
            DriverName = NodeProperty(info.DevInst, Native.DriverInf), DriverVersion = NodeProperty(info.DevInst, Native.DriverVersion),
            Parent = parent, Hub = hub, Controller = controller, Topology = RegistryString(set, ref info, 35), Power = power
        };
    }

    private static string? NodeId(uint node)
    {
        var value = new StringBuilder(4096);
        return Native.CM_Get_Device_IDW(node, value, value.Capacity, 0) == 0 ? value.ToString() : null;
    }
    private static string? NodeRegistry(uint node, uint property)
    {
        var data = new byte[8192]; uint size = (uint)data.Length;
        return Native.CM_Get_DevNode_Registry_PropertyW(node, property, out _, data, ref size, 0) == 0 ? Decode(data, size) : null;
    }
    private static string? NodeProperty(uint node, Native.PropertyKey property)
    {
        var data = new byte[8192]; uint size = (uint)data.Length;
        return Native.CM_Get_DevNode_PropertyW(node, ref property, out var type, data, ref size, 0) == 0 && type == 18 ? Decode(data, size) : null;
    }
    private static byte[] RegistryBytes(nint set, ref Native.DevInfo info, uint property)
    {
        var data = new byte[8192];
        return Native.SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, data, (uint)data.Length, out var size)
            ? data[..(int)Math.Min(size, (uint)data.Length)] : [];
    }
    private static string? RegistryString(nint set, ref Native.DevInfo info, uint property)
    {
        var bytes = RegistryBytes(set, ref info, property);
        return bytes.Length > 0 ? Decode(bytes, (uint)bytes.Length) : null;
    }
    private static string Decode(byte[] bytes, uint size) => Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(size, (uint)bytes.Length) / 2 * 2).Split('\0')[0];

    private void EnsureAudio()
    {
        if (audio is not null) return;
        audio = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
    }

    private InvestigationRawDevice ReadAudio(IAudioDevice endpoint)
    {
        // Opaque endpoint ID is never parsed. PKEY_Device_InstanceId, when available, provides the
        // link to the PnP endpoint devnode and its ancestors; absence is represented explicitly.
        // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties
        Marshal.ThrowExceptionForHR(endpoint.GetId(out var id));
        Marshal.ThrowExceptionForHR(endpoint.GetState(out var state));
        string? instance = null;
        if (endpoint.OpenPropertyStore(0, out var store) >= 0)
        {
            try
            {
                var property = Native.InstanceId;
                if (store.GetValue(ref property, out var value) >= 0)
                {
                    try { if (value.Type == 31 && value.Pointer != 0) instance = Marshal.PtrToStringUni(value.Pointer); }
                    finally { Native.PropVariantClear(ref value); }
                }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        var node = instance is not null && !instance.StartsWith("audio:", StringComparison.OrdinalIgnoreCase) ? ReadDevice(instance) : null;
        return new("audio:" + id)
        {
            Class = "AudioEndpoint", Parent = node?.Key, Hub = node?.Hub, Controller = node?.Controller,
            Topology = node?.Topology, Power = node?.Power, DriverName = node?.DriverName, DriverVersion = node?.DriverVersion,
            State = state switch { 1 => "active", 2 => "disabled", 4 => "not-present", 8 => "unplugged", _ => "unknown" }
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Unregister outside callbacks: CM unregister waits for in-flight callbacks; Core Audio
        // forbids unregistering/releasing the last endpoint reference inside its notification.
        // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_unregister_notification
        // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
        var unregistered = true;
        foreach (var registration in registrations)
        {
            var result = Native.CM_Unregister_Notification(registration);
            if (result != 0)
            {
                unregistered = false;
                status(new("CM cleanup", "degraded", "Unregister returned CONFIGRET " + result + "; callback retained safely until process exit"));
            }
        }
        registrations.Clear();
        foreach (var watcher in logWatchers) { try { watcher.Dispose(); } catch { status(new("EventLog cleanup", "degraded", "Watcher disposal failed")); } }
        logWatchers.Clear();
        foreach (var watcher in wmiWatchers)
        {
            try { watcher.Stop(); watcher.Dispose(); }
            catch { status(new("WMI cleanup", "degraded", "Watcher stop/disposal failed")); }
        }
        wmiWatchers.Clear();
        if (audio is not null)
        {
            var audioUnregistered = true;
            try
            {
                if (audioRegistered && audioCallback is not null) Marshal.ThrowExceptionForHR(audio.UnregisterEndpointNotificationCallback(audioCallback));
            }
            catch
            {
                audioUnregistered = false;
                unregistered = false;
                status(new("Audio cleanup", "degraded", "Unregister failed; callback retained safely until process exit"));
            }
            if (audioUnregistered) { Marshal.ReleaseComObject(audio); audio = null; audioCallback = null; audioRegistered = false; }
        }
        if (unregistered && subscriptionRoot.IsAllocated) subscriptionRoot.Free();
        GC.KeepAlive(cmCallback);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioNotifications(InvestigationWindowsDeviceSource owner) : IAudioNotifications
    {
        private int Send(string type, string? id, bool removed = false, uint? state = null, int? flow = null, int? role = null)
        {
            owner.Callback(() => owner.Emit(new(InvestigationDeviceSignalKind.Audio, "Core Audio", type)
            { Key = id is null ? null : "audio:" + id, Removed = removed, AudioState = state, AudioFlow = flow, AudioRole = role }));
            return 0;
        }
        public int OnDeviceStateChanged(string id, uint state) => Send("Audio endpoint state changed", id, state: state);
        public int OnDeviceAdded(string id) => Send("Audio endpoint added", id);
        public int OnDeviceRemoved(string id) => Send("Audio endpoint removed", id, true);
        public int OnDefaultDeviceChanged(int flow, int role, string? id) => Send("Default audio endpoint changed", id, flow: flow, role: role);
        public int OnPropertyValueChanged(string id, Native.PropertyKey key) => Send("Audio endpoint properties changed", id);
    }

    // COM vtable order and IIDs checked against Microsoft's SDK headers, including unused slots:
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/mmdeviceapi.h
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IAudioCollection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IAudioDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IAudioDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IAudioNotifications client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IAudioNotifications client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IAudioDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, out nint result);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
    private interface IAudioNotifications
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, Native.PropertyKey key);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out Native.PropertyKey key);
        [PreserveSig] int GetValue(ref Native.PropertyKey key, out Native.PropVariant value);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey(Guid id, uint pid) { public Guid Id = id; public uint Pid = pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }
        internal static PropertyKey InstanceId => new(new("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);
        internal static PropertyKey DriverVersion => new(new("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 3);
        internal static PropertyKey DriverInf => new(new("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 5);
        [StructLayout(LayoutKind.Explicit, Size = 416)] internal struct NotifyFilter
        {
            [FieldOffset(0)] public uint Size;
            [FieldOffset(4)] public uint Flags;
            [FieldOffset(8)] public uint FilterType;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DevInfo
        {
            public uint Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved;
            public static DevInfo Create() => new() { Size = (uint)Marshal.SizeOf<DevInfo>() };
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint NotifyCallback(nint handle, nint context, uint action, nint data, uint length);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Register_Notification(ref NotifyFilter filter, nint context, NotifyCallback callback, out nint registration);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Unregister_Notification(nint registration);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey property, out uint type, byte[] data, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] data, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_Interface_PropertyW(string path, ref PropertyKey property, out uint type, byte[] data, ref uint size, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwnd);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DevInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiOpenDeviceInfoW(nint set, string id, nint hwnd, uint flags, ref DevInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DevInfo info, uint property, out uint type, byte[] data, uint size, out uint required);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
        [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropVariant value);
    }
}
