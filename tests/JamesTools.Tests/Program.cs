using System.Text.Json.Nodes;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;

if (args.Contains("--wardogs-montage-integration")) await WardogsExportTests.Integration();
if (args.Contains("--media-integration")) await MediaTests.Integration();
var wardogsIntegration = Array.IndexOf(args, "--wardogs-integration");
if (wardogsIntegration >= 0) await WardogsTests.Integration(args.ElementAtOrDefault(wardogsIntegration + 1) ?? throw new ArgumentException("Supply an absolute tesseract.exe path after --wardogs-integration."));
var tests = new (string Name, Func<Task> Run)[]
{
    ("WARDOGS verified exports and markers", WardogsExportTests.Run),
    ("WARDOGS synthetic scanner, tracking, cancellation and intervals", WardogsTests.Run),
    ("performance history versions, groups, compatibility and persistence", PerformanceHistoryTests.Run),
    ("configuration snapshots and guarded single-file restore", ConfigSnapshotTests.Run),
    ("network diagnostics fake collectors, summaries, retention and validation", NetworkProbeTests.Run),
    ("benchmark recipes, runner timing, persistence and comparability", BenchmarkTests.Run),
    ("media parsing, ranges, arguments and settings", MediaTests.Run),
    ("rolling hardware diagnostic capture", RollingTests.Run),
    ("recording bookmarks capture identity and persistence", BookmarkTests.Run),
    ("settings round trip and snapshot isolation", Tests.SettingsRoundTrip),
    ("settings export/import preserves positions and apps", Tests.SettingsExportImport),
    ("settings validation and duplicate paths", Tests.ValidateSettings),
    ("deterministic recording health alerts and settings", RecordingHealthTests.Run),
    ("vibrance target settings round trip and validation", Tests.VibranceTargetSettings),
    ("profile resolution precedence, legacy defaults and overlay presets", Tests.ProfileResolution),
    ("profile reference validation and protected deletion", Tests.ProfileReferencesAndDeletion),
    ("profile directory satisfies per-game recording validation", Tests.ProfileDirectoryValidation),
    ("capture modes map to OBS launch arguments", Tests.CaptureModeArguments),
    ("exact game matching and Steam path boundaries", Tests.MatchProcesses),
    ("executable aliases, diagnostics, and path validation", Tests.ExecutableAliases),
    ("detects a selected portable OBS configuration", Tests.DetectPortableObs),
    ("OBS retarget preserves tracks and rolls back", Tests.ObsRetargetAndRollback),
    ("OBS replay profile changes preserve audio and roll back", Tests.ObsReplayConfiguration),
    ("OBS waits for a window without changing files", Tests.ObsWaitsForWindow),
    ("OBS refuses hidden capture and concurrent edits", Tests.ObsRejectsUnsafeChanges),
    ("OBS WebSocket auth, requests, status, paths, timeouts and disconnects", ObsWebSocketTests.Run),
    ("automation transitions and recording grace period", Tests.AutomationTransitions),
    ("managed stop intents cover watching disabled", Tests.WatchingOffStopIntent),
    ("recording follows a second selected game", Tests.RecordingSwitchesGames),
    ("alias recorder target keeps canonical game ownership", Tests.AliasRecordingTarget),
    ("automation does not own an existing OBS", Tests.ExistingObsIsNotStopped),
    ("automation stops retrying after OBS closes", Tests.ClosedObsDoesNotLoop),
    ("automation retries startup failures with a limit", Tests.StartFailureBackoff),
    ("manual changes are respected when disabling automation", Tests.ManualChangesAreRespected),
    ("match modes support name and folder rules", Tests.MatchModes),
    ("per-game vibrance and stop delay overrides apply", Tests.PerGameOverrides),
    ("profile capture values, stop delay, vibrance and power reach automation", Tests.ProfileAutomationValues),
    ("HDR pauses vibrance when enabled", Tests.HdrPausesVibrance),
    ("selected vibrance changes only its display and restores ownership", Tests.SelectedVibranceTarget),
    ("focused vibrance restores on alt-tab while recording continues", Tests.FocusedVibrance),
    ("game window vibrance follows display moves and rejects unavailable mapping", Tests.GameWindowVibrance),
    ("targeted vibrance respects manual values and retries failed restores", Tests.TargetedVibranceRestore),
    ("closes configured apps once when a game launches", Tests.ClosesAppsOnGameLaunch),
    ("performance report captures samples without exposing paths", Tests.PerformanceReport),
    ("frame analysis aggregates intervals and tail percentiles", FrameAnalysisTests.Run),
    ("diagnostic timeline isolates streams and preserves missing values", DiagnosticReportTests.Run),
    ("saved report timeline parses markers and analyzes ranges", ReportTimelineTests.Run),
    ("WARDOGS saved reviews, ownership, cancellation and recovery", WardogsReviewTests.Run),
    ("session library tracking, associations, persistence, and recovery", SessionTests.Run),
    ("recording storage planning, capacity warnings, and cleanup suggestions", StorageTests.Run),
    ("live frame CSV handles quoted fields", Tests.LiveFrameCsv)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS  " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL  " + test.Name + ": " + ex); }
}
if (args.Contains("--hardware", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        Console.WriteLine("Power plans: " + string.Join(", ", PowerPlans.List().Select(p => p.Name)));
        Console.WriteLine("Active plan: " + PowerPlans.GetActive());
        using var nvidia = new NvidiaVibrance();
        Console.WriteLine("NVIDIA display vibrance: " + string.Join(", ", nvidia.Read().Select(d => $"{d.Percent}%")));
        Console.WriteLine("PASS  read-only hardware check");
    }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL  read-only hardware check: " + ex.Message); }
}
var total = tests.Length + (args.Contains("--hardware", StringComparer.OrdinalIgnoreCase) ? 1 : 0);
Console.WriteLine($"{total - failed}/{total} tests passed.");
return failed == 0 ? 0 : 1;

internal static class Tests
{
    private const string GamePath = @"C:\Games\Example\game.exe";
    private static readonly Guid DesktopPlan = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid GamingPlan = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static Task LiveFrameCsv()
    {
        var values = LiveFrameSampler.ParseCsv("game.exe,\"value,with comma\",\"quoted \"\"name\"\"\",16.67,NA");
        Assert(values.Length == 5 && values[1] == "value,with comma" && values[2] == "quoted \"name\"" && values[3] == "16.67" && values[4] == "NA", "CSV fields were not parsed correctly");
        return Task.CompletedTask;
    }

