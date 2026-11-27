using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace JamesTools.Core.Diagnostics;

/// <summary>
/// Normal, unprivileged, event-driven monitoring. No kernel session, HWND, log enabling or timer.
/// Native callbacks only copy bounded data into a bounded queue; inventory reads and publishing run
/// on one worker. Publish must return promptly. Dispose awaits that worker and unregisters sources.
/// Receipt timestamps do not identify hardware reset time. Missing notifications cannot be counted
/// by CM/Core Audio; Status explicitly reports this delivery uncertainty.
/// </summary>
public sealed class InvestigationDeviceCollector : IAsyncDisposable
{
    private readonly InvestigationClock clock;
    private readonly Action<InvestigationEvent> publish;
    private readonly IInvestigationDeviceSource source;
    private readonly InvestigationDevicePrivacy privacy;
    private readonly Channel<InvestigationDeviceSignal> queue;
    private readonly object gate = new();
    private readonly Dictionary<string, InvestigationDevice> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InvestigationCollectorStatus> statuses = new(StringComparer.Ordinal);
    private readonly Task worker;
    private int stopping;
    private long dropped, callbackTicks, callbackCount, publishFailures;
    private const int InventoryLimit = 4096;

    public InvestigationDeviceCollector(InvestigationClock clock, Action<InvestigationEvent> publish)
        : this(clock, publish, new InvestigationWindowsDeviceSource(), InvestigationDevicePrivacy.Local()) { }

    internal InvestigationDeviceCollector(InvestigationClock clock, Action<InvestigationEvent> publish,
        IInvestigationDeviceSource source, InvestigationDevicePrivacy privacy, int capacity = 512)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.source = source;
        this.privacy = privacy;
        queue = Channel.CreateBounded<InvestigationDeviceSignal>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = Task.Run(() => Work(ready));
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Immutable copies, including removed devices, capped at 4096 identities.</summary>
    public IReadOnlyList<InvestigationDevice> Inventory
    { get { lock (gate) return Array.AsReadOnly(devices.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray()); } }

    public IReadOnlyList<InvestigationCollectorStatus> Status
    {
        get
        {
            lock (gate)
            {
                return Array.AsReadOnly(statuses.Values.Concat(source.CallbackStatus is { } native ? [native] : Array.Empty<InvestigationCollectorStatus>()).Concat([
                    new InvestigationCollectorStatus("Device delivery", Interlocked.Read(ref dropped) > 0 ? "partial" : Volatile.Read(ref stopping) == 0 ? "active" : "stopped",
                        "Queue capacity is bounded; unknown upstream loss is not zero. CM/audio timestamps are callback receipt only; UTC provider clocks may skew. Callback count: " + Interlocked.Read(ref callbackCount) + ". Publisher failures: " + Interlocked.Read(ref publishFailures) + ". " + privacy.Persistence)
                    {
                        EventsDropped = Interlocked.Read(ref dropped),
                        CollectionMilliseconds = Interlocked.Read(ref callbackTicks) * 1000d / Stopwatch.Frequency
                    }
                ]).ToArray());
            }
        }
    }

    /// <summary>One-shot inventory for MCP; no subscriptions. IDs share the collector's local salt.</summary>
    public static IReadOnlyList<InvestigationDevice> ReadInventory()
    {
        // Core Audio is initialized on MTA even when called from a WPF STA thread.
        return Task.Run(() =>
        {
            using var source = new InvestigationWindowsDeviceSource();
            var privacy = InvestigationDevicePrivacy.Local();
            return (IReadOnlyList<InvestigationDevice>)Array.AsReadOnly(source.ReadInventory().Take(InventoryLimit).Select(privacy.Redact).ToArray());
        }).GetAwaiter().GetResult();
    }

