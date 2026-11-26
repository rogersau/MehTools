using System.Diagnostics;
using System.Text.Json;
using JamesTools.Core.Diagnostics;

internal static class InvestigationDeviceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static InvestigationClock Clock() => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, Origin);
    private static InvestigationDevicePrivacy Privacy(byte value = 7) => new(Enumerable.Repeat(value, 32).ToArray());
    private const string Usb = @"USB\VID_1234&PID_ABCD\PRIVATE-SERIAL-123456";
    private const string Hub = @"USB\ROOT_HUB30\PRIVATE-HUB-SERIAL";
    private const string Controller = @"PCI\VEN_8086&DEV_1234\PRIVATE-CONTROLLER";
    private const string Endpoint = "audio:{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}";
    private static InvestigationRawDevice Device() => new(Usb)
    {
        Name = "Alice's PRIVATE-SERIAL-123456 headphones", Class = "USB", Manufacturer = "Logitech",
        DriverName = "oem42.inf", DriverVersion = "10.0.1234.5", Parent = Hub, Hub = Hub, Controller = Controller,
        Topology = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)",
        Power = "CM power: most recently reported D0; capabilities=0x00000001; policy unavailable"
    };

    public static async Task Run()
    {
        await IdentityTopologyAndAudio();
        await OutOfOrderAndSnapshots();
        await BoundedQueueAndDisposal();
        await FailuresAndConfiguration();
        PrivacyBoundary();
        NativeCallbackDoesNotPublishStatus();
    }

    private static void NativeCallbackDoesNotPublishStatus()
    {
        using var source = new InvestigationWindowsDeviceSource();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var published = 0;
        typeof(InvestigationWindowsDeviceSource).GetField("status", flags)!.SetValue(source, (Action<InvestigationCollectorStatus>)(_ => published++));
        typeof(InvestigationWindowsDeviceSource).GetMethod("Callback", flags)!.Invoke(source, [(Action)(() => { })]);
        Check(published == 0 && source.CallbackStatus.Detail.Contains("Callback count: 1"),
            "Native/audio callback must use atomic counters and leave status publication to snapshot readers");
    }

    private static async Task IdentityTopologyAndAudio()
    {
        var source = new FakeSource(Device(), new(Hub) { Class = "USB" }, new(Controller) { Class = "USB" },
            new(Endpoint) { Class = "AudioEndpoint", Parent = Usb, Hub = Hub, Controller = Controller, State = "active" });
        var events = new List<InvestigationEvent>();
        await using var collector = new InvestigationDeviceCollector(Clock(), events.Add, source, Privacy());
        Check(source.StartedBeforeInventory, "inventory must follow subscription, avoiding a startup blind gap");
        Check(source.InventoryReads == 1, "constructor must take one inventory");
        var usb = collector.Inventory.Single(x => x.Vid == "1234");
        Check(usb.Pid == "ABCD" && usb.DriverVersion == "10.0.1234.5" && usb.DriverName == "oem42.inf", "USB/driver identity lost");
        Check(usb.ParentId == Privacy().Id(Hub) && usb.ParentHubId == Privacy().Id(Hub) && usb.ControllerId == Privacy().Id(Controller), "ancestry IDs must use the event identity salt");
        Check(usb.Topology!.EndsWith("USB(3)") && usb.PowerManagement!.Contains("D0"), "topology/power missing");
        source.Emit(new(InvestigationDeviceSignalKind.Interface, "CM interfaces", "Interface arrived") { Key = "interface-private-path" });
        source.Emit(new(InvestigationDeviceSignalKind.Audio, "Core Audio", "Default audio endpoint changed") { Key = Endpoint, AudioFlow = 0, AudioRole = 2 });
        source.Emit(new(InvestigationDeviceSignalKind.Audio, "Core Audio", "Default audio endpoint changed") { AudioFlow = 1, AudioRole = 0 });
        await collector.FlushAsync();
        Check(events[0].DeviceId == usb.Id && collector.Inventory.Count == 4, "interface arrival must map to existing devnode");
        Check(events[1].DeviceId == collector.Inventory.Single(x => x.Class == "AudioEndpoint").Id, "audio inventory/event identity mismatch");
        Check(events[1].Details["role"] == "communications" && events[1].Details["flow"] == "render", "audio roles lost");
        Check(events[2].DeviceId is null && events[2].Details["flow"] == "capture", "no-default endpoint must remain null");
        Check(events.All(e => e.TimestampBasis.Contains("receipt") && e.TimestampSeconds == e.ReceiptSeconds && e.UncertaintyMs is null), "receipt incorrectly claimed as actual occurrence/reset time");
        source.Emit(new(InvestigationDeviceSignalKind.Interface, "CM interfaces", "Interface removed") { Key = "interface-private-path", Removed = true });
        await collector.FlushAsync();
        Check(collector.Inventory.Single(x => x.Id == usb.Id).ConnectionState == "present", "interface disablement incorrectly asserted physical device removal");
        source.Emit(new(InvestigationDeviceSignalKind.Device, "CM instances", "Device removed") { Key = Usb, Removed = true });
        source.Emit(new(InvestigationDeviceSignalKind.Audio, "Core Audio", "Audio endpoint removed") { Key = Endpoint, Removed = true });
        await collector.FlushAsync();
        Check(collector.Inventory.Single(x => x.Id == usb.Id).ConnectionState == "removed", "removed device identity not retained");
        Check(collector.Inventory.Single(x => x.Id == Privacy().Id(Endpoint)).ConnectionState == "removed", "audio removal lost identity");
        const string arrived = @"USB\VID_5555&PID_6666\NEW-PRIVATE-SERIAL";
        source.Extra = new(arrived) { Class = "USB", Parent = Hub, Hub = Hub, Controller = Controller, Topology = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)" };
        source.Emit(new(InvestigationDeviceSignalKind.Device, "CM instances", "Device arrived") { Key = arrived });
        await collector.FlushAsync();
        Check(collector.Inventory.Single(x => x.Vid == "5555").ParentHubId == Privacy().Id(Hub) && events.Last().DeviceId == Privacy().Id(arrived), "new arrival did not populate current identity/topology");
        AssertPrivate(JsonSerializer.Serialize(new { collector.Inventory, events, collector.Status }));
        Check(source.InventoryReads == 1, "notifications must not cause periodic/full device polling");
    }

    private static async Task OutOfOrderAndSnapshots()
    {
        var source = new FakeSource(Device());
        var events = new List<InvestigationEvent>();
        await using var collector = new InvestigationDeviceCollector(Clock(), events.Add, source, Privacy());
        var before = collector.Inventory;
        source.Emit(new(InvestigationDeviceSignalKind.Device, "CM instances", "Device removed") { Key = Usb, Removed = true, Utc = Origin.AddSeconds(20) });
        source.Emit(new(InvestigationDeviceSignalKind.Device, "CM instances", "Device arrived") { Key = Usb, Utc = Origin.AddSeconds(10) });
        await collector.FlushAsync();
        Check(events.Count == 2 && events[1].TimestampSeconds == 10 && events[1].Quality.Contains("out-of-order"), "out-of-order evidence should remain visible at its provider time");
        Check(collector.Inventory.Single().ConnectionState == "removed", "late arrival resurrected a removed device");
        Check(before.Single().ConnectionState == "present", "returned inventory snapshot mutated");
        Check(collector.Inventory is not InvestigationDevice[], "public snapshot exposes mutable internal array");
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) { Check(collector.Inventory.Count == 1, "concurrent inventory corrupted"); _ = collector.Status.Count; }
        }));
        await Task.WhenAll(tasks);
        Check(events.All(x => x.TimestampBasis.Contains("UTC") && x.UncertaintyMs is null), "UTC timing uncertainty lost");
    }

    private static async Task BoundedQueueAndDisposal()
    {
        var source = new FakeSource(Device());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var published = 0;
        var collector = new InvestigationDeviceCollector(Clock(), _ =>
        {
            Interlocked.Increment(ref published);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test publisher release");
        }, source, Privacy(), capacity: 2);
        try
        {
            source.Emit(new(InvestigationDeviceSignalKind.Process, "WMI process start", "Utility started"));
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "worker never published");
            for (var i = 0; i < 12; i++) source.Emit(new(InvestigationDeviceSignalKind.Process, "WMI process start", "Utility started"));
            Check(collector.Status.Single(x => x.Source == "Device delivery").EventsDropped == 10, "queue overflow was not exactly accounted");
            var dispose = collector.DisposeAsync().AsTask();
            Check(!dispose.IsCompleted, "Dispose must await an in-flight publisher");
            release.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
            source.Emit(new(InvestigationDeviceSignalKind.Device, "CM instances", "Device arrived") { Key = Usb });
            Check(published == 1 && source.DisposeCount == 1, "disposal published queued/late events or failed to unregister once");
            await collector.DisposeAsync();
            Check(source.DisposeCount == 1, "disposal is not idempotent");
            Check(collector.Status.Single(x => x.Source == "Device delivery").EventsDropped == 12, "queued shutdown discards were not accounted");
        }
        finally { release.Set(); await collector.DisposeAsync(); }
    }

    private static async Task FailuresAndConfiguration()
    {
        var source = new FakeSource(Device()) { ReportUnavailable = true };
        await using var collector = new InvestigationDeviceCollector(Clock(), _ => throw new InvalidOperationException("publisher failed"), source, Privacy());
        Check(collector.Status.Any(x => x.Source == "Core Audio" && x.Availability == "unavailable"), "startup failures hidden");
        source.Emit(new(InvestigationDeviceSignalKind.Configuration, "WMI configuration", "Configuration changed"));
        source.Emit(new(InvestigationDeviceSignalKind.Configuration, "WMI configuration", "Configuration changed"));
        await collector.FlushAsync();
        Check(source.InventoryReads == 2, "configuration storm was not coalesced into bounded work");
        Check(collector.Status.Single(x => x.Source == "Device delivery").Detail.Contains("Publisher failures: 2"), "publisher failures hidden or worker stopped");
        Check(collector.Status.Single(x => x.Source == "Device delivery") is { EventsDropped: >= 2, Availability: "partial" }, "failed publication must reduce coverage and increment dropped evidence");
        var failed = new FakeSource() { ThrowStartup = true };
        var startup = new InvestigationDeviceCollector(Clock(), _ => { }, failed, Privacy());
        Check(startup.Status.Any(x => x.Availability == "unavailable"), "inventory/startup exception hidden");
        await startup.DisposeAsync();
        Check(failed.DisposeCount == 1, "partial startup was not cleaned up");
    }

    private static void PrivacyBoundary()
    {
        var p = Privacy();
        var raw = Device() with { Manufacturer = "Alice PRIVATE-SERIAL-123456", DriverName = @"C:\Users\Alice\secret.inf", DriverVersion = "1234 Alice", Class = "Alice", Topology = @"C:\Users\Alice", Power = "Alice" };
        var text = JsonSerializer.Serialize(p.Redact(raw));
        AssertPrivate(text);
        Check(!text.Contains("Alice") && text.Contains("Unavailable"), "personal metadata was not rejected");
        Check(p.Id(Usb) == Privacy().Id(Usb.ToLowerInvariant()), "same local salt/case-insensitive identifier must map across recordings");
        Check(p.Id(Usb) != Privacy(8).Id(Usb), "different salts leaked a globally stable identifier");
        Check(p.Redact(Device()).Name == "Logitech USB device", "generic safe display label missing");
        Check(!p.Redact(Device() with { DriverName = "Alice.inf" }).DriverName!.Contains("Alice"), "private driver filename escaped pseudonymization");
        Check(p.Redact(new("unknown")).Topology!.Contains("Unavailable") && p.Redact(new("unknown")).PowerManagement!.Contains("Unavailable"), "missing topology/power fabricated");
    }

    private static void AssertPrivate(string json)
    {
        foreach (var secret in new[] { "PRIVATE-SERIAL", "PRIVATE-HUB", "PRIVATE-CONTROLLER", "deadbeef", "Alice", "interface-private-path", "USB\\\\VID_" })
            Check(!json.Contains(secret, StringComparison.OrdinalIgnoreCase), "portable evidence leaked " + secret);
    }

    /// <summary>Opt-in native registration/enumeration/cleanup check. No device manipulation or elevation.</summary>
    public static async Task Smoke()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inventory = InvestigationDeviceCollector.ReadInventory();
        await using var collector = new InvestigationDeviceCollector(Clock(), _ => { });
        Check(collector.Status.Any(x => x.Source == "Device inventory"), "native inventory status missing");
        Check(collector.Status.Any(x => x.Source == "CM instances"), "CM subscription status missing");
        Check(collector.Status.Any(x => x.Source == "Audio inventory" && x.Availability == "available"), "native audio inventory unavailable");
        Check(collector.Inventory.All(x => x.Id.StartsWith("device-") && !x.Name.Contains('\\')), "native IDs or paths leaked");
        Check(inventory.All(x => !x.Id.Contains('{')), "endpoint ID escaped redaction");
        Console.WriteLine("Device smoke: " + collector.Inventory.Count + " devices; " + string.Join("; ", collector.Status.Select(x => x.Source + "=" + x.Availability)));
        await collector.DisposeAsync();
        Check(!collector.Status.Any(x => x.Source.Contains("cleanup") && x.Availability == "degraded"), "native subscription cleanup failed");
    }

    private sealed class FakeSource(params InvestigationRawDevice[] initial) : IInvestigationDeviceSource
    {
        private Action<InvestigationDeviceSignal> notify = _ => { };
        private bool started;
        public int InventoryReads { get; private set; }
        public bool StartedBeforeInventory { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ReportUnavailable { get; init; }
        public bool ThrowStartup { get; init; }
        public InvestigationRawDevice? Extra { get; set; }
        public void Start(Action<InvestigationDeviceSignal> notify, Action<InvestigationCollectorStatus> status)
        {
            this.notify = notify; started = true;
            if (ReportUnavailable) status(new("Core Audio", "unavailable", "Injected registration failure"));
            if (ThrowStartup) throw new InvalidOperationException("injected startup failure");
        }
        public IReadOnlyList<InvestigationRawDevice> ReadInventory() { InventoryReads++; StartedBeforeInventory = started; return initial; }
        public InvestigationRawDevice? ReadDevice(string key) => initial.FirstOrDefault(x => x.Key == key) ?? (Extra?.Key == key ? Extra : null);
        public string? ResolveKey(string key, bool deviceInterface) => deviceInterface ? key == "interface-private-path" ? Usb : null : key;
        public void Emit(InvestigationDeviceSignal signal) => notify(signal);
        public void Dispose() { DisposeCount++; }
    }
}
