using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Recording;
using JamesTools.Core.Platform;
using JamesTools.Core.Sessions;
using JamesTools.Core.Storage;

internal static class StorageTests
{
    public static async Task Run()
    {
        PlannerIsSafeAndPure();
        CapacityUsesExistingFolder();
        CleanupPreviewProtectsAndDeduplicates();
        SettingsPersistStorageOptions();
        await CapacityWarningsAndOrganisedStartup();
    }

    private static void CapacityUsesExistingFolder()
    {
        using var temp = new TempDirectory();
        var provider = new FileSystemStorageCapacityProvider();
        var result = provider.Query(Path.Combine(temp.Path, "not-created", "capture"));
        Check(result.Capacity is { TotalBytes: > 0, AvailableBytes: >= 0 } && result.Capacity.QueriedPath == temp.Path,
            "capacity did not use the nearest existing directory");
        var file = Path.Combine(temp.Path, "file.txt");
        File.WriteAllText(file, "temporary fixture");
        Check(!provider.Query(file).IsKnown && !provider.Query(Path.Combine(file, "child")).IsKnown,
            "a file path was reported as usable recording storage");
        Check(!provider.Query("relative-folder").IsKnown, "relative storage should remain unknown");
    }

    private static void PlannerIsSafeAndPure()
    {
        using var temp = new TempDirectory();
        var before = Directory.GetDirectories(temp.Path, "*", SearchOption.AllDirectories).Length;
        var plan = RecordingFolderPlanner.Plan(temp.Path, "../CON: Game. ", new DateTime(2026, 4, 5), true);
        Check(plan.PlannedDirectory.StartsWith(temp.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "planned folder escaped the base");
        Check(plan.PlannedDirectory.Contains("_CON_ Game", StringComparison.OrdinalIgnoreCase), "invalid or reserved game name was not made safe");
        Check(plan.PlannedDirectory.EndsWith(Path.Combine("2026-04-05"), StringComparison.Ordinal), "date segment was incorrect");
        Check(!Directory.Exists(plan.PlannedDirectory), "planning created a directory");
        Check(RecordingFolderPlanner.SanitiseGameFolderName("CON").StartsWith('_'), "reserved Windows device name was accepted");
        Check(RecordingFolderPlanner.SanitiseGameFolderName("..").Length > 0, "dot traversal name was accepted");
        Check(Directory.GetDirectories(temp.Path, "*", SearchOption.AllDirectories).Length == before, "planning changed the directory tree");
    }

    private static void CleanupPreviewProtectsAndDeduplicates()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "recordings"); Directory.CreateDirectory(root);
        var old = WriteVideo(root, "old.mkv", 42);
        var young = WriteVideo(root, "young.mp4", 2);
        var favourite = WriteVideo(root, "favorite.mkv", 60);
        var clip = WriteVideo(root, "clip.mkv", 60);
        var active = WriteVideo(root, "active.mkv", 60);
        var interrupted = WriteVideo(root, "interrupted.mkv", 60);
        var outside = WriteVideo(temp.Path, "outside.mp4", 60);
        var missing = Path.Combine(root, "missing.mkv");
        var report = Path.Combine(root, "report.json"); File.WriteAllText(report, "report");
        var sessions = new[]
        {
            Session("old", SessionStatus.Completed, old), Session("duplicate", SessionStatus.Completed, old),
            Session("young", SessionStatus.Completed, young), Session("fav", SessionStatus.Completed, favourite, favourite: true),
            Session("clip", SessionStatus.Completed, clip, (SessionAttachmentKind.Clip, clip)),
            Session("active", SessionStatus.Active, active), Session("interrupted", SessionStatus.Interrupted, interrupted),
            Session("outside", SessionStatus.Completed, outside), Session("missing", SessionStatus.Completed, missing),
            Session("report", SessionStatus.Completed, report, (SessionAttachmentKind.PerformanceReport, report))
        };
        var now = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var preview = CleanupPreviewBuilder.Build(sessions, [root], now, 30, null);
        Check(preview.Entries.Count(e => e.Path.Equals(old, StringComparison.OrdinalIgnoreCase)) == 1, "duplicate references were not canonicalised");
        Check(preview.Entries.Single(e => e.Path == old).Candidate && preview.Entries.Single(e => e.Path == old).AgeDays == 42, "age-boundary candidate was not suggested");
        Check(!preview.Entries.Single(e => e.Path == young).Candidate, "recent recording crossed the age boundary");
        Check(!preview.Entries.Single(e => e.Path == favourite).Candidate && preview.Entries.Single(e => e.Path == favourite).Reason.Contains("favourite", StringComparison.OrdinalIgnoreCase), "favourite was not protected");
        Check(!preview.Entries.Single(e => e.Path == clip).Candidate, "clip attachment was not protected");
        Check(!preview.Entries.Single(e => e.Path == active).Candidate && !preview.Entries.Single(e => e.Path == interrupted).Candidate, "non-completed session was not protected");
        Check(!preview.Entries.Single(e => e.Path == outside).Candidate && preview.Entries.Single(e => e.Path == outside).Reason.Contains("outside", StringComparison.OrdinalIgnoreCase), "outside-root media was not excluded");
        Check(!preview.Entries.Single(e => e.Path == missing).Candidate && preview.Entries.Single(e => e.Path == missing).Reason.Contains("missing", StringComparison.OrdinalIgnoreCase), "missing file was not excluded");
        Check(preview.Entries.Single(e => e.Path == missing).AgeDays is null && preview.Entries.Single(e => e.Path == missing).SizeBytes is null, "missing file fabricated age or size metadata");
        Check(preview.Entries.All(e => !e.Path.Equals(report, StringComparison.OrdinalIgnoreCase)), "performance report was included");
        var budgetPreview = CleanupPreviewBuilder.Build(sessions, [root], now, null, 0.000000001);
        Check(budgetPreview.Entries.Single(e => e.Path == old).Candidate, "budget did not suggest the oldest media first");
        var generousBudget = CleanupPreviewBuilder.Build(sessions, [root], now, null, 1);
        Check(!generousBudget.Entries.Any(e => e.Candidate), "media at or below the budget boundary was suggested");
        Check(preview.PotentialReclaimableBytes == new FileInfo(old).Length, "potential reclaimable size was incorrect");
        Check(preview.ToText().Contains("Suggestions only", StringComparison.OrdinalIgnoreCase), "export did not explain that this is a suggestion");
        var malformedRoots = CleanupPreviewBuilder.Build(sessions, [root, "C:\\invalid\0root"], now, 30, null);
        Check(malformedRoots.Entries.Single(e => e.Path == old).Candidate, "invalid draft root prevented valid suggestions");
        var sharedProtection = CleanupPreviewBuilder.Build(sessions.Append(Session("shared favourite", SessionStatus.Completed, old, favourite: true)), [root], now, 30, null);
        Check(!sharedProtection.Entries.Single(e => e.Path == old).Candidate, "another favourite reference did not protect a shared recording");
        var relativeReference = CleanupPreviewBuilder.Build([Session("relative", SessionStatus.Completed, "relative.mkv")], [root], now, 30, null);
        Check(relativeReference.Entries.Count == 0, "relative attachment path was resolved against the app working directory");
    }

    private static void SettingsPersistStorageOptions()
    {
        var store = new SettingsStore(Path.GetTempPath());
        var settings = new AppSettings { OrganiseCapturesByGameAndDate = true, LowStorageWarningsEnabled = true, LowStorageThresholdGiB = 22.5, CleanupSuggestionAgeDays = 35, CleanupSuggestionBudgetGiB = 100 };
        var snapshot = settings.Snapshot();
        Check(snapshot.OrganiseCapturesByGameAndDate && snapshot.LowStorageWarningsEnabled && snapshot.CleanupSuggestionAgeDays == 35, "storage options were omitted from settings snapshots");
        var loaded = store.Deserialize(store.Serialize(settings));
        Check(loaded.OrganiseCapturesByGameAndDate && loaded.LowStorageWarningsEnabled && loaded.LowStorageThresholdGiB == 22.5 && loaded.CleanupSuggestionAgeDays == 35 && loaded.CleanupSuggestionBudgetGiB == 100, "storage options did not survive settings export/import");
        Check(!new AppSettings().OrganiseCapturesByGameAndDate && !new AppSettings().LowStorageWarningsEnabled, "backwards-compatible defaults changed");
        var legacy = store.Deserialize("{}");
        Check(legacy.LowStorageThresholdGiB == 10 && !legacy.OrganiseCapturesByGameAndDate && !legacy.LowStorageWarningsEnabled, "legacy settings did not normalize to safe defaults");
        var invalid = new AppSettings { LowStorageThresholdGiB = double.NaN, CleanupSuggestionAgeDays = 0, CleanupSuggestionBudgetGiB = double.PositiveInfinity };
        SettingsStore.Normalize(invalid);
        Check(invalid.LowStorageThresholdGiB == 10 && invalid.CleanupSuggestionAgeDays is null && invalid.CleanupSuggestionBudgetGiB is null, "invalid storage options did not normalize safely");
        Check(SettingsValidation.Validate(new AppSettings { LowStorageThresholdGiB = -1, CleanupSuggestionAgeDays = 0, CleanupSuggestionBudgetGiB = 0 }).Count >= 3, "storage settings validation missed invalid values");
    }

    private static async Task CapacityWarningsAndOrganisedStartup()
    {
        using var temp = new TempDirectory();
        var basePath = Path.Combine(temp.Path, "base"); Directory.CreateDirectory(basePath);
        var settings = new AppSettings
        {
            AutomationEnabled = true, RecordingEnabled = true, RecordingDirectory = basePath,
            ObsExecutablePath = "obs64.exe", ObsConfigRoot = basePath, LowStorageWarningsEnabled = true,
            LowStorageThresholdGiB = 10, OrganiseCapturesByGameAndDate = true,
            PowerForAllSteamGames = false,
            Games = [new GameRule { Name = "CON/../Game", ExecutablePath = @"C:\Games\Example\game.exe", Record = true }]
        };
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", @"C:\Games\Example\game.exe")] };
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        var capacity = new FakeCapacity(new StorageCapacity(5L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, basePath));
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now, capacity);
        var notifications = new List<AutomationNotification>();
        automation.Notify += notifications.Add;
        await automation.TickForTestsAsync();
        var launchedFolder = recorder.LastSettings!.RecordingDirectory;
        Check(launchedFolder.Contains(Path.Combine("CON_.._Game", "2026-01-01"), StringComparison.OrdinalIgnoreCase), "automation did not pass the organised folder to recorder");
        Check(Directory.Exists(launchedFolder), "required directory was not created before recorder preparation");
        Check(notifications.Count(n => n.Title == "Low recording space") == 1, "startup low-space notification was not raised");
        settings.OrganiseCapturesByGameAndDate = false;
        automation.UpdateSettings(settings);
        clock.Now = clock.Now.AddSeconds(31);
        await automation.TickForTestsAsync();
        Check(recorder.LastSettings!.RecordingDirectory == launchedFolder, "settings/date change altered the launched capture folder");
        Check(capacity.Calls == 2, "capacity was not rechecked during capture");
        Check(notifications.Count(n => n.Title == "Low recording space") == 1, "low-space warning cooldown did not suppress duplicate notification");
        clock.Now = clock.Now.AddDays(1);
        await automation.TickForTestsAsync();
        Check(recorder.LastSettings!.RecordingDirectory == launchedFolder, "date change altered the launched capture folder");
        Check(notifications.Count(n => n.Title == "Low recording space") == 2, "warning did not resume after the cooldown");
        var warningsBeforeUnknown = notifications.Count(n => n.Title == "Low recording space");
        capacity.Result = new(null, "volume disconnected");
        clock.Now = clock.Now.AddSeconds(31);
        await automation.TickForTestsAsync();
        Check(notifications.Count(n => n.Title == "Low recording space") == warningsBeforeUnknown, "unknown capacity was treated as zero space");
        capacity.Result = new(new StorageCapacity(50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, basePath), null);
        clock.Now = clock.Now.AddSeconds(31);
        await automation.TickForTestsAsync();
        Check(notifications.Count(n => n.Title == "Low recording space") == warningsBeforeUnknown, "normal capacity triggered a low-space warning");
    }

    private static string WriteVideo(string root, string name, int ageDays)
    {
        var path = Path.Combine(root, name); File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-ageDays));
        return path;
    }

    private static GameSession Session(string name, SessionStatus status, string recording,
        (SessionAttachmentKind Kind, string Path)? extra = null, bool favourite = false)
    {
        var attachments = new List<SessionAttachment> { new() { Kind = SessionAttachmentKind.Recording, Path = recording } };
        if (extra is { } e) attachments.Add(new SessionAttachment { Kind = e.Kind, Path = e.Path });
        return new GameSession { Id = Guid.NewGuid(), GameName = name, Status = status, Favourite = favourite, Attachments = attachments };
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class FakeCapacity(StorageCapacity capacity) : IStorageCapacityProvider
    {
        public int Calls { get; private set; }
        public StorageCapacityResult Result { get; set; } = new(capacity, null);
        public StorageCapacityResult Query(string path)
        {
            Calls++;
            return Result.Capacity is { } current ? new(current with { QueriedPath = path }, null) : Result;
        }
    }
}