    private void Accept(InvestigationDeviceSignal signal)
    {
        var begin = Stopwatch.GetTimestamp();
        try
        {
            if (Volatile.Read(ref stopping) != 0) return;
            signal = signal with { Receipt = clock.NowSeconds };
            if (!queue.Writer.TryWrite(signal)) Interlocked.Increment(ref dropped);
        }
        finally
        {
            Interlocked.Increment(ref callbackCount);
            Interlocked.Add(ref callbackTicks, Stopwatch.GetTimestamp() - begin);
        }
    }

    private void SetStatus(InvestigationCollectorStatus status)
    { lock (gate) statuses[status.Source] = status; }

    private async Task Work(TaskCompletionSource ready)
    {
        try
        {
            try
            {
                source.Start(Accept, SetStatus);
                foreach (var item in source.ReadInventory().Take(InventoryLimit + 1))
                {
                    if (devices.Count == InventoryLimit) { Interlocked.Increment(ref dropped); break; }
                    devices[item.Key] = privacy.Redact(item);
                }
                SetStatus(new("Device inventory", "available", "Session-start snapshot; subscription overlaps inventory. Duplicate arrival observations are possible. Names are generic class labels because friendly names can contain personal identifiers."));
            }
            catch (Exception ex) { SetStatus(new("Device inventory", "unavailable", "Startup failed: " + ex.GetType().Name)); }
            finally { ready.TrySetResult(); }

            await foreach (var signal in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref stopping) != 0) { Interlocked.Increment(ref dropped); continue; }
                try { Process(signal); }
                catch (Exception ex) { SetStatus(new("Device worker", "degraded", "Observation failed: " + ex.GetType().Name)); Interlocked.Increment(ref dropped); }
            }
        }
        finally
        {
            ready.TrySetResult();
            try { source.Dispose(); }
            catch (Exception ex) { SetStatus(new("Device cleanup", "degraded", "Unregister failed: " + ex.GetType().Name)); }
        }
    }

    private void Process(InvestigationDeviceSignal s)
    {
        if (s.Barrier is { } barrier) { barrier.TrySetResult(); return; }
        var timestamp = s.Utc is { } utc ? clock.FromUtc(utc) : s.Receipt;
        var details = new Dictionary<string, string>
        {
            ["delivery"] = s.Utc is null ? "Callback receipt; actual occurrence time and latency unavailable" : "Provider UTC; clock alignment and delivery latency uncertain",
            ["resetAttribution"] = "A notification does not establish a hardware reset or a cause of stutter"
        };
        string? id = null;
        var stale = false;
        if (s.Key is { Length: > 0 } key)
        {
            // Resolve an opaque interface path on the worker; never parse the path as an instance ID.
            var resolved = source.ResolveKey(key, s.Kind == InvestigationDeviceSignalKind.Interface);
            key = resolved ?? key;
            id = privacy.Id(key);
            lock (gate)
            {
                stale = latest.TryGetValue(key, out var last) && timestamp < last;
                if (!stale && (latest.ContainsKey(key) || latest.Count < InventoryLimit)) latest[key] = timestamp;
            }
            if (!stale && s.Kind is InvestigationDeviceSignalKind.Device or InvestigationDeviceSignalKind.Interface or InvestigationDeviceSignalKind.Audio)
            {
                // Interface disablement does not prove the devnode was physically removed.
                var interfaceRemoval = s.Removed && s.Kind == InvestigationDeviceSignalKind.Interface;
                var raw = s.Removed && !interfaceRemoval ? null : source.ReadDevice(key);
                lock (gate)
                {
                    if (devices.Count < InventoryLimit || devices.ContainsKey(key))
                    {
                        devices[key] = raw is not null ? privacy.Redact(raw) : devices.TryGetValue(key, out var old)
                            ? old with { ConnectionState = s.Removed && !interfaceRemoval ? "removed" : "unknown" }
                            : privacy.Redact(new(key) { Class = s.Kind == InvestigationDeviceSignalKind.Audio ? "AudioEndpoint" : null, State = s.Removed && !interfaceRemoval ? "removed" : "unknown" });
                    }
                    else Interlocked.Increment(ref dropped);
                }
            }
            if (resolved is null && s.Kind == InvestigationDeviceSignalKind.Interface)
                details["identity"] = "Interface could not be resolved; separate pseudonym retained (common after removal)";
            if (s.Removed && s.Kind == InvestigationDeviceSignalKind.Interface)
                details["scope"] = "Interface disabled; physical device removal is not established";
        }
        if (s.Kind == InvestigationDeviceSignalKind.Configuration)
        {
            // Configuration notifications have no target identity. Refresh at most once per second of
            // receipt time; this is event-triggered coalescing, never periodic device polling.
            if (s.Receipt - lastRefresh >= 1)
            {
                lastRefresh = s.Receipt;
                foreach (var raw in source.ReadInventory().Take(InventoryLimit))
                {
                    lock (gate)
                        if (devices.Count < InventoryLimit || devices.ContainsKey(raw.Key)) devices[raw.Key] = privacy.Redact(raw);
                }
            }
            else details["inventory"] = "Configuration inventory refresh coalesced";
        }
        if (s.AudioFlow is { } flow) details["flow"] = flow == 0 ? "render" : flow == 1 ? "capture" : "all";
        if (s.AudioRole is { } role) details["role"] = role == 0 ? "console" : role == 1 ? "multimedia" : "communications";
        if (s.AudioState is { } state) details["endpointState"] = state.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.EventId is { } eventId) details["eventId"] = eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.RecordId is { } recordId) details["recordId"] = recordId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Kind == InvestigationDeviceSignalKind.Log) details["identity"] = "Device identity unavailable; event messages and payloads omitted for privacy";
        if (s.Provider is { } provider && InvestigationWindowsDeviceSource.DriverProviders.Contains(provider, StringComparer.Ordinal)) details["provider"] = provider;
        if (s.ProcessName is { } name && InvestigationWindowsDeviceSource.UtilityNames.Contains(name, StringComparer.OrdinalIgnoreCase)) details["utility"] = name.ToLowerInvariant();
        var type = s.EventType;
        // EventType/Source come exclusively from internal fixed vocabularies, never event messages.
        var ev = new InvestigationEvent
        {
            TimestampSeconds = timestamp, ReceiptSeconds = s.Receipt, Source = s.Source,
            Category = s.Kind == InvestigationDeviceSignalKind.Audio ? "audio" : s.Kind == InvestigationDeviceSignalKind.Process ? "process" : s.Kind == InvestigationDeviceSignalKind.Log ? "driver" : "device",
            EventType = type, DeviceId = id, ProcessId = s.ProcessId,
            TimestampBasis = s.Utc is null ? "QPC callback receipt (occurrence unavailable)" : "Provider UTC mapped to session clock",
            UncertaintyMs = null, Quality = stale ? "out-of-order delivery; inventory unchanged" : "delivery latency unknown",
            Severity = s.Kind == InvestigationDeviceSignalKind.Log ? "warning" : "information",
            Summary = type + "; observation only; occurrence/reset timing uncertain",
            Details = new ReadOnlyDictionary<string, string>(details)
        };
        try { publish(ev); }
        catch { Interlocked.Increment(ref publishFailures); Interlocked.Increment(ref dropped); }
    }
    private double lastRefresh = double.NegativeInfinity;

    internal Task FlushAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new(InvestigationDeviceSignalKind.Barrier, "", "") { Barrier = ready }))
            throw new InvalidOperationException("Cannot flush a full or closed queue.");
        return ready.Task;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0) queue.Writer.TryComplete();
        return new ValueTask(worker);
    }
}

