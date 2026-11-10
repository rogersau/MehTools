using System.Collections.ObjectModel;

namespace JamesTools.Core.Configuration;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool AutomationEnabled { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int PollSeconds { get; set; } = 4;
    public ObservableCollection<GameRule> Games { get; set; } = [];

    public bool RecordingEnabled { get; set; }
    public string ObsExecutablePath { get; set; } = "";
    public string ObsConfigRoot { get; set; } = "";
    public string RecordingDirectory { get; set; } = "";
    public bool ObsPortable { get; set; } = true;
    public int RecordingStopDelaySeconds { get; set; } = 10;

    public bool VibranceEnabled { get; set; }
    public int DesktopVibrance { get; set; } = 50;
    public int GameVibrance { get; set; } = 80;

    public bool PowerEnabled { get; set; }
    public bool PowerForAllSteamGames { get; set; } = true;
    public Guid DesktopPowerPlan { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public Guid GamingPowerPlan { get; set; } = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public AppSettings Snapshot() => new()
    {
        SchemaVersion = SchemaVersion,
        AutomationEnabled = AutomationEnabled,
        StartWithWindows = StartWithWindows,
        MinimizeToTray = MinimizeToTray,
        PollSeconds = PollSeconds,
        Games = new ObservableCollection<GameRule>(Games.Select(game => game.Copy())),
        RecordingEnabled = RecordingEnabled,
        ObsExecutablePath = ObsExecutablePath,
        ObsConfigRoot = ObsConfigRoot,
        RecordingDirectory = RecordingDirectory,
        ObsPortable = ObsPortable,
        RecordingStopDelaySeconds = RecordingStopDelaySeconds,
        VibranceEnabled = VibranceEnabled,
        DesktopVibrance = DesktopVibrance,
        GameVibrance = GameVibrance,
        PowerEnabled = PowerEnabled,
        PowerForAllSteamGames = PowerForAllSteamGames,
        DesktopPowerPlan = DesktopPowerPlan,
        GamingPowerPlan = GamingPowerPlan
    };
}

public sealed class GameRule
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public bool Record { get; set; } = true;
    public bool UseVibrance { get; set; } = true;

    public GameRule Copy() => new()
    {
        Name = Name,
        ExecutablePath = ExecutablePath,
        Record = Record,
        UseVibrance = UseVibrance
    };
}
