using System.Text.Json.Nodes;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;

var tests = new (string Name, Func<Task> Run)[]
{
    ("settings round trip and snapshot isolation", Tests.SettingsRoundTrip),
    ("settings validation and duplicate paths", Tests.ValidateSettings),
    ("exact game matching and Steam path boundaries", Tests.MatchProcesses),
    ("detects a selected portable OBS configuration", Tests.DetectPortableObs),
    ("OBS retarget preserves tracks and rolls back", Tests.ObsRetargetAndRollback),
    ("OBS waits for a window without changing files", Tests.ObsWaitsForWindow),
    ("OBS refuses hidden capture and concurrent edits", Tests.ObsRejectsUnsafeChanges),
    ("automation transitions and recording grace period", Tests.AutomationTransitions),
    ("recording follows a second selected game", Tests.RecordingSwitchesGames),
    ("automation does not own an existing OBS", Tests.ExistingObsIsNotStopped),
    ("automation stops retrying after OBS closes", Tests.ClosedObsDoesNotLoop),
    ("automation retries startup failures with a limit", Tests.StartFailureBackoff),
    ("manual changes are respected when disabling automation", Tests.ManualChangesAreRespected),
    ("performance report captures samples without exposing paths", Tests.PerformanceReport),
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
        await Task.Delay(TimeSpan.FromSeconds(5));
        var report = await logger.StopAsync();
        Assert(report == await logger.StopAsync(), "stopping twice changed the report");
        Assert(report.Contains("Total CPU:") && !report.Contains("Total CPU: unavailable"), "CPU samples missing");
        Assert(report.Contains("Physical memory used:") && !report.Contains("Physical memory used: unavailable"), "memory samples missing");
        Assert(report.Contains("Selected processes working set:") && !report.Contains("Selected processes working set: unavailable"), "selected process samples missing");
        Assert(!report.Contains(executable, StringComparison.OrdinalIgnoreCase), "full executable path leaked into report");
        Assert(report.Contains("FPS, frame times") && report.Contains("not measured"), "measurement limitations missing");
    }

    public static Task SettingsRoundTrip()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);
        var settings = Settings();
        settings.TrayOnMinimize = true;
        settings.Games.Add(new GameRule { Name = "Second", ExecutablePath = @"C:\Games\two.exe" });
        var copy = settings.Snapshot();
        settings.Games[0].Name = "Changed";
        Assert(copy.Games[0].Name == "Example", "snapshot shared a mutable game");
        store.Save(settings);
        settings.GameVibrance = 72;
        store.Save(settings);
        Assert(store.Load()!.GameVibrance == 72, "latest setting not saved");
        Assert(store.Load()!.TrayOnMinimize, "minimize-to-tray option was not saved");
        Assert(File.Exists(store.SettingsPath + ".bak"), "old settings were not backed up");
        Assert(File.ReadAllText(store.SettingsPath + ".bak").Contains("\"GameVibrance\": 80"), "backup was not the previous settings");
        settings.GameVibrance = 73;
        store.Save(settings);
        Assert(store.Load()!.GameVibrance == 73, "a later save failed when a backup already existed");
        Assert(File.ReadAllText(store.SettingsPath + ".bak").Contains("\"GameVibrance\": 72"), "backup was not refreshed");
        return Task.CompletedTask;
    }

    public static Task ValidateSettings()
    {
        var settings = Settings();
        settings.RecordingEnabled = false;
        settings.Games.Add(new GameRule { Name = "Duplicate", ExecutablePath = GamePath.ToUpperInvariant() });
        settings.DesktopVibrance = 101;
        var errors = SettingsValidation.Validate(settings);
        Assert(errors.Any(e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase)), "duplicate path accepted");
        Assert(errors.Any(e => e.Contains("Vibrance", StringComparison.OrdinalIgnoreCase)), "invalid vibrance accepted");
        settings.Games.RemoveAt(1);
        settings.DesktopVibrance = 50;
        Assert(SettingsValidation.Validate(settings).Count == 0, "valid configuration rejected");
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
        await automation.TickForTestsAsync();
        Assert(platform.Vibrance == 50 && platform.Power == DesktopPlan, "desktop settings incorrect");
        platform.Processes = [new RunningProcess(10, "game.exe", GamePath)];
        await automation.TickForTestsAsync();
        Assert(recorder.Starts == 1 && recorder.Owned && platform.Vibrance == 80 && platform.Power == GamingPlan, "game transition failed");
        platform.Processes = [];
        await automation.TickForTestsAsync();
        Assert(recorder.Owned && platform.Vibrance == 50 && platform.Power == DesktopPlan, "grace period or desktop restore failed");
        clock.Now = clock.Now.AddSeconds(11);
        await automation.TickForTestsAsync();
        Assert(!recorder.Owned && recorder.Stops == 1, "owned recording did not stop after grace");
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
    public int Vibrance { get; set; } = 50;
    public Guid Power { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public int VibranceWrites { get; private set; }
    public int PowerWrites { get; private set; }
    public IReadOnlyList<RunningProcess> CaptureProcesses() => Processes;
    public IReadOnlyList<SteamGame> DiscoverSteamGames() => [];
    public IReadOnlyList<SteamGame> FindRunningSteamGames(IReadOnlyList<SteamGame> catalog, IReadOnlyList<RunningProcess> processes) => [];
    public IReadOnlyList<VibranceDisplay> ReadVibrance() => [new VibranceDisplay(0, Vibrance)];
    public void SetVibrance(int percent) { Vibrance = percent; VibranceWrites++; }
    public void ResetVibrance() { }
    public Guid GetActivePowerPlan() => Power;
    public void SetPowerPlan(Guid id) { Power = id; PowerWrites++; }
    public void Dispose() { }
}

internal sealed class FakeRecorder : IRecordingController
{
    public bool Owned { get; set; }
    public int Starts { get; private set; }
    public int LastPid { get; private set; }
    public int Stops { get; private set; }
    public RecorderStartStatus Result { get; set; } = RecorderStartStatus.Started;
    public bool IsOwnedRecording => Owned;
    public RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath)
    {
        Starts++;
        LastPid = gamePid;
        Owned = Result == RecorderStartStatus.Started;
        return new RecorderStartResult(Result, Result.ToString());
    }
    public Task<bool> StopOwnedAsync() { Stops++; Owned = false; return Task.FromResult(true); }
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