internal enum InvestigationDeviceSignalKind { Device, Interface, Audio, Configuration, Process, Log, Barrier }
internal sealed record InvestigationDeviceSignal(InvestigationDeviceSignalKind Kind, string Source, string EventType)
{
    public string? Key { get; init; }
    public bool Removed { get; init; }
    public DateTimeOffset? Utc { get; init; }
    public double Receipt { get; init; }
    public int? AudioFlow { get; init; }
    public int? AudioRole { get; init; }
    public uint? AudioState { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public int? EventId { get; init; }
    public long? RecordId { get; init; }
    public string? Provider { get; init; }
    public TaskCompletionSource? Barrier { get; init; }
}

internal sealed record InvestigationRawDevice(string Key)
{
    public string? Name { get; init; }
    public string? Class { get; init; }
    public string? Manufacturer { get; init; }
    public string? DriverName { get; init; }
    public string? DriverVersion { get; init; }
    public string State { get; init; } = "present";
    public string? Parent { get; init; }
    public string? Hub { get; init; }
    public string? Controller { get; init; }
    public string? Topology { get; init; }
    public string? Power { get; init; }
}

internal interface IInvestigationDeviceSource : IDisposable
{
    InvestigationCollectorStatus? CallbackStatus => null;
    void Start(Action<InvestigationDeviceSignal> notify, Action<InvestigationCollectorStatus> status);
    IReadOnlyList<InvestigationRawDevice> ReadInventory();
    InvestigationRawDevice? ReadDevice(string key);
    string? ResolveKey(string key, bool deviceInterface);
}

/// <summary>HMAC salt is local to the Windows account; raw IDs never enter portable evidence.
/// User-editable friendly names, endpoint labels, event messages, command lines and paths are omitted.
/// Only fixed class/vendor labels, numeric versions/VID/PID and validated bus paths leave this boundary.
/// </summary>
internal sealed class InvestigationDevicePrivacy(byte[] salt, string persistence = "Persistent local identity salt")
{
    private static readonly Lazy<InvestigationDevicePrivacy> local = new(CreateLocal, true);
    public string Persistence { get; } = persistence;
    public static InvestigationDevicePrivacy Local() => local.Value;
    private static InvestigationDevicePrivacy CreateLocal()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamesTools", "Investigation");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "device-identity.salt");
            // FileShare.None + CreateNew prevents concurrent processes from replacing a valid salt.
            if (!File.Exists(path))
            {
                try
                {
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(RandomNumberGenerator.GetBytes(32));
                    output.Flush(true);
                }
                catch (IOException) when (File.Exists(path)) { }
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != 32) throw new IOException("Invalid identity salt");
            return new(bytes);
        }
        catch { return new(RandomNumberGenerator.GetBytes(32), "Salt persistence unavailable; identities stable only in this process"); }
    }
    public string Id(string key) => "device-" + Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).ToLowerInvariant()[..24];
    private static string? Match(string? value, string pattern) => value is not null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant) ? value : null;
    private static readonly string[] classes = ["USB", "USBDevice", "HIDClass", "Keyboard", "Mouse", "MEDIA", "AudioEndpoint", "Display", "Net", "Bluetooth", "DiskDrive", "SCSIAdapter", "System", "SoftwareDevice", "Monitor", "Processor", "Computer", "Battery", "Ports", "Volume", "WPD", "Camera", "Image", "Printer", "PrintQueue", "SecurityDevices", "Sensor", "Extension", "Firmware"];
    private static readonly string[] vendors = ["Microsoft", "Intel", "AMD", "NVIDIA", "Realtek", "Logitech", "Razer", "Corsair", "SteelSeries", "ASMedia", "Generic", "Samsung", "Western Digital", "Kingston"];
    private static readonly string[] descriptions = ["USB Composite Device", "USB Input Device", "USB Root Hub (USB 3.0)", "Generic USB Hub", "Generic SuperSpeed USB Hub", "HID-compliant mouse", "HID Keyboard Device", "HID-compliant consumer control device", "High Definition Audio Device", "USB Audio Device", "Speakers", "Headphones", "Microphone", "Standard NVM Express Controller", "Generic PnP Monitor"];
    private static readonly string[] driverFiles = ["usb.inf", "usbccgp.inf", "usbhub3.inf", "hidusb.inf", "input.inf", "keyboard.inf", "msmouse.inf", "monitor.inf", "audioendpoint.inf", "wdma_usb.inf", "bth.inf", "machine.inf", "volume.inf", "disk.inf", "stornvme.inf", "battery.inf", "cpu.inf"];
    public InvestigationDevice Redact(InvestigationRawDevice raw)
    {
        var cls = classes.FirstOrDefault(x => string.Equals(x, raw.Class, StringComparison.OrdinalIgnoreCase)) ?? "Device";
        var vid = Regex.Match(raw.Key, @"(?:^|[\\&])VID_([0-9A-Fa-f]{4})(?=[&\\]|$)", RegexOptions.IgnoreCase);
        var pid = Regex.Match(raw.Key, @"(?:^|[\\&])PID_([0-9A-Fa-f]{4})(?=[&\\]|$)", RegexOptions.IgnoreCase);
        var vendor = raw.Manufacturer switch { "Intel Corporation" or "Intel(R) Corporation" => "Intel", "Advanced Micro Devices, Inc." or "Advanced Micro Devices" => "AMD", "NVIDIA Corporation" => "NVIDIA", "Logitech Inc." => "Logitech", "Realtek Semiconductor Corp." => "Realtek", _ => raw.Manufacturer };
        var manufacturer = vendors.FirstOrDefault(x => string.Equals(x, vendor, StringComparison.OrdinalIgnoreCase));
        var driver = Match(raw.DriverName, @"\A[A-Za-z0-9_-]{1,64}\.inf\z");
        if (driver is not null && !Regex.IsMatch(driver, @"\Aoem[0-9]{1,8}\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !driverFiles.Contains(driver, StringComparer.OrdinalIgnoreCase))
            driver = "driver-" + Id("driver:" + driver)[7..];
        return new()
        {
            Id = Id(raw.Key), Class = cls, Name = descriptions.FirstOrDefault(x => string.Equals(x, raw.Name, StringComparison.OrdinalIgnoreCase)) ?? (manufacturer is null ? "" : manufacturer + " ") + cls + " device",
            LocalName = raw.Name,
            Manufacturer = manufacturer ?? "Unavailable (unverified vendor label omitted)",
            Vid = vid.Success ? vid.Groups[1].Value.ToUpperInvariant() : null,
            Pid = pid.Success ? pid.Groups[1].Value.ToUpperInvariant() : null,
            DriverName = driver ?? "Unavailable",
            DriverVersion = Match(raw.DriverVersion, @"\A[0-9]{1,10}(?:\.[0-9]{1,10}){0,7}\z") ?? "Unavailable",
            ConnectionState = raw.State is "present" or "removed" or "active" or "disabled" or "unplugged" or "not-present" ? raw.State : "unknown",
            ParentId = raw.Parent is null ? null : Id(raw.Parent), ParentHubId = raw.Hub is null ? null : Id(raw.Hub), ControllerId = raw.Controller is null ? null : Id(raw.Controller),
            Topology = Match(raw.Topology, @"\A(?:PCIROOT\([0-9A-Fa-f]+\)|PCI\([0-9A-Fa-f]+\)|USBROOT\([0-9]+\)|USB\([0-9]+\)|#){1,100}\z") ?? "Unavailable (no validated PCI/USB port path)",
            PowerManagement = Match(raw.Power, @"\ACM power: most recently reported D[0-3?]; capabilities=0x[0-9A-F]{8}; policy unavailable\z") ?? "Unavailable (power policy/state not exposed)",
            IdentityQuality = "Locally salted HMAC; safe generic labels replace user-editable names; unrecognized driver filenames pseudonymized. " + Persistence
        };
    }
}