    public static async Task PerformanceReport()
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Current executable unavailable");
        await using var logger = await PerformanceLogger.StartAsync(executable);
        logger.SetContext("2560x1440; cap 144; test scene", "Stutters after loading; target 144 FPS");
        logger.AddMarker("Test marker");
        await Task.Delay(TimeSpan.FromSeconds(5));
        var report = await logger.StopAsync();
        Assert(report == await logger.StopAsync(), "stopping twice changed the report");
        Assert(report.Contains("Total CPU:") && !report.Contains("Total CPU: unavailable"), "CPU samples missing");
        Assert(report.Contains("Physical memory used:") && !report.Contains("Physical memory used: unavailable"), "memory samples missing");
        Assert(report.Contains("Selected processes working set:") && !report.Contains("Selected processes working set: unavailable"), "selected process samples missing");
        Assert(report.Contains("Selected processes private bytes:") && !report.Contains("Selected processes private bytes: unavailable"), "private allocation samples missing");
        Assert(report.Contains("System committed memory:") && !report.Contains("System committed memory: unavailable"), "commit samples missing");
        Assert(report.Contains("2560x1440; cap 144; test scene") && report.Contains("Stutters after loading; target 144 FPS") && report.Contains("Test marker"), "player context or marker missing");
        Assert(report.Contains("## Sample timeline (CSV)") && report.Contains("## Logical CPU load"), "diagnostic timeline missing");
        Assert(ReportTimeline.TryParse(report, out var timeline, out var timelineError), "generated report could not be viewed: " + timelineError);
        Assert(timeline!.Rows.Any(r => r.MemoryUsedMiB is > 0), "generated RAM timeline column does not match the viewer");
        var summaryStart = report.IndexOf("```json", StringComparison.Ordinal) + 7;
        var summaryEnd = report.IndexOf("```", summaryStart, StringComparison.Ordinal);
        using var summary = System.Text.Json.JsonDocument.Parse(report[summaryStart..summaryEnd]);
        Assert(summary.RootElement.GetProperty("schemaVersion").GetInt32() == 2 && summary.RootElement.GetProperty("fpsAvg").ValueKind == System.Text.Json.JsonValueKind.Null,
            "summary version or unavailable frame metric incorrect");
        Assert(summary.RootElement.GetProperty("timelineRows").GetInt32() > 0, "timeline has no real sample rows");
        Assert(!report.Contains(executable, StringComparison.OrdinalIgnoreCase), "full executable path leaked into report");
        Assert(report.Contains("FPS, frame times") && report.Contains("not measured"), "measurement limitations missing");
    }

    public static Task SettingsRoundTrip()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);
        var settings = Settings();
        settings.TrayOnMinimize = true;
        settings.ObsWebSocketPort = 4456;
        settings.ReplayBufferSeconds = 95;
        settings.SaveClipHotkey = "Ctrl+Shift+F9";
        settings.StutterMarkerHotkey = "Ctrl+Shift+F8";
        settings.Games.Add(new GameRule { Name = "Second", ExecutablePath = @"C:\Games\two.exe", VibrancePercent = 90, RecordingStopDelaySeconds = 20 });
        settings.Games[1].CaptureMode = CaptureMode.Both;
        settings.Profiles.Add(new GameProfile { Name = "Recording", CaptureMode = CaptureMode.Both, ReplayBufferSeconds = 110, VibrancePercent = 65 });
        settings.Games[1].ProfileId = settings.Profiles[0].Id;
        var copy = settings.Snapshot();
        settings.Games[0].Name = "Changed";
        Assert(copy.Games[0].Name == "Example", "snapshot shared a mutable game");
        Assert(copy.Games[1].VibrancePercent == 90 && copy.Games[1].RecordingStopDelaySeconds == 20, "snapshot did not copy per-game overrides");
        Assert(copy.ObsWebSocketPort == 4456, "snapshot did not copy the OBS WebSocket port");
        Assert(copy.ReplayBufferSeconds == 95 && copy.SaveClipHotkey == "Ctrl+Shift+F9" && copy.StutterMarkerHotkey == "Ctrl+Shift+F8", "snapshot missed replay/hotkey settings");
        Assert(copy.Games[1].CaptureMode == CaptureMode.Both, "snapshot missed game capture mode");
        settings.Profiles[0].Name = "Changed profile";
        Assert(copy.Profiles[0].Name == "Recording" && copy.Games[1].ProfileId == copy.Profiles[0].Id, "snapshot shared profiles or lost profile assignment");
        store.Save(settings);
        settings.GameVibrance = 72;
        store.Save(settings);
        var loaded = store.Load()!;
        Assert(loaded.GameVibrance == 72, "latest setting not saved");
        Assert(loaded.SchemaVersion == 2, "settings not upgraded to schema 2");
        Assert(loaded.TrayOnMinimize, "minimize-to-tray option was not saved");
        Assert(loaded.Games[1].VibrancePercent == 90 && loaded.Games[1].RecordingStopDelaySeconds == 20, "per-game overrides were not saved");
        Assert(loaded.ObsWebSocketPort == 4456, "OBS WebSocket port was not saved");
        Assert(loaded.ReplayBufferSeconds == 95 && loaded.SaveClipHotkey == "Ctrl+Shift+F9" && loaded.StutterMarkerHotkey == "Ctrl+Shift+F8" && loaded.Games[1].CaptureMode == CaptureMode.Both,
            "replay settings did not round trip");
        Assert(loaded.Profiles.Count == 1 && loaded.Games[1].ProfileId == loaded.Profiles[0].Id, "profiles did not round trip");
        var legacy = store.Deserialize("{\"SchemaVersion\":2,\"Games\":[{\"Name\":\"Old\",\"ExecutablePath\":\"C:\\\\Games\\\\old.exe\"}]}");
        Assert(legacy.Games[0].Record && legacy.Games[0].CaptureMode is null && EffectiveGameOptionsResolver.Resolve(legacy, legacy.Games[0]).CaptureMode == CaptureMode.Recording && legacy.ReplayBufferSeconds == 60 && legacy.SaveClipHotkey == "Ctrl+Alt+F10" && legacy.StutterMarkerHotkey == "Ctrl+Alt+F9",
            "legacy settings did not receive backward-compatible defaults");
        Assert(File.Exists(store.SettingsPath + ".bak"), "old settings were not backed up");
        Assert(File.ReadAllText(store.SettingsPath + ".bak").Contains("\"GameVibrance\": 80"), "backup was not the previous settings");
        settings.GameVibrance = 73;
        store.Save(settings);
        Assert(store.Load()!.GameVibrance == 73, "a later save failed when a backup already existed");
        Assert(File.ReadAllText(store.SettingsPath + ".bak").Contains("\"GameVibrance\": 72"), "backup was not refreshed");
        return Task.CompletedTask;
    }

    public static Task SettingsExportImport()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);
        var settings = Settings();
        settings.OverlayLeft = double.NaN;
        settings.OverlayTop = double.NaN;
        settings.AppsToCloseOnGameLaunch = ["ChatGPT", "t3-code.exe"];
        settings.Games[0].VibrancePercent = 90;
        settings.Games[0].RecordingStopDelaySeconds = 15;
        settings.Profiles.Add(new GameProfile { Name = "Quiet", RecordingDirectory = temp.Path, OverlayPreset = OverlayPreset.Minimal });
        settings.Games[0].ProfileId = settings.Profiles[0].Id;

        var json = store.Serialize(settings.Snapshot());
        store.Save(settings);
        var fromDisk = store.Load()!;
        Assert(double.IsNaN(fromDisk.OverlayLeft) && double.IsNaN(fromDisk.OverlayTop), "NaN overlay positions were not loaded");
        Assert(fromDisk.AppsToCloseOnGameLaunch.SequenceEqual(settings.AppsToCloseOnGameLaunch), "apps to close were not loaded");

        var imported = store.Deserialize(json);
        Assert(imported.SchemaVersion == SettingsStore.CurrentSchemaVersion, "exported settings were not upgraded");
        Assert(double.IsNaN(imported.OverlayLeft) && double.IsNaN(imported.OverlayTop), "NaN overlay positions were not preserved by export/import");
        Assert(imported.AppsToCloseOnGameLaunch.SequenceEqual(settings.AppsToCloseOnGameLaunch), "apps to close were not preserved by export/import");
        Assert(imported.Games.Count == 1 && imported.Games[0].VibrancePercent == 90 && imported.Games[0].RecordingStopDelaySeconds == 15,
            "game overrides were not preserved by export/import");
        Assert(imported.Profiles.Count == 1 && imported.Games[0].ProfileId == imported.Profiles[0].Id && imported.Profiles[0].OverlayPreset == OverlayPreset.Minimal,
            "profiles were not preserved by export/import");

        var legacy = JsonNode.Parse(json)!;
        legacy["SchemaVersion"] = 1;
        var legacySettings = store.Deserialize(legacy.ToJsonString());
        Assert(legacySettings.SchemaVersion == SettingsStore.CurrentSchemaVersion, "legacy settings were not upgraded");

        var unsupported = JsonNode.Parse(json)!;
        unsupported["SchemaVersion"] = SettingsStore.CurrentSchemaVersion + 1;
        AssertThrows<InvalidDataException>(() => store.Deserialize(unsupported.ToJsonString()));
        AssertThrows<InvalidDataException>(() => store.Deserialize("{ not valid settings"));
        return Task.CompletedTask;
    }

    public static Task ValidateSettings()
    {
        var settings = Settings();
        settings.RecordingEnabled = false;
        settings.Games.Add(new GameRule { Name = "Duplicate", ExecutablePath = GamePath.ToUpperInvariant() });
        settings.DesktopVibrance = 101;
        settings.Games[0].VibrancePercent = 120;
        settings.Games[0].RecordingStopDelaySeconds = -1;
        settings.StutterMarkerHotkey = settings.SaveClipHotkey;
        var errors = SettingsValidation.Validate(settings);
        Assert(errors.Any(e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase)), "duplicate path accepted");
        Assert(errors.Any(e => e.Contains("hotkeys must be different", StringComparison.OrdinalIgnoreCase)), "duplicate global hotkeys accepted");
        Assert(errors.Any(e => e.Contains("Vibrance", StringComparison.OrdinalIgnoreCase)), "invalid vibrance accepted");
        Assert(errors.Any(e => e.Contains("wait time", StringComparison.OrdinalIgnoreCase)), "invalid per-game stop delay accepted");
        settings.Games.RemoveAt(1);
        settings.Games[0].VibrancePercent = null;
        settings.Games[0].RecordingStopDelaySeconds = null;
        settings.DesktopVibrance = 50;
        settings.StutterMarkerHotkey = "Ctrl+Alt+F9";
        Assert(SettingsValidation.Validate(settings).Count == 0, "valid configuration rejected");
        return Task.CompletedTask;
    }

    public static Task VibranceTargetSettings()
    {
        var settings = Settings();
        settings.VibranceTargetMode = VibranceTargetMode.SelectedDisplay;
        settings.VibranceSelectedDisplay = @"\\.\DISPLAY2";
        settings.VibranceOnlyWhenFocused = true;
        var store = new SettingsStore(Path.GetTempPath());
        var copy = store.Deserialize(store.Serialize(settings.Snapshot()));
        Assert(copy.VibranceTargetMode == VibranceTargetMode.SelectedDisplay && copy.VibranceSelectedDisplay == settings.VibranceSelectedDisplay && copy.VibranceOnlyWhenFocused,
            "vibrance targeting settings did not round trip");
        Assert(new AppSettings().VibranceTargetMode == VibranceTargetMode.AllDisplays, "legacy targeting default changed");
        copy.VibranceTargetMode = (VibranceTargetMode)99;
        Assert(SettingsValidation.Validate(copy).Any(e => e.Contains("target", StringComparison.OrdinalIgnoreCase)), "invalid target enum passed validation");
        copy.VibranceTargetMode = VibranceTargetMode.SelectedDisplay;
        copy.VibranceSelectedDisplay = "";
        Assert(SettingsValidation.Validate(copy).Any(e => e.Contains("display", StringComparison.OrdinalIgnoreCase)), "missing selected display passed validation");
        return Task.CompletedTask;
    }

    public static Task ProfileResolution()
    {
        var settings = Settings();
        settings.RecordingDirectory = "global folder";
        settings.ReplayBufferSeconds = 45;
        settings.RecordingStopDelaySeconds = 7;
        settings.GameVibrance = 55;
        settings.GamingPowerPlan = GamingPlan;
        settings.OverlayShowCpu = true;
        settings.OverlayShowGpu = false;
        settings.OverlayShowFps = false;
        settings.OverlayShowLatency = true;
        var profile = new GameProfile
        {
            Name = "Mixed", CaptureMode = CaptureMode.Replay, RecordingDirectory = "profile folder",
            ReplayBufferSeconds = 100, RecordingStopDelaySeconds = 22, VibrancePercent = 67,
            PowerPlanId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), OverlayPreset = OverlayPreset.Fps
        };
        settings.Profiles.Add(profile);
        var game = settings.Games[0];
        game.ProfileId = profile.Id;
        var inherited = EffectiveGameOptionsResolver.Resolve(settings, game);
        Assert(inherited.CaptureMode == CaptureMode.Replay && inherited.RecordingDirectory == "profile folder" && inherited.ReplayBufferSeconds == 100 && inherited.RecordingStopDelaySeconds == 22,
            "capture, directory, replay or stop delay did not inherit profile values");
        Assert(inherited.VibrancePercent == 67 && inherited.PowerPlanId == profile.PowerPlanId,
            "vibrance or power did not inherit profile values");
        game.PowerPlanId = Guid.Empty;
        Assert(EffectiveGameOptionsResolver.Resolve(settings, game).PowerPlanId == settings.GamingPowerPlan,
            "explicit global power fallback did not bypass the profile target");
        game.PowerPlanId = null;
        Assert(!inherited.OverlayShowCpu && !inherited.OverlayShowGpu && inherited.OverlayShowFps && inherited.OverlayShowLatency,
            "FPS overlay preset did not resolve to FPS and frame delay");
        profile.OverlayPreset = OverlayPreset.Minimal;
        var minimal = EffectiveGameOptionsResolver.Resolve(settings, game);
        Assert(!minimal.OverlayShowCpu && !minimal.OverlayShowGpu && minimal.OverlayShowFps && !minimal.OverlayShowLatency,
            "Minimal overlay preset did not resolve to FPS only");
        profile.OverlayPreset = OverlayPreset.Fps;
        Assert(inherited.Record && inherited.UseVibrance, "profile resolution changed explicit game permissions");

        game.CaptureMode = CaptureMode.Both;
        game.RecordingDirectory = "game folder";
        game.ReplayBufferSeconds = 180;
        game.RecordingStopDelaySeconds = 4;
        game.VibrancePercent = 91;
        game.PowerPlanId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        game.OverlayPreset = OverlayPreset.Full;
        var overridden = EffectiveGameOptionsResolver.Resolve(settings, game);
        Assert(overridden.CaptureMode == CaptureMode.Both && overridden.RecordingDirectory == "game folder" && overridden.ReplayBufferSeconds == 180 && overridden.RecordingStopDelaySeconds == 4,
            "game capture values did not take precedence over profile values");
        Assert(overridden.VibrancePercent == 91 && overridden.PowerPlanId == game.PowerPlanId && overridden.OverlayShowCpu && overridden.OverlayShowGpu && overridden.OverlayShowFps && overridden.OverlayShowLatency,
            "game vibrance, power or overlay override did not take precedence");

        game.CaptureMode = null; game.RecordingDirectory = null; game.ReplayBufferSeconds = null; game.RecordingStopDelaySeconds = null;
        game.VibrancePercent = null; game.PowerPlanId = null; game.OverlayPreset = null;
        profile.CaptureMode = null; profile.RecordingDirectory = null; profile.ReplayBufferSeconds = null;
        profile.RecordingStopDelaySeconds = null; profile.VibrancePercent = null; profile.PowerPlanId = null; profile.OverlayPreset = null;
        var global = EffectiveGameOptionsResolver.Resolve(settings, game);
        Assert(global.CaptureMode == CaptureMode.Recording && global.RecordingDirectory == "global folder" && global.ReplayBufferSeconds == 45 && global.RecordingStopDelaySeconds == 7 && global.VibrancePercent == 55 && global.PowerPlanId == GamingPlan,
            "unset profile values did not inherit global defaults");
        Assert(global.OverlayShowCpu && !global.OverlayShowGpu && !global.OverlayShowFps && global.OverlayShowLatency,
            "unset overlay preset did not inherit global visibility options");

        var legacyStore = new SettingsStore(Path.GetTempPath());
        var legacy = legacyStore.Deserialize("{\"SchemaVersion\":2,\"Games\":[{\"Name\":\"Old\",\"ExecutablePath\":\"C:\\\\Games\\\\old.exe\",\"CaptureMode\":1}]}");
        Assert(EffectiveGameOptionsResolver.Resolve(legacy, legacy.Games[0]).CaptureMode == CaptureMode.Replay,
            "legacy numeric capture mode was not retained as an explicit value");
        var absent = legacyStore.Deserialize("{\"SchemaVersion\":2,\"Games\":[{\"Name\":\"Old\",\"ExecutablePath\":\"C:\\\\Games\\\\old.exe\"}]}");
        Assert(EffectiveGameOptionsResolver.Resolve(absent, absent.Games[0]).CaptureMode == CaptureMode.Recording,
            "legacy missing capture mode did not resolve to recording");
        return Task.CompletedTask;
    }

    public static Task ProfileReferencesAndDeletion()
    {
        var settings = Settings();
        var profile = new GameProfile { Name = "Assigned" };
        settings.Profiles.Add(profile);
        settings.Games[0].ProfileId = profile.Id;
        Assert(!GameProfileOperations.TryRemove(settings.Profiles, settings.Games, profile.Id, out var message) && message.Contains("Example"),
            "assigned profile deletion was not refused with the game named");
        settings.Profiles.Add(new GameProfile { Id = profile.Id, Name = "Assigned" });
        settings.Profiles.Add(new GameProfile { Id = Guid.Empty, Name = " " });
        settings.Games[0].ProfileId = Guid.NewGuid();
        var errors = SettingsValidation.Validate(settings);
        Assert(errors.Any(error => error.Contains("profile ID is duplicated", StringComparison.OrdinalIgnoreCase)) &&
               errors.Any(error => error.Contains("profile ID is empty", StringComparison.OrdinalIgnoreCase)) &&
               errors.Any(error => error.Contains("profile name", StringComparison.OrdinalIgnoreCase)) &&
               errors.Any(error => error.Contains("selected profile is missing", StringComparison.OrdinalIgnoreCase)),
            "invalid profile IDs, names or imported references were not actionable");
        settings.Profiles.RemoveAt(2);
        settings.Profiles.RemoveAt(1);
        settings.Games[0].ProfileId = null;
        Assert(GameProfileOperations.TryRemove(settings.Profiles, settings.Games, profile.Id, out _), "unassigned profile could not be deleted");
        return Task.CompletedTask;
    }

    public static Task ProfileDirectoryValidation()
    {
        using var temp = new TempDirectory();
        var config = System.IO.Path.Combine(temp.Path, "obs-config");
        Directory.CreateDirectory(config);
        var executable = System.IO.Path.Combine(temp.Path, "obs.exe");
        File.WriteAllText(executable, "");
        var settings = Settings();
        settings.RecordingEnabled = true;
        settings.ObsExecutablePath = executable;
        settings.ObsConfigRoot = config;
        settings.RecordingDirectory = "";
        settings.Profiles.Add(new GameProfile { Name = "Per-game folder", RecordingDirectory = temp.Path });
        settings.Games[0].ProfileId = settings.Profiles[0].Id;
        Assert(SettingsValidation.Validate(settings).Count == 0,
            "valid profile folder did not satisfy the selected game's output-folder requirement");
        settings.Profiles[0].RecordingDirectory = System.IO.Path.Combine(temp.Path, "missing");
        Assert(SettingsValidation.Validate(settings).Any(error => error.Contains("recording folder", StringComparison.OrdinalIgnoreCase)),
            "missing effective recording folder was not reported");
        return Task.CompletedTask;
    }

    public static Task CaptureModeArguments()
    {
        Assert(ObsRecorder.BuildCaptureArguments(CaptureMode.Recording).SequenceEqual(["--startrecording"]), "recording launch arguments incorrect");
        Assert(ObsRecorder.BuildCaptureArguments(CaptureMode.Replay).SequenceEqual(["--startreplaybuffer"]), "replay launch arguments incorrect");
        Assert(ObsRecorder.BuildCaptureArguments(CaptureMode.Both).SequenceEqual(["--startrecording", "--startreplaybuffer"]), "both launch arguments incorrect");
        return Task.CompletedTask;
    }

    public static Task ObsReplayConfiguration()
    {
        using var fixture = new ObsFixture();
        var originalProfile = File.ReadAllBytes(fixture.Profile);
        var prepared = ObsConfiguration.Prepare(fixture.Root, fixture.Recordings, 123, @"C:\Games\NewGame.exe",
            (_, exe) => new GameWindow(123, "Game", "Class", exe), CaptureMode.Replay, 90);
        prepared.Transaction!.Apply(() => false);
        var applied = File.ReadAllText(fixture.Profile);
        Assert(applied.Contains("RecRB=true") && applied.Contains("RecRBTime=90"), "replay buffer profile options were not set");
        Assert(applied.Contains("RecTracks=15") && applied.Contains("Track4Name=Microphone"), "replay setup changed audio track configuration");
        Assert(prepared.Transaction.TryRollback(() => false, out _), "replay profile rollback failed");
        Assert(originalProfile.SequenceEqual(File.ReadAllBytes(fixture.Profile)), "replay profile rollback was not exact");

        using var simple = new ObsFixture();
        File.WriteAllText(simple.Profile, "[Output]\nMode=Simple\n[SimpleOutput]\nRecQuality=Stream\nRecRB=false\nRecRBTime=20\nRecAudioEncoder=aac\nRecTracks=3\n");
        AssertThrows<InvalidDataException>(() => ObsConfiguration.Prepare(simple.Root, simple.Recordings, 123,
            @"C:\Games\NewGame.exe", (_, exe) => new GameWindow(123, "Game", "Class", exe), CaptureMode.Replay, 60));
        File.WriteAllText(simple.Profile, "[Output]\nMode=Simple\n[SimpleOutput]\nRecRB=false\nRecRBTime=20\n");
        AssertThrows<InvalidDataException>(() => ObsConfiguration.Prepare(simple.Root, simple.Recordings, 123,
            @"C:\Games\NewGame.exe", (_, exe) => new GameWindow(123, "Game", "Class", exe), CaptureMode.Replay, 60));

        var invalid = Settings();
        invalid.ReplayBufferSeconds = 14;
        Assert(SettingsValidation.Validate(invalid).Any(e => e.Contains("Replay buffer length")), "invalid global replay length was accepted");
        invalid.ReplayBufferSeconds = 60;
        invalid.Games[0].CaptureMode = (CaptureMode)99;
        Assert(SettingsValidation.Validate(invalid).Any(e => e.Contains("capture mode")), "invalid per-game capture mode was accepted");
        return Task.CompletedTask;
    }

    public static Task MatchProcesses()
    {
        var settings = Settings();
        var running = new[] { new RunningProcess(1, "game.exe", GamePath.ToUpperInvariant()),
            new RunningProcess(2, "game.exe", null) };
        Assert(AutomationPolicy.MatchGames(settings.Games, running).Count == 1, "selected game was not matched by full path");
        Assert(AutomationPolicy.MatchGames(settings.Games, [running[1]]).Count == 0, "unverified executable was matched by name");
        var root = @"C:\Steam\steamapps\common\Example";
        var catalog = new[] { new SteamGame("Example", root) };
        var processes = new[] { new RunningProcess(1, "game.exe", root + @"\bin\game.exe"),
            new RunningProcess(2, "game.exe", root + @"-other\game.exe"),
            new RunningProcess(3, "CrashReportClient.exe", root + @"\CrashReportClient.exe") };
        Assert(SteamGameDetector.FindRunning(catalog, processes).Count == 1, "game was not detected");
        Assert(SteamGameDetector.FindRunning(catalog, processes.Skip(1).ToArray()).Count == 0, "helper or adjacent path was treated as a game");
        return Task.CompletedTask;
    }

    public static Task ObsRetargetAndRollback()
    {
        using var fixture = new ObsFixture();
        var oldProfile = File.ReadAllBytes(fixture.Profile);
        var oldScene = File.ReadAllBytes(fixture.Scene);
        var prepared = ObsConfiguration.Prepare(fixture.Root, fixture.Recordings, 123, @"C:\Games\NewGame.exe",
            (_, exe) => new GameWindow(123, "A #Title:Here", "UnrealWindow", exe));
        Assert(!prepared.WaitingForWindow && prepared.Transaction is not null, "configuration not prepared");
        prepared.Transaction!.Apply(() => false);
        var profile = File.ReadAllText(fixture.Profile);
        var scene = JsonNode.Parse(File.ReadAllText(fixture.Scene))!;
        var capture = scene["sources"]!.AsArray().First(x => (string?)x!["name"] == "Game Capture")!;
        Assert((string?)capture["settings"]!["window"] == "A #22Title#3AHere:UnrealWindow:NewGame.exe", "target was not correctly escaped");
        Assert((bool?)capture["settings"]!["capture_audio"] == true && (int?)capture["mixers"] == 195, "game audio settings changed");
        var discord = scene["sources"]!.AsArray().First(x => (string?)x!["name"] == "Discord")!;
        Assert((int?)discord["mixers"] == 197, "Discord audio routing changed");
        Assert(profile.Contains("RecTracks=15") && profile.Contains("Track4Name=Microphone"), "recording tracks changed");
        Assert(profile.Contains(fixture.Recordings.Replace("\\", "\\\\")), "recording path not updated");
        Assert(prepared.Transaction.TryRollback(() => false, out _), "rollback failed");
        Assert(oldProfile.SequenceEqual(File.ReadAllBytes(fixture.Profile)), "profile rollback was not exact");
        Assert(oldScene.SequenceEqual(File.ReadAllBytes(fixture.Scene)), "scene rollback was not exact");
        return Task.CompletedTask;
    }

    public static Task DetectPortableObs()
    {
        using var temp = new TempDirectory();
        var install = System.IO.Path.Combine(temp.Path, "obs");
        var bin = System.IO.Path.Combine(install, "bin", "64bit");
        var config = System.IO.Path.Combine(install, "config", "obs-studio");
        var profile = System.IO.Path.Combine(config, "basic", "profiles", "Test");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(profile);
        var executable = System.IO.Path.Combine(bin, "obs64.exe");
        File.WriteAllText(executable, "");
        File.WriteAllText(System.IO.Path.Combine(config, "user.ini"), "[Basic]\nProfileDir=Test\n");
        File.WriteAllText(System.IO.Path.Combine(profile, "basic.ini"), "[Output]\nMode=Advanced\n[AdvOut]\nRecFilePath=D:\\\\Recordings\n");
        var found = ObsDiscovery.FromExecutable(executable);
        Assert(found is not null && found.Portable && found.ConfigRoot == config, "portable config was not detected");
        Assert(found!.RecordingDirectory == @"D:\Recordings", "OBS recording path was not decoded");
        return Task.CompletedTask;
    }

    public static Task ObsWaitsForWindow()
    {
        using var fixture = new ObsFixture();
        var before = File.ReadAllBytes(fixture.Scene);
        var prepared = ObsConfiguration.Prepare(fixture.Root, fixture.Recordings, 123, @"C:\Games\NewGame.exe", (_, _) => null);
        Assert(prepared.WaitingForWindow && prepared.Transaction is null, "should wait for the game window");
        Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Scene)), "scene was modified while waiting");
        return Task.CompletedTask;
    }

    public static Task ObsRejectsUnsafeChanges()
    {
        using (var fixture = new ObsFixture(visible: false))
        {
            AssertThrows<InvalidDataException>(() => ObsConfiguration.Prepare(fixture.Root, fixture.Recordings, 123,
                @"C:\Games\NewGame.exe", (_, exe) => new GameWindow(123, "Game", "Class", exe)));
        }
        using (var fixture = new ObsFixture())
        {
            var originalProfile = File.ReadAllBytes(fixture.Profile);
            var prepared = ObsConfiguration.Prepare(fixture.Root, fixture.Recordings, 123, @"C:\Games\NewGame.exe",
                (_, exe) => new GameWindow(123, "Game", "Class", exe));
            File.AppendAllText(fixture.Scene, " ");
            AssertThrows<IOException>(() => prepared.Transaction!.Apply(() => false));
            Assert(originalProfile.SequenceEqual(File.ReadAllBytes(fixture.Profile)), "concurrent change overwrote the profile");
        }
        return Task.CompletedTask;
    }

    public static async Task AutomationTransitions()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        var platform = new FakePlatform();
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now);
        AutomationStatus? latest = null;
        var lifecycle = new List<AutomationRecordingLifecycle>();
        automation.StatusChanged += status => latest = status;
        automation.RecordingLifecycleChanged += state => lifecycle.Add(state);
        await automation.TickForTestsAsync();
        Assert(platform.Vibrance == 50 && platform.Power == DesktopPlan, "desktop settings incorrect");
        platform.Processes = [new RunningProcess(10, "game.exe", GamePath)];
        await automation.TickForTestsAsync();
        Assert(recorder.Starts == 1 && recorder.Owned && platform.Vibrance == 80 && platform.Power == GamingPlan, "game transition failed");
        Assert(lifecycle.Count == 1 && lifecycle[0].IsOwned && !lifecycle[0].IntentionalStop && lifecycle[0].RequestedMode == CaptureMode.Recording,
            "managed recording start lifecycle did not carry its confirmed requested capture mode");
        Assert(latest?.Recording == "OBS launched (recording requested)" && latest.Headline == "OBS launched", "process liveness was presented as confirmed recording");
        platform.Processes = [];
        await automation.TickForTestsAsync();
        Assert(recorder.Owned && platform.Vibrance == 50 && platform.Power == DesktopPlan, "grace period or desktop restore failed");
        clock.Now = clock.Now.AddSeconds(11);
        await automation.TickForTestsAsync();
        Assert(!recorder.Owned && recorder.Stops == 1, "owned recording did not stop after grace");
        Assert(lifecycle.Count == 3 && lifecycle[1].IsOwned && lifecycle[1].IntentionalStop && !lifecycle[2].IsOwned && lifecycle[2].IntentionalStop,
            "game-end stop intent was not published before stop and completion");
    }

    public static async Task WatchingOffStopIntent()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        settings.AutomationEnabled = true;
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => DateTime.UtcNow);
        var lifecycle = new List<AutomationRecordingLifecycle>();
        automation.RecordingLifecycleChanged += state => lifecycle.Add(state);
        await automation.TickForTestsAsync();
        settings.AutomationEnabled = false;
        automation.UpdateSettings(settings);
        recorder.StopException = new IOException("simulated stop failure");
        try { await automation.TickForTestsAsync(); throw new Exception("expected stop failure"); }
        catch (IOException) { }
        Assert(recorder.Owned && lifecycle.Count == 3 && lifecycle[1].IntentionalStop && !lifecycle[2].IntentionalStop && lifecycle[2].IsOwned,
            "thrown stop request did not release health suppression");
        lifecycle.RemoveRange(1, 2);
        recorder.StopException = null;
        await automation.TickForTestsAsync();
        Assert(recorder.Stops == 2 && !recorder.Owned, "turning watching off did not stop the owned recorder");
        Assert(lifecycle.Count == 3 && lifecycle[1].IsOwned && lifecycle[1].IntentionalStop && !lifecycle[2].IsOwned && lifecycle[2].IntentionalStop,
            "watching-off stop intent was not ordered before the recorder stop");
    }

    public static async Task ExistingObsIsNotStopped()
    {
        using var temp = new TempDirectory();
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder { Result = RecorderStartStatus.AlreadyRunning };
        await using var automation = new AutomationService(Settings(), temp.Path, platform, recorder, () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        platform.Processes = [];
        await automation.TickForTestsAsync();
        Assert(recorder.Stops == 0, "existing OBS was stopped");
    }

    public static async Task RecordingSwitchesGames()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        const string secondPath = @"C:\Games\Second\game2.exe";
        settings.Games.Add(new GameRule { Name = "Second", ExecutablePath = secondPath, Record = true });
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath), new RunningProcess(20, "game2.exe", secondPath)] };
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now);
        await automation.TickForTestsAsync();
        Assert(recorder.LastPid == 10, "first game was not selected");
        platform.Processes = [new RunningProcess(20, "game2.exe", secondPath)];
        await automation.TickForTestsAsync();
        clock.Now = clock.Now.AddSeconds(11);
        await automation.TickForTestsAsync();
        await automation.TickForTestsAsync();
        Assert(recorder.Stops == 1 && recorder.Starts == 2 && recorder.LastPid == 20, "recording did not move to the second game");
    }

    public static async Task AliasRecordingTarget()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        const string alias = @"C:\AltInstall\game.exe";
        settings.Games[0].ExecutableAliases.Add(alias);
        var platform = new FakePlatform { Processes = [new RunningProcess(99, "game.exe", alias)] };
        var recorder = new FakeRecorder();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        Assert(recorder.LastPath == alias && recorder.LastPid == 99, "recorder did not receive the observed alias executable with its PID");
        Assert(automation.RecordingGamePath == GamePath, "recording owner identity changed from the canonical rule path");
        platform.Processes = [new RunningProcess(5, "game.exe", GamePath), new RunningProcess(99, "game.exe", alias)];
        await automation.TickForTestsAsync();
        Assert(recorder.Starts == 1 && recorder.Stops == 0 && automation.RecordingGamePath == GamePath,
            "snapshot reordering or a likely name candidate made the owned recording leave its matching PID");

        var inaccessibleSettings = Settings();
        inaccessibleSettings.Games[0].MatchMode = "name";
        var inaccessiblePlatform = new FakePlatform { Processes = [new RunningProcess(77, "game.exe", null)] };
        var inaccessibleRecorder = new FakeRecorder();
        await using var inaccessibleAutomation = new AutomationService(inaccessibleSettings, temp.Path, inaccessiblePlatform, inaccessibleRecorder, () => DateTime.UtcNow);
        await inaccessibleAutomation.TickForTestsAsync();
        Assert(inaccessibleRecorder.Starts == 0 && inaccessibleAutomation.RecordingGamePath is null,
            "an inaccessible process path was paired with the canonical path for recording");
    }

    public static async Task ClosedObsDoesNotLoop()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now);
        await automation.TickForTestsAsync();
        recorder.Owned = false;
        await automation.TickForTestsAsync();
        clock.Now = clock.Now.AddMinutes(1);
        await automation.TickForTestsAsync();
        Assert(recorder.Starts == 1, "OBS was repeatedly restarted during one game");
        automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(recorder.Starts == 2, "saving settings did not permit a retry");
    }

    public static async Task StartFailureBackoff()
    {
        using var temp = new TempDirectory();
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder { Result = RecorderStartStatus.Failed };
        var clock = new FakeClock();
        await using var automation = new AutomationService(Settings(), temp.Path, platform, recorder, () => clock.Now);
        for (var i = 0; i < 4; i++) { await automation.TickForTestsAsync(); clock.Now = clock.Now.AddSeconds(31); }
        Assert(recorder.Starts == 3, "failed recording attempts were not capped");
    }

    public static async Task ManualChangesAreRespected()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        var custom = Guid.NewGuid();
        platform.Power = custom;
        platform.Vibrance = 65;
        settings.PowerEnabled = false;
        settings.VibranceEnabled = false;
        automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(platform.Power == custom && platform.Vibrance == 65, "manual settings were overwritten when disabling");
    }


    public static Task MatchModes()
    {
        var byName = new GameRule { Name = "ByName", ExecutablePath = @"C:\Other\game.exe", MatchMode = "name" };
        var byFolder = new GameRule { Name = "ByFolder", ExecutablePath = @"C:\Games\Launcher\launcher.exe", MatchMode = "folder" };
        var processes = new[] {
            new RunningProcess(1, "game.exe", @"D:\Somewhere\game.exe"),
            new RunningProcess(2, "child.exe", @"C:\Games\Launcher\bin\child.exe"),
            new RunningProcess(3, "game.exe", null),
            new RunningProcess(4, "child.exe", @"C:\Games\LauncherElse\child.exe")
        };
        var m1 = AutomationPolicy.MatchGames([byName], processes);
        Assert(m1.Count == 1 && m1[0].Process.Id == 1, "name match failed");
        var m2 = AutomationPolicy.MatchGames([byFolder], processes);
        Assert(m2.Count == 1 && m2[0].Process.Id == 2, "folder match or directory boundary failed");
        Assert(AutomationPolicy.MatchGames([new GameRule { Name = "X", ExecutablePath = @"D:\Somewhere\game.exe", MatchMode = "path" }], processes).Count == 1, "path match regressed");
        return Task.CompletedTask;
    }

    public static Task ExecutableAliases()
    {
        var settings = Settings();
        var game = settings.Games[0];
        const string alias = @"C:\AltInstall\game.exe";
        game.ExecutableAliases.Add(alias);
        var snapshot = settings.Snapshot();
        game.ExecutableAliases[0] = @"C:\Changed\other.exe";
        Assert(snapshot.Games[0].ExecutableAliases[0] == alias, "settings snapshot shared mutable aliases");
        game.ExecutableAliases[0] = alias;

        var processes = new[]
        {
            new RunningProcess(40, "game.exe", alias.ToUpperInvariant()),
            new RunningProcess(41, "game.exe", @"C:\AltInstallElse\game.exe"),
            new RunningProcess(42, "game.exe", null),
            new RunningProcess(43, "game.exe", @"C:\Games\Example\game.exe")
        };
        var match = AutomationPolicy.MatchGames([game], processes);
        var allMatches = AutomationPolicy.MatchAllGames([game], processes);
        Assert(match.Count == 1 && match[0].Process.Id == 43 && allMatches.Count == 2 && allMatches[1].Process.Id == 40,
            "primary and case-insensitive exact alias paths did not match predictably");
        var evaluation = AutomationPolicy.EvaluateRule(game, processes, [game]);
        Assert(evaluation.Match?.Process.Id == match[0].Process.Id && evaluation.Match?.MatchKind == GameProcessMatchKind.PrimaryPath,
            "diagnostics disagree with the runtime match policy");
        Assert(evaluation.Processes.Any(item => item.Process.Id == 41 && item.MatchKind is null && item.Reason.Contains("does not equal", StringComparison.OrdinalIgnoreCase)),
            "adjacent directory path was not explained as a mismatch");
        Assert(evaluation.Processes.Any(item => item.Process.Id == 42 && item.MatchKind is null && item.Reason.Contains("inaccessible", StringComparison.OrdinalIgnoreCase)),
            "inaccessible process path was not explained");
        var competing = new GameRule { Name = "Competing", ExecutablePath = @"C:\Else\other.exe", ExecutableAliases = [GamePath] };
        var competingEvaluation = AutomationPolicy.EvaluateRule(game, processes, [game, competing]);
        Assert(competingEvaluation.CompetingGames.Contains("Competing"), "a competing rule matching the selected PID was not diagnosed");
        Assert(AutomationPolicy.ResolveObservedTarget(game, [processes[0]], out _)?.EndsWith(@"AltInstall\game.exe", StringComparison.OrdinalIgnoreCase) == true,
            "performance target did not resolve an alias to the observed executable");
        var ambiguousTarget = AutomationPolicy.ResolveObservedTarget(game, [processes[0], processes[3]], out var ambiguity);
        Assert(ambiguousTarget is null && ambiguity.Contains("different executable paths", StringComparison.OrdinalIgnoreCase),
            "multiple actual executable targets were not marked ambiguous");
        var nameRule = game.Copy();
        nameRule.MatchMode = "name";
        Assert(AutomationPolicy.ResolveObservedTarget(nameRule, [processes[0], processes[2]], out _) is null,
            "a readable target masked another matching process with an inaccessible path");
        Assert(!ExecutablePathRules.IsValidAbsoluteExecutable("C:\\Games\\bad\0.exe") &&
               !ExecutablePathRules.IsValidAbsoluteExecutable(@"C:\Games\*.exe"),
            "malformed executable paths passed validation");

        var store = new SettingsStore(Path.GetTempPath());
        var imported = store.Deserialize(store.Serialize(snapshot));
        Assert(imported.Games[0].ExecutablePath == GamePath && imported.Games[0].ExecutableAliases.SequenceEqual(snapshot.Games[0].ExecutableAliases),
            "canonical game identity or aliases did not round trip");
        imported.Games[0].ExecutableAliases.Add("not-an-absolute-path.exe");
        imported.Games[0].ExecutableAliases.Add(GamePath.ToUpperInvariant());
        imported.Games[0].ExecutableAliases.Add(@"C:\AltInstall\.\game.exe");
        var issues = SettingsValidation.Validate(imported);
        Assert(issues.Any(issue => issue.Contains("full .exe path", StringComparison.OrdinalIgnoreCase)) &&
               issues.Any(issue => issue.Contains("duplicates the primary", StringComparison.OrdinalIgnoreCase)) &&
               issues.Any(issue => issue.Contains("duplicates another", StringComparison.OrdinalIgnoreCase)),
            "invalid, canonical duplicate, or normalized duplicate aliases were not surfaced");
        return Task.CompletedTask;
    }

    public static async Task PerGameOverrides()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        settings.Games[0].VibrancePercent = 95;
        settings.Games[0].RecordingStopDelaySeconds = 0;
        settings.Games[0].CaptureMode = CaptureMode.Replay;
        settings.ReplayBufferSeconds = 90;
        settings.PowerForAllSteamGames = false;
        settings.Games[0].PowerPlanId = GamingPlan; // explicit
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now);
        await automation.TickForTestsAsync();
        Assert(recorder.Owned && platform.Vibrance == 95, "per-game vibrance override not applied");
        Assert(recorder.LastSettings?.CaptureMode == CaptureMode.Replay && recorder.LastSettings.ReplayBufferSeconds == 90,
            "automation did not pass the selected game's capture mode and replay length to the recorder");
        AutomationStatus? replayStatus = null;
        automation.StatusChanged += status => replayStatus = status;
        // A changed notice ensures a status notification while OBS stays owned.
        settings.VibranceEnabled = false;
        automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(replayStatus?.Recording == "OBS launched (replay buffer requested)", "ongoing replay status incorrectly claims full recording");
        platform.Processes = [];
        await automation.TickForTestsAsync();
        Assert(!recorder.Owned, "per-game stop delay override did not apply");
    }

    public static async Task ProfileAutomationValues()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        settings.RecordingDirectory = "global-folder";
        var targetPower = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var profile = new GameProfile
        {
            Name = "Capture profile", CaptureMode = CaptureMode.Both, RecordingDirectory = temp.Path,
            ReplayBufferSeconds = 125, RecordingStopDelaySeconds = 20, VibrancePercent = 73, PowerPlanId = targetPower
        };
        settings.Profiles.Add(profile);
        settings.Games[0].ProfileId = profile.Id;
        settings.Games[0].UseVibrance = true;
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now);
        await automation.TickForTestsAsync();
        Assert(recorder.LastSettings?.CaptureMode == CaptureMode.Both && recorder.LastSettings.RecordingDirectory == temp.Path && recorder.LastSettings.ReplayBufferSeconds == 125,
            "profile mode, folder or replay length did not reach the recorder");
        Assert(platform.Vibrance == 73 && platform.Power == targetPower, "profile vibrance or power target did not reach automation");
        Assert(automation.RecordingGamePath == GamePath, "automation changed the configured game identity");
        platform.Processes = [];
        await automation.TickForTestsAsync();
        clock.Now = clock.Now.AddSeconds(19);
        await automation.TickForTestsAsync();
        Assert(recorder.Owned, "profile stop delay was ignored before its deadline");
        clock.Now = clock.Now.AddSeconds(1);
        await automation.TickForTestsAsync();
        Assert(!recorder.Owned && recorder.Stops == 1, "profile stop delay was not applied");
    }

    public static async Task HdrPausesVibrance()
    {
        var modeType = typeof(HdrStatus).GetNestedType("DISPLAYCONFIG_MODE_INFO", System.Reflection.BindingFlags.NonPublic)!;
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(modeType) == 64, "native HDR mode buffer has the wrong Windows ABI size");
        using var temp = new TempDirectory();
        var settings = Settings();
        settings.DisableVibranceWhenHdr = true;
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)], HdrActive = true };
        var recorder = new FakeRecorder();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        Assert(platform.Vibrance == 50 || platform.VibranceWrites == 0 || platform.Vibrance != 80, "vibrance was not paused/restored for HDR");
        var unknown = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)], HdrUnknown = true };
        await using var guarded = new AutomationService(settings, temp.Path, unknown, new FakeRecorder(), () => DateTime.UtcNow);
        await guarded.TickForTestsAsync();
        Assert(unknown.DisplayValues[@"\\.\DISPLAY1"] == 50, "game vibrance was applied while HDR state was unknown");
    }

    public static async Task SelectedVibranceTarget()
    {
        using var temp = new TempDirectory();
        var settings = Settings(); settings.VibranceTargetMode = VibranceTargetMode.SelectedDisplay; settings.VibranceSelectedDisplay = @"\\.\DISPLAY1";
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        await using var automation = new AutomationService(settings, temp.Path, platform, new FakeRecorder(), () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 80 && platform.DisplayValues[@"\\.\DISPLAY2"] == 50, "selected target changed another display");
        platform.Processes = [];
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 50 && platform.DisplayValues[@"\\.\DISPLAY2"] == 50, "selected display did not restore to desktop vibrance");
    }

    public static async Task FocusedVibrance()
    {
        using var temp = new TempDirectory();
        var settings = Settings(); settings.VibranceTargetMode = VibranceTargetMode.SelectedDisplay; settings.VibranceSelectedDisplay = @"\\.\DISPLAY1"; settings.VibranceOnlyWhenFocused = true;
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)], ForegroundPid = 10 };
        var recorder = new FakeRecorder();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 80 && recorder.Owned, "focused game did not receive vibrance and recording");
        platform.ForegroundPid = null;
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 50 && recorder.Owned && recorder.Starts == 1, "alt-tab did not restore colour independently of recording");
        settings.Games.Add(new GameRule { Name = "Second", ExecutablePath = @"C:\Games\second.exe", VibrancePercent = 95 });
        platform.Processes = [new RunningProcess(10, "game.exe", GamePath), new RunningProcess(20, "second.exe", @"C:\Games\second.exe")];
        platform.GameMonitors[10] = @"\\.\DISPLAY1";
        platform.GameMonitors[20] = @"\\.\DISPLAY2";
        platform.ForegroundPid = 20;
        settings.VibranceTargetMode = VibranceTargetMode.GameWindowDisplay;
        automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 50 && platform.DisplayValues[@"\\.\DISPLAY2"] == 95,
            "focused second game did not supply its own strength and monitor");
        Assert(recorder.Owned && recorder.Starts == 1, "colour focus switched recording ownership");
        settings.Games[0].UseVibrance = settings.Games[1].UseVibrance = false;
        settings.VibranceTargetMode = VibranceTargetMode.SelectedDisplay;
        platform.DisplayValues[@"\\.\DISPLAY1"] = 65;
        automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 65 && platform.DisplayValues[@"\\.\DISPLAY2"] == 50,
            "no permitted colour game changed an unowned screen instead of only restoring previous ownership");
    }

    public static async Task GameWindowVibrance()
    {
        using var temp = new TempDirectory();
        var settings = Settings(); settings.VibranceTargetMode = VibranceTargetMode.GameWindowDisplay;
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        platform.GameMonitors[10] = @"\\.\DISPLAY1";
        await using var automation = new AutomationService(settings, temp.Path, platform, new FakeRecorder(), () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 80 && platform.DisplayValues[@"\\.\DISPLAY2"] == 50, "game display target was not isolated");
        platform.GameMonitors[10] = @"\\.\DISPLAY2";
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 50 && platform.DisplayValues[@"\\.\DISPLAY2"] == 80, "game display move was not followed");
        platform.GameMonitors.Remove(10);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 50 && platform.DisplayValues[@"\\.\DISPLAY2"] == 50, "unavailable game display mapping changed another screen or retained vibrance");
        settings.VibranceTargetMode = VibranceTargetMode.SelectedDisplay; settings.VibranceSelectedDisplay = @"\\.\DISPLAY3";
        automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 50 && platform.DisplayValues[@"\\.\DISPLAY2"] == 50, "disconnected saved selection fell back to another display");
    }

    public static async Task TargetedVibranceRestore()
    {
        using var temp = new TempDirectory();
        var settings = Settings(); settings.VibranceTargetMode = VibranceTargetMode.SelectedDisplay; settings.VibranceSelectedDisplay = @"\\.\DISPLAY1";
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        await using var automation = new AutomationService(settings, temp.Path, platform, new FakeRecorder(), () => DateTime.UtcNow);
        await automation.TickForTestsAsync();
        platform.DisplayValues[@"\\.\DISPLAY1"] = 65; // A user or another app now owns the value.
        platform.Processes = [];
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY1"] == 65, "manual vibrance value was overwritten during restoration");

        platform.Processes = [new RunningProcess(10, "game.exe", GamePath)];
        settings.VibranceSelectedDisplay = @"\\.\DISPLAY2"; automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        platform.TargetedWriteFailures = 1;
        settings.VibranceEnabled = false; automation.UpdateSettings(settings);
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY2"] == 80, "failed restoration unexpectedly changed the display");
        await automation.TickForTestsAsync();
        Assert(platform.DisplayValues[@"\\.\DISPLAY2"] == 50, "failed restoration ownership was not retried");
    }

    public static async Task ClosesAppsOnGameLaunch()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        settings.CloseAppsOnGameLaunch = true;
        settings.AppsToCloseOnGameLaunch = ["ChatGPT", "t3-code.exe"];
        var platform = new FakePlatform { Processes = [new RunningProcess(10, "game.exe", GamePath)] };
        var recorder = new FakeRecorder();
        var clock = new FakeClock();
        await using var automation = new AutomationService(settings, temp.Path, platform, recorder, () => clock.Now);
        await automation.TickForTestsAsync();
        Assert(platform.ClosedApps.Count == 2, "apps were not closed when a game launched");
        await automation.TickForTestsAsync();
        Assert(platform.ClosedApps.Count == 2, "apps were closed more than once during the same game session");
        platform.Processes = [];
        clock.Now = clock.Now.AddSeconds(11);
        await automation.TickForTestsAsync();
        platform.Processes = [new RunningProcess(11, "game.exe", GamePath)];
        await automation.TickForTestsAsync();
        Assert(platform.ClosedApps.Count == 4, "apps were not closed on a later game launch");
    }
    private static AppSettings Settings() => new()
    {
        AutomationEnabled = true, RecordingEnabled = true, VibranceEnabled = true, PowerEnabled = true,
        PowerForAllSteamGames = false, DesktopPowerPlan = DesktopPlan, GamingPowerPlan = GamingPlan,
        Games = [new GameRule { Name = "Example", ExecutablePath = GamePath, Record = true, UseVibrance = true }]
    };

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

