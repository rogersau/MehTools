namespace JamesTools.Core.Configuration;

public static class SettingsValidation
{
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();
        if (settings.PollSeconds is < 2 or > 30) errors.Add("Check interval must be between 2 and 30 seconds.");
        if (settings.RecordingStopDelaySeconds is < 0 or > 120) errors.Add("Wait time before stopping must be between 0 and 120 seconds.");
        if (settings.DesktopVibrance is < 0 or > 100 || settings.GameVibrance is < 0 or > 100)
            errors.Add("Vibrance must be between 0 and 100%.");
        if (settings.RecordingEnabled)
        {
            if (!File.Exists(settings.ObsExecutablePath)) errors.Add("Choose the OBS program file.");
            if (!Directory.Exists(settings.ObsConfigRoot)) errors.Add("Choose the OBS settings folder.");
            if (!Directory.Exists(settings.RecordingDirectory)) errors.Add("Choose a folder to save videos.");
        }
        if (settings.PowerEnabled && (settings.DesktopPowerPlan == Guid.Empty || settings.GamingPowerPlan == Guid.Empty))
            errors.Add("Choose a normal plan and a gaming plan.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in settings.Games)
        {
            if (string.IsNullOrWhiteSpace(game.Name)) errors.Add("Every game needs a name.");
            if (!Path.IsPathFullyQualified(game.ExecutablePath) ||
                !string.Equals(Path.GetExtension(game.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                errors.Add($"Choose a full executable path for {game.Name}.");
            else if (!paths.Add(game.ExecutablePath)) errors.Add($"{game.Name} has a duplicate executable path.");
        }
        return errors;
    }
}
