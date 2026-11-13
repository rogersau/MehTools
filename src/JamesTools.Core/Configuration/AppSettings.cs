using System.Collections.ObjectModel;

namespace JamesTools.Core.Configuration;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool AutomationEnabled { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayOnMinimize { get; set; }
    public int PollSeconds { get; set; } = 4;
    public ObservableCollection<GameRule> Games { get; set; } = [];
    public ObservableCollection<GameProfile> Profiles { get; set; } = [];

    public string MediaToolsFolder { get; set; } = "";
    public bool RecordingEnabled { get; set; }
    public string ObsExecutablePath { get; set; } = "";
    public string ObsConfigRoot { get; set; } = "";
    public string RecordingDirectory { get; set; } = "";
    public bool ObsPortable { get; set; } = true;
    public int ObsWebSocketPort { get; set; } = 4455;
    public int RecordingStopDelaySeconds { get; set; } = 10;
    public int ReplayBufferSeconds { get; set; } = 60;
    public bool RecordingHealthAlertsEnabled { get; set; }
    public bool RecordingHealthSoundEnabled { get; set; }
    public int RecordingHealthStallSeconds { get; set; } = 30;
    public bool OrganiseCapturesByGameAndDate { get; set; }
    public bool LowStorageWarningsEnabled { get; set; }
    public double LowStorageThresholdGiB { get; set; } = 10;
    /// <summary>Optional age limit used only to suggest cleanup candidates.</summary>
    public int? CleanupSuggestionAgeDays { get; set; }
    /// <summary>Optional storage budget used only to suggest cleanup candidates (GiB).</summary>
    public double? CleanupSuggestionBudgetGiB { get; set; }

    public bool VibranceEnabled { get; set; }
    public int DesktopVibrance { get; set; } = 50;
    public int GameVibrance { get; set; } = 80;
    public bool DisableVibranceWhenHdr { get; set; }
    public VibranceTargetMode VibranceTargetMode { get; set; } = VibranceTargetMode.AllDisplays;
    public string VibranceSelectedDisplay { get; set; } = "";
    public bool VibranceOnlyWhenFocused { get; set; }

    public bool PowerEnabled { get; set; }
    public bool PowerForAllSteamGames { get; set; } = true;
    public Guid DesktopPowerPlan { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public Guid GamingPowerPlan { get; set; } = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public bool ShowNotifications { get; set; } = true;

    // Performance / overlay
    public bool NetworkDiagnosticsEnabled { get; set; }
    public string NetworkTargets { get; set; } = "";
    public string PresentMonPath { get; set; } = "";
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowFps { get; set; } = true;
    public bool OverlayShowLatency { get; set; } = true;
    public double OverlayOpacity { get; set; } = 0.92;
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public bool OverlayClickThrough { get; set; }
    public string OverlayHotkey { get; set; } = ""; // e.g. Ctrl+Alt+O
    public string SaveClipHotkey { get; set; } = "Ctrl+Alt+F10";
    public bool RollingDiagnosticsEnabled { get; set; }
    public int RollingBeforeSeconds { get; set; } = 60;
    public int RollingAfterSeconds { get; set; } = 15;
    public string RollingHotkey { get; set; } = "Ctrl+Alt+F7";
    public string BookmarkHotkey { get; set; } = "Ctrl+Alt+F8";
    public string StutterMarkerHotkey { get; set; } = "Ctrl+Alt+F9";

    // Detection
    public bool DetectEpicGames { get; set; }
    public bool DetectGogGames { get; set; }
    public bool DetectEaGames { get; set; }
    public bool DetectUbisoftGames { get; set; }
    public bool DetectXboxGames { get; set; }

    // Actions
    public bool OpenRecordingsAfterStop { get; set; }
    public bool CloseAppsOnGameLaunch { get; set; }
    public ObservableCollection<string> AppsToCloseOnGameLaunch { get; set; } = [];
    public string LastOverlayGamePath { get; set; } = "";

    public AppSettings Snapshot() => new()
    {
        SchemaVersion = SchemaVersion,
        NetworkDiagnosticsEnabled = NetworkDiagnosticsEnabled,
        NetworkTargets = NetworkTargets,
        MediaToolsFolder = MediaToolsFolder,
        AutomationEnabled = AutomationEnabled,
        StartWithWindows = StartWithWindows,
        MinimizeToTray = MinimizeToTray,
        TrayOnMinimize = TrayOnMinimize,
        PollSeconds = PollSeconds,
        Games = new ObservableCollection<GameRule>(Games.Select(game => game.Copy())),
        Profiles = new ObservableCollection<GameProfile>(Profiles.Select(profile => profile.Copy())),
        RecordingEnabled = RecordingEnabled,
        ObsExecutablePath = ObsExecutablePath,
        ObsConfigRoot = ObsConfigRoot,
        RecordingDirectory = RecordingDirectory,
        ObsPortable = ObsPortable,
        ObsWebSocketPort = ObsWebSocketPort,
        RecordingStopDelaySeconds = RecordingStopDelaySeconds,
        ReplayBufferSeconds = ReplayBufferSeconds,
        RecordingHealthAlertsEnabled = RecordingHealthAlertsEnabled,
        RecordingHealthSoundEnabled = RecordingHealthSoundEnabled,
        RecordingHealthStallSeconds = RecordingHealthStallSeconds,
        OrganiseCapturesByGameAndDate = OrganiseCapturesByGameAndDate,
        LowStorageWarningsEnabled = LowStorageWarningsEnabled,
        LowStorageThresholdGiB = LowStorageThresholdGiB,
        CleanupSuggestionAgeDays = CleanupSuggestionAgeDays,
        CleanupSuggestionBudgetGiB = CleanupSuggestionBudgetGiB,
        VibranceEnabled = VibranceEnabled,
        DesktopVibrance = DesktopVibrance,
        GameVibrance = GameVibrance,
        DisableVibranceWhenHdr = DisableVibranceWhenHdr,
        VibranceTargetMode = VibranceTargetMode,
        VibranceSelectedDisplay = VibranceSelectedDisplay,
        VibranceOnlyWhenFocused = VibranceOnlyWhenFocused,
        PowerEnabled = PowerEnabled,
        PowerForAllSteamGames = PowerForAllSteamGames,
        DesktopPowerPlan = DesktopPowerPlan,
        GamingPowerPlan = GamingPowerPlan,
        ShowNotifications = ShowNotifications,
        PresentMonPath = PresentMonPath,
        OverlayShowCpu = OverlayShowCpu,
        OverlayShowGpu = OverlayShowGpu,
        OverlayShowFps = OverlayShowFps,
        OverlayShowLatency = OverlayShowLatency,
        OverlayOpacity = OverlayOpacity,
        OverlayLeft = OverlayLeft,
        OverlayTop = OverlayTop,
        OverlayClickThrough = OverlayClickThrough,
        OverlayHotkey = OverlayHotkey,
        SaveClipHotkey = SaveClipHotkey,
        StutterMarkerHotkey = StutterMarkerHotkey,
        RollingDiagnosticsEnabled = RollingDiagnosticsEnabled,
        RollingBeforeSeconds = RollingBeforeSeconds,
        RollingAfterSeconds = RollingAfterSeconds,
        RollingHotkey = RollingHotkey,
        BookmarkHotkey = BookmarkHotkey,
        DetectEpicGames = DetectEpicGames,
        DetectGogGames = DetectGogGames,
        DetectEaGames = DetectEaGames,
        DetectUbisoftGames = DetectUbisoftGames,
        DetectXboxGames = DetectXboxGames,
        OpenRecordingsAfterStop = OpenRecordingsAfterStop,
        CloseAppsOnGameLaunch = CloseAppsOnGameLaunch,
        AppsToCloseOnGameLaunch = new ObservableCollection<string>(AppsToCloseOnGameLaunch.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())),
        LastOverlayGamePath = LastOverlayGamePath
    };
}