internal sealed class FakeClock { public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); }

internal sealed class FakePlatform : IAutomationPlatform
{
    public IReadOnlyList<RunningProcess> Processes { get; set; } = [];
    public Dictionary<string, int> DisplayValues { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\\.\DISPLAY1"] = 50, [@"\\.\DISPLAY2"] = 50
    };
    public int Vibrance { get => DisplayValues[@"\\.\DISPLAY1"]; set => DisplayValues[@"\\.\DISPLAY1"] = value; }
    public int? ForegroundPid { get; set; }
    public Dictionary<int, string> GameMonitors { get; } = [];
    public int TargetedWriteFailures { get; set; }
    public Guid Power { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public bool HdrActive { get; set; }
    public bool HdrUnknown { get; set; }
    public int VibranceWrites { get; private set; }
    public int PowerWrites { get; private set; }
    public IReadOnlyList<RunningProcess> CaptureProcesses() => Processes;
    public IReadOnlyList<LibraryGame> DiscoverLibraryGames(AppDetectionOptions options) => [];
    public IReadOnlyList<LibraryGame> FindRunningLibraryGames(IReadOnlyList<LibraryGame> catalog, IReadOnlyList<RunningProcess> processes) => [];
    public IReadOnlyList<VibranceDisplay> ReadVibrance() => DisplayValues.Select((d, i) => new VibranceDisplay(i, d.Value, d.Key)).ToArray();
    public void SetVibrance(int percent) { foreach (var key in DisplayValues.Keys.ToArray()) DisplayValues[key] = percent; VibranceWrites++; }
    public void SetVibrance(int percent, string deviceName)
    {
        if (TargetedWriteFailures > 0) { TargetedWriteFailures--; throw new IOException("temporary display write failure"); }
        if (!DisplayValues.ContainsKey(deviceName)) throw new InvalidOperationException("display unavailable");
        DisplayValues[deviceName] = percent; VibranceWrites++;
    }
    public int? GetForegroundProcessId() => ForegroundPid;
    public string? GetVisibleGameMonitorIdentity(int processId) => GameMonitors.TryGetValue(processId, out var identity) ? identity : null;
    public void ResetVibrance() { }
    public Guid GetActivePowerPlan() => Power;
    public void SetPowerPlan(Guid id) { Power = id; PowerWrites++; }
    public bool IsHdrActive() => HdrActive;
    public bool? GetHdrState() => HdrUnknown ? null : HdrActive;
    public int CloseApplications(IEnumerable<string> targets) { var items = targets.ToList(); ClosedApps.AddRange(items); return items.Count; }
    public List<string> ClosedApps { get; } = [];
    public void Dispose() { }
}

internal sealed class FakeRecorder : IRecordingController
{
    public bool Owned { get; set; }
    public int Starts { get; private set; }
    public int LastPid { get; private set; }
    public string? LastPath { get; private set; }
    public int Stops { get; private set; }
    public ObsSettings? LastSettings { get; private set; }
    public RecorderStartStatus Result { get; set; } = RecorderStartStatus.Started;
    public bool IsOwnedRecording => Owned;
    public Exception? StopException { get; set; }
    public RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath)
    {
        LastSettings = settings;
        Starts++;
        LastPid = gamePid;
        LastPath = gameExePath;
        Owned = Result == RecorderStartStatus.Started;
        return new RecorderStartResult(Result, Result.ToString());
    }
    public Task<bool> StopOwnedAsync()
    {
        Stops++;
        if (StopException is not null) return Task.FromException<bool>(StopException);
        Owned = false;
        return Task.FromResult(true);
    }
    public void Dispose() { }
}

