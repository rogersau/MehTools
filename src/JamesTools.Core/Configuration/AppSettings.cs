using System.Collections.ObjectModel;

namespace JamesTools.Core.Configuration;

public sealed class AppSettings
{
    public JamesTools.Core.Diagnostics.DiagnosticOptions Diagnostics { get; set; } = new();
    public SteamHighlightSettings SteamHighlights { get; set; } = new();
    public int SchemaVersion { get; set; } = 2;
    public bool AutomationEnabled { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayOnMinimize { get; set; }
    public int PollSeconds { get; set; } = 4;
    public ObservableCollection<GameRule> Games { get; set; } = [];

    public string MediaToolsFolder { get; set; } = "";
    public bool RecordingEnabled { get; set; }
    public string ObsExecutablePath { get; set; } = "";
    public string ObsConfigRoot { get; set; } = "";
    public string RecordingDirectory { get; set; } = "";
    public bool ObsPortable { get; set; } = true;
    public int ObsWebSocketPort { get; set; } = 4455;
    public int RecordingStopDelaySeconds { get; set; } = 10;
    public int ReplayBufferSeconds { get; set; } = 60;
    public bool OrganiseCapturesByGameAndDate { get; set; }
    public bool LowStorageWarningsEnabled { get; set; }
    public double LowStorageThresholdGiB { get; set; } = 10;

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

    // Performance
    public bool NetworkDiagnosticsEnabled { get; set; }
    public string NetworkTargets { get; set; } = "";
    public string PresentMonPath { get; set; } = "";
    public string SaveClipHotkey { get; set; } = "Ctrl+Alt+F10";
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

    public AppSettings Snapshot() => new()
    {
        Diagnostics = (Diagnostics ?? new()).Copy(),
        SteamHighlights = (SteamHighlights ?? new()).Copy(),
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
        RecordingEnabled = RecordingEnabled,
        ObsExecutablePath = ObsExecutablePath,
        ObsConfigRoot = ObsConfigRoot,
        RecordingDirectory = RecordingDirectory,
        ObsPortable = ObsPortable,
        ObsWebSocketPort = ObsWebSocketPort,
        RecordingStopDelaySeconds = RecordingStopDelaySeconds,
        ReplayBufferSeconds = ReplayBufferSeconds,
        OrganiseCapturesByGameAndDate = OrganiseCapturesByGameAndDate,
        LowStorageWarningsEnabled = LowStorageWarningsEnabled,
        LowStorageThresholdGiB = LowStorageThresholdGiB,
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
        SaveClipHotkey = SaveClipHotkey,
        StutterMarkerHotkey = StutterMarkerHotkey,
        DetectEpicGames = DetectEpicGames,
        DetectGogGames = DetectGogGames,
        DetectEaGames = DetectEaGames,
        DetectUbisoftGames = DetectUbisoftGames,
        DetectXboxGames = DetectXboxGames,
        OpenRecordingsAfterStop = OpenRecordingsAfterStop,
        CloseAppsOnGameLaunch = CloseAppsOnGameLaunch,
        AppsToCloseOnGameLaunch = new ObservableCollection<string>(AppsToCloseOnGameLaunch.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()))
    };
}

public sealed class GameRule
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public bool Record { get; set; } = true;
    /// <summary>Null preserves the legacy Steam highlight setting for WARDOGS.</summary>
    public bool? SteamAutoHighlights { get; set; }
    /// <summary>Null inherits the global recording default.</summary>
    public CaptureMode? CaptureMode { get; set; }
    public bool UseVibrance { get; set; } = true;
    /// <summary>null = use global game vibrance.</summary>
    public int? VibrancePercent { get; set; }
    /// <summary>null = use global gaming plan; Guid.Empty = follow global behavior.</summary>
    public Guid? PowerPlanId { get; set; }
    /// <summary>null = use global recording stop delay.</summary>
    public int? RecordingStopDelaySeconds { get; set; }
    public string? RecordingDirectory { get; set; }
    public int? ReplayBufferSeconds { get; set; }
    /// <summary>Matching mode: "path" (default), "name", or "folder".</summary>
    public string MatchMode { get; set; } = "path";

    public GameRule Copy() => new()
    {
        Name = Name,
        ExecutablePath = ExecutablePath,
        Record = Record,
        SteamAutoHighlights = SteamAutoHighlights,
        CaptureMode = CaptureMode,
        UseVibrance = UseVibrance,
        VibrancePercent = VibrancePercent,
        PowerPlanId = PowerPlanId,
        RecordingStopDelaySeconds = RecordingStopDelaySeconds,
        RecordingDirectory = RecordingDirectory,
        ReplayBufferSeconds = ReplayBufferSeconds,
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