public sealed class GameRule
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    /// <summary>Additional exact executable paths for this game. ExecutablePath remains its identity.</summary>
    public ObservableCollection<string> ExecutableAliases { get; set; } = [];
    public bool Record { get; set; } = true;
    /// <summary>Null inherits from the selected profile or global recording default.</summary>
    public CaptureMode? CaptureMode { get; set; }
    public bool UseVibrance { get; set; } = true;
    /// <summary>null = use global game vibrance.</summary>
    public int? VibrancePercent { get; set; }
    /// <summary>null = use global gaming plan / all-Steam behavior; Guid.Empty = follow global behavior.</summary>
    public Guid? PowerPlanId { get; set; }
    /// <summary>null = use global recording stop delay.</summary>
    public int? RecordingStopDelaySeconds { get; set; }
    public Guid? ProfileId { get; set; }
    public string? RecordingDirectory { get; set; }
    public int? ReplayBufferSeconds { get; set; }
    public OverlayPreset? OverlayPreset { get; set; }
    /// <summary>Additional matching: "path" (default), "name", or "folder".</summary>
    public string MatchMode { get; set; } = "path";

    public GameRule Copy() => new()
    {
        Name = Name,
        ExecutablePath = ExecutablePath,
        ExecutableAliases = new ObservableCollection<string>(ExecutableAliases ?? []),
        Record = Record,
        CaptureMode = CaptureMode,
        UseVibrance = UseVibrance,
        VibrancePercent = VibrancePercent,
        PowerPlanId = PowerPlanId,
        RecordingStopDelaySeconds = RecordingStopDelaySeconds,
        ProfileId = ProfileId,
        RecordingDirectory = RecordingDirectory,
        ReplayBufferSeconds = ReplayBufferSeconds,
        OverlayPreset = OverlayPreset,
        MatchMode = MatchMode
    };
}

/// <summary>Windows executable path helpers used by validation and exact process matching.</summary>
public static class ExecutablePathRules
{
    public static bool IsValidAbsoluteExecutable(string? value)
        => Normalize(value) is not null;

    public static string? Normalize(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var path = value.Trim();
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var full = Path.GetFullPath(path);
            var relative = full[(Path.GetPathRoot(full)?.Length ?? 0)..];
            if (relative.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))) return null;
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    public static bool EqualsPath(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft is not null && normalizedRight is not null &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
}

public enum CaptureMode { Recording, Replay, Both }
public enum VibranceTargetMode { AllDisplays, SelectedDisplay, GameWindowDisplay }
public enum OverlayPreset { Minimal, Fps, Full }

/// <summary>A reusable set of optional values. Null fields inherit global settings.</summary>
public sealed class GameProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public CaptureMode? CaptureMode { get; set; }
    public string? RecordingDirectory { get; set; }
    public int? ReplayBufferSeconds { get; set; }
    public int? RecordingStopDelaySeconds { get; set; }
    public int? VibrancePercent { get; set; }
    public Guid? PowerPlanId { get; set; }
    public OverlayPreset? OverlayPreset { get; set; }

    public GameProfile Copy() => new()
    {
        Id = Id, Name = Name, CaptureMode = CaptureMode, RecordingDirectory = RecordingDirectory,
        ReplayBufferSeconds = ReplayBufferSeconds, RecordingStopDelaySeconds = RecordingStopDelaySeconds,
        VibrancePercent = VibrancePercent, PowerPlanId = PowerPlanId, OverlayPreset = OverlayPreset
    };
}