internal sealed class ObsFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    public string Root => _temp.Path;
    public string Recordings { get; }
    public string Profile { get; }
    public string Scene { get; }
    public ObsFixture(bool visible = true)
    {
        Recordings = System.IO.Path.Combine(Root, "recordings");
        Profile = System.IO.Path.Combine(Root, "basic", "profiles", "Test", "basic.ini");
        Scene = System.IO.Path.Combine(Root, "basic", "scenes", "Test.json");
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Profile)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Scene)!);
        File.WriteAllText(System.IO.Path.Combine(Root, "user.ini"),
            "[BasicWindow]\nPreviewProgramMode=false\n[Basic]\nProfileDir=Test\nSceneCollectionFile=Test.json\n");
        File.WriteAllText(Profile, "[Output]\nMode=Advanced\n[SimpleOutput]\nFilePath=C:\\\\Old\n[AdvOut]\nRecType=Standard\nRecFilePath=C:\\\\Old\nRecTracks=15\nTrack4Name=Microphone\n");
        File.WriteAllText(Scene, """
            {"current_scene":"Main","current_program_scene":"Main","sources":[
              {"id":"game_capture","name":"Game Capture","uuid":"game","mixers":195,
               "settings":{"window":"Old:Class:Old.exe","capture_mode":"window","capture_audio":true}},
              {"id":"wasapi_process_output_capture","name":"Discord","mixers":197,"settings":{"window":"Discord:Class:Discord.exe"}},
              {"id":"scene","name":"Main","uuid":"main","settings":{"items":[
                {"name":"Game Capture","source_uuid":"game","visible":VISIBILITY}]}}
            ]}
            """.Replace("VISIBILITY", visible ? "true" : "false", StringComparison.Ordinal));
    }
    public void Dispose() => _temp.Dispose();
}

internal sealed class TempDirectory : IDisposable
{
    private readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
    public string Path { get; }
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(_parent, "JamesTools-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(_parent, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(resolved).StartsWith("JamesTools-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a directory outside the test's temporary root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
