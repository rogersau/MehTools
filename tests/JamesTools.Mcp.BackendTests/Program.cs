using System.Runtime.Intrinsics.X86;
using JamesTools.Core.Mcp;
using JamesTools.Core.Platform;

var tests = new (string Name, Func<Task> Run)[]
{
    ("reject unsafe root syntax", () => Sync(RootSyntax)),
    ("classify recognized installed components", () => Sync(Classification)),
    ("enforce exact catalog boundaries and ambiguity", () => Sync(CatalogBoundaries)),
    ("inspect metadata without inferring runtime state", () => Sync(Metadata)),
    ("bound depth and output without irrelevant DLL crowding", () => Sync(ScanBounds)),
    ("reject reparse ancestors", () => Sync(ReparseAncestors)),
    ("validate event filters and generate only controlled XPath", () => Sync(EventFilters)),
    ("redact event summaries", () => Sync(EventSummaries)),
    ("health findings require finite observed evidence", () => Sync(HealthEvidence)),
    ("keep every diagnostic category in the bounded metric prefix", () => Sync(MetricPriority)),
    ("report runtime instruction capabilities", () => Sync(InstructionSets)),
    ("distinguish cached inventory from current memory and monitoring", () => Sync(DynamicInventory)),
    ("share one NVML initialization and owner", () => Sync(WindowsNvmlFixture.VerifySharedOwnership)),
    ("honor cancellation, unsupported categories and disposal", BackendBoundaries)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
}
if (args.Contains("--windows-smoke"))
{
    try { await WindowsSmoke(); Console.WriteLine("PASS Windows read-only smoke"); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL Windows read-only smoke: " + ex); }
}
return failed == 0 ? 0 : 1;

static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static void RootSyntax()
{
    foreach (var root in new[] { @"C:\", @"C:\Games\..\Windows", @"C:\Games\.\Game", @"C:\Games\Game:stream", @"\\server\share\Game", @"\\?\C:\Game", @"C:Games", @"C:\Games\bad.", @"C:\Games\bad ", "C:\\Games\\bad\n", "relative", "/tmp/game" })
        Check(GameInspectionReader.NormalizeRoot(root) is null, root);
    Check(GameInspectionReader.NormalizeRoot(@"C:\Games\Example\") == @"C:\Games\Example");
}
static void Classification()
{
    var expected = new Dictionary<string, string>
    {
        ["nvngx_dlss.dll"] = "dlss_super_resolution", ["NVNGX_DLSSG.DLL"] = "dlss_frame_generation",
        ["nvngx_dlssd.dll"] = "dlss_ray_reconstruction", ["ffx_fsr2_api_x64.dll"] = "fsr_indicator",
        ["amd_fidelityfx_dx12.dll"] = "fsr_indicator", ["libxess.dll"] = "xess_super_resolution",
        ["libxess_fg.dll"] = "xess_frame_generation", ["d3d12.dll"] = "graphics_api_indicator", ["game.exe"] = "executable"
    };
    foreach (var pair in expected) Check(GameInspectionReader.ComponentKind(pair.Key) == pair.Value, pair.Key);
    Check(GameInspectionReader.ComponentKind("unrelated.dll") is null);
}
static void CatalogBoundaries()
{
    var catalog = new GameCatalog([new("Example", @"C:\Games\Example", "test")], []);
    foreach (var request in new[] { @"C:\Games", @"C:\Games\Example\bin", @"C:\Games\Example2", @"C:\Games\Example\..\Example", @"C:\Games\Example:stream", "Unknown" })
        Check(GameInspectionReader.Inspect(request, catalog, default).Status == "permission_denied", request);
    var ambiguous = catalog with { Games = catalog.Games.Concat([new LibraryGame("Example", @"D:\Games\Example", "test")]).ToArray() };
    Check(GameInspectionReader.Inspect("Example", ambiguous, default).Status == "unsupported");
}
static void WithGame(Action<string, GameCatalog> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "JamesTools-Mcp-BackendTests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { action(directory, new([new("Fixture", directory, "test")], [])); }
    finally { Directory.Delete(directory, true); }
}
static void Metadata() => WithGame((root, catalog) =>
{
    File.Copy(typeof(object).Assembly.Location, Path.Combine(root, "nvngx_dlss.dll"));
    File.Copy(typeof(object).Assembly.Location, Path.Combine(root, "game.exe"));
    File.WriteAllText(Path.Combine(root, "settings.ini"), "SECRET CONTENT THAT MUST NOT BE READ");
    File.WriteAllText(Path.Combine(root, "runtime.log"), "SECRET CONTENT THAT MUST NOT BE READ");
    var result = GameInspectionReader.Inspect("fixture", catalog, default);
    Check(result.Status == "available");
    Check(result.Components.Count == 4);
    Check(result.Components.Single(c => c.Kind == "dlss_super_resolution").Version is not null);
    Check(result.Components.Single(c => c.Kind == "executable").Version is not null);
    Check(result.Components.All(c => c.RuntimeStatus == "unknown" && !c.Evidence.Contains("SECRET")));
    Check(result.TimestampUtc.Offset == TimeSpan.Zero);
    Check(GameInspectionReader.Inspect(root, catalog, default).Components.Count == 4);
    Check(GameInspectionReader.Inspect("Fixture: Subtitle", catalog with { Games = [catalog.Games[0] with { Name = "Fixture: Subtitle" }] }, default).Components.Count == 4);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Throws<OperationCanceledException>(() => GameInspectionReader.Inspect(root, catalog, cancel.Token));
});
static void ScanBounds() => WithGame((root, catalog) =>
{
    for (var i = 0; i < 160; i++) File.WriteAllText(Path.Combine(root, $"unrelated{i}.dll"), "irrelevant");
    for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(root, $"settings{i}.ini"), "content");
    File.Copy(typeof(object).Assembly.Location, Path.Combine(root, "nvngx_dlssg.dll"));
    var deep = Path.Combine(root, "a", "b", "c", "d"); Directory.CreateDirectory(deep);
    File.WriteAllText(Path.Combine(deep, "not-read.log"), "content");
    var result = GameInspectionReader.Inspect("Fixture", catalog, default);
    Check(result.Status == "partial");
    Check(result.Components.Count <= 128);
    Check(result.Components.Any(c => c.Kind == "dlss_frame_generation"));
    Check(result.Components.All(c => c.Kind != "dll" && !c.RelativePath.Contains("not-read")));
});
static void ReparseAncestors() => WithGame((root, _) =>
{
    var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
    var link = Path.Combine(root, "link");
    WindowsJunctionFixture.Create(link, target);
    try
    {
        Throws<UnauthorizedAccessException>(() => GameInspectionReader.CheckAncestors(link));
        var nested = Path.Combine(link, "nested"); Directory.CreateDirectory(nested);
        var catalog = new GameCatalog([new("Linked", nested, "test")], []);
        Check(GameInspectionReader.Inspect("Linked", catalog, default).Status == "permission_denied");
    }
    finally { Directory.Delete(link); }
});
static void EventFilters()
{
    var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    var query = WindowsEventLogs.Validate(new(Source: "Display", Severity: "error", EventIds: [4101], Category: "gpu", Limit: 5), now);
    Check(query.Limit == 5 && query.XPath.Contains("EventID=4101") && query.XPath.Contains("Level=2") && query.XPath.Contains("Provider[@Name='Display']"));
    Check(query.Logs.SequenceEqual(["System", "Application"]));
    Check(WindowsEventLogs.Validate(new(Category: "Application"), now).Logs.SequenceEqual(["Application"]));
    var large = WindowsEventLogs.Validate(new(EventIds: Enumerable.Range(1, 32).ToArray()), now);
    Check(large.XPaths.Length > 1 && large.XPaths.All(x => x.Split("EventID=").Length <= 5));
    Check(System.Xml.Linq.XElement.Parse(large.Structured("System")).Name == "QueryList");
    var filters = new EventLogFilter[]
    {
        new(Limit: 0), new(Limit: 201), new(Source: "Display'] or *"), new(Category: "Security"), new(Severity: "all"),
        new(EventIds: [-1]), new(EventIds: new int[33]), new(FromUtc: now, ToUtc: now.AddHours(-1)),
        new(FromUtc: now.AddDays(-8), ToUtc: now), new(ToUtc: now.AddDays(1)), new(Source: "Display", Category: "storage")
    };
    foreach (var filter in filters) Throws<ArgumentException>(() => WindowsEventLogs.Validate(filter, now));
    var offset = now.ToOffset(TimeSpan.FromHours(10));
    Check(WindowsEventLogs.Validate(new(FromUtc: offset.AddHours(-1), ToUtc: offset), now).XPath.Contains("2026-11-15T11:00:00.0000000Z"));
}
static void EventSummaries()
{
    var summary = WindowsEventLogs.Summary("SECRET SOURCE WITH IP 192.0.2.1", 100, "error");
    Check(!summary.Contains("SECRET") && !summary.Contains("192.0.2.1"));
    Check(WindowsEventLogs.Summary("Microsoft-Windows-Kernel-Power", 41, "critical").Contains("does not identify its cause"));
}
static void HealthEvidence()
{
    var now = DateTimeOffset.UtcNow;
    Metric Metric(string name, double? value, string status) => new(name, value, "%", now, status, "fixture");
    var snapshot = new MetricsSnapshot(1, now,
        [Metric("memory.physical_load", 95, "available"), Metric("gpu.thermal_limited", 1, "unsupported"), Metric("storage.transfer_latency", double.NaN, "available")], []);
    var findings = WindowsDiagnosticBackend.Evaluate(snapshot);
    Check(findings.Count == 1 && findings[0].Component == "memory");
    Check(findings[0].Evidence.Single().Contains("95") && findings[0].Explanation!.Contains("single"));
    Check(WindowsDiagnosticBackend.Evaluate(snapshot with { Metrics = [] }).Count == 0);
    Check(WindowsDiagnosticBackend.Evaluate(snapshot with { Metrics = [snapshot.Metrics[0] with { Status = "ok" }] }).Count == 1,
        "Normalized session observations must retain findings.");
}
static void InstructionSets()
{
    var sets = WindowsInventory.InstructionSets();
    Check(sets["avx2"] == Avx2.IsSupported.ToString());
    Check(sets.Count >= 16 && sets.Values.All(v => v is "True" or "False"));
}
static void MetricPriority()
{
    var metrics = Enumerable.Range(0, 30).Select(i => new Metric("gpu.optional" + i, i, "count", DateTimeOffset.UtcNow, "available", "fixture"))
        .Concat(new[] { "cpu.utilization", "memory.physical_load", "storage.transfer_latency", "network.receive", "gpu.temperature", "cpu.temperature" }
            .Select(name => new Metric(name, null, "%", DateTimeOffset.UtcNow, "unavailable", "fixture"))).ToArray();
    var prefix = WindowsDiagnosticBackend.Prioritize(metrics).Take(24).Select(m => m.Name).ToArray();
    Check(prefix.Contains("memory.physical_load") && prefix.Contains("storage.transfer_latency") && prefix.Contains("network.receive"));
    Check(prefix.Contains("cpu.temperature"));
}
static void DynamicInventory()
{
    var old = DateTimeOffset.UtcNow.AddDays(-1);
    var now = DateTimeOffset.UtcNow;
    var inventory = new SystemInventory(1, old, [new("cpu", "Fixture", new Dictionary<string, string?>(), new("available"))], []);
    var snapshot = new MetricsSnapshot(1, now,
        [new("memory.physical_total", 16384, "MiB", now, "available", "fixture"), new("memory.physical_used", 4096, "MiB", now, "available", "fixture"),
        new("cpu.temperature", null, "°C", now, "unsupported", "fixture", "No sensor")], []);
    var combined = WindowsDiagnosticBackend.CombineInventory(inventory, snapshot);
    var ram = combined.Components.Single(c => c.Name == "Current physical memory");
    Check(ram.Availability.Status == "available" && ram.Properties["availableMiB"] == "12288" && ram.Properties["totalMiB"] == "16384");
    Check(ram.Properties["observedAtUtc"] == now.ToString("O"));
    Check(combined.Components.Single(c => c.Name == "Static inventory collection").Properties["observedAtUtc"] == old.ToString("O"));
    Check(combined.Components.Single(c => c.Name == "cpu.temperature").Availability.Status == "unsupported");
    var missing = WindowsDiagnosticBackend.CombineInventory(inventory, snapshot with { Metrics = [] });
    var missingRam = missing.Components.Single(c => c.Name == "Current physical memory");
    Check(missingRam.Availability.Status == "unavailable" && missingRam.Properties["totalMiB"] is null && missingRam.Properties["availableMiB"] is null);
}
static async Task BackendBoundaries()
{
    using var backend = new WindowsDiagnosticBackend(Path.GetTempPath());
    var capabilities = backend.GetCapabilities();
    Check(capabilities.Profiles.SequenceEqual(["general", "gaming", "cpu", "gpu", "storage"]));
    Check((await backend.RunDiagnosticsAsync("arbitrary-command", default)).Status == "unsupported");
    Check((await backend.GetEventLogsAsync(new(Source: "arbitrary"), default)).Status == "unsupported");
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await backend.SampleAsync(cancel.Token); throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException) { }
    backend.Dispose();
    try { await backend.SampleAsync(default); throw new InvalidOperationException("Expected disposed"); } catch (ObjectDisposedException) { }
}
static async Task WindowsSmoke()
{
    using var backend = new WindowsDiagnosticBackend(Path.GetTempPath());
    var first = await backend.SampleAsync(default);
    await Task.Delay(150);
    var second = await backend.SampleAsync(default);
    Check(second.Metrics.Any(m => m.Name == "cpu.utilization" && m.Status == "available"));
    Check(second.Metrics.Where(m => m.Status == "available").All(m => m.Value.HasValue && double.IsFinite(m.Value.Value)));
    Check(second.Metrics.Where(m => m.Status != "available").All(m => m.Value is null));
    Check(second.Metrics.All(m => m.TimestampUtc.Offset == TimeSpan.Zero));
    Check(second.Metrics.Single(m => m.Name == "cpu.temperature").Status == "unsupported");
    Check(second.Metrics.Single(m => m.Name == "cpu.package_power").Status == "unsupported");
    var inventory = await backend.GetSystemInfoAsync(default);
    Check(inventory.Components.Count > 0, "Inventory collector did not complete");
    var refreshed = await backend.GetSystemInfoAsync(default);
    Check(inventory.Components.Single(c => c.Name == "Static inventory collection").Properties["observedAtUtc"] ==
        refreshed.Components.Single(c => c.Name == "Static inventory collection").Properties["observedAtUtc"], "Static inventory not cached");
    Check(refreshed.Components.Single(c => c.Name == "Current physical memory").Properties["availableMiB"] is not null);
    Check(inventory.Components.All(c => c.Properties.Keys.All(k => !new[] { "SerialNumber", "MACAddress", "IPAddress", "UserName" }.Contains(k))));
    var events = await backend.GetEventLogsAsync(new(Limit: 3), default);
    Check(events.Events.Count <= 3 && events.TimestampUtc.Offset == TimeSpan.Zero);
    Check(events.Status is "available" or "partial" or "permission_denied" or "unavailable", "Event reader failed: " + string.Join(';', events.Warnings));
    var health = await backend.GetHealthReportAsync(default);
    Check(health.Assessment is "observations_only" or "insufficient_data");
    Console.WriteLine($"Windows smoke: {first.Metrics.Count}/{second.Metrics.Count} metrics; {inventory.Components.Count} inventory components; events {events.Status}; health {health.Assessment}.");
}
