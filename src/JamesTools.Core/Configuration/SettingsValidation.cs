namespace JamesTools.Core.Configuration;

public static class SettingsValidation
{
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();
        try { settings.Diagnostics.Validate(); } catch (ArgumentException ex) { errors.Add(ex.Message); }
        if (settings.SteamHighlights is not { } steam || !SteamHighlightSettings.ValidPadding(steam.BeforeSeconds, steam.AfterSeconds))
            errors.Add("Steam highlights need 0–60 seconds before and after each event, with at least one side above zero.");
        if (settings.SteamHighlights is { } steamFolders)
        {
            if (steamFolders.IncludedCategories == 0 && settings.Games.Any(steamFolders.AutoExportFor))
                errors.Add("Choose at least one event type for automatic Steam highlights.");
            if (!string.IsNullOrWhiteSpace(steamFolders.RecordingFolder) && !SteamHighlightSettings.IsLocalFolder(steamFolders.RecordingFolder.Trim()))
                errors.Add("Steam recording folder must be an absolute local path, or blank to auto-detect.");
            if (!string.IsNullOrWhiteSpace(steamFolders.OutputFolder) && !SteamHighlightSettings.IsLocalFolder(steamFolders.OutputFolder.Trim()))
                errors.Add("Steam highlight output folder must be an absolute local path, or blank to use Videos.");
        }
        try { Diagnostics.NetworkProbeOptions.Create(settings.NetworkDiagnosticsEnabled, settings.NetworkTargets); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }
        if (!string.IsNullOrWhiteSpace(settings.MediaToolsFolder) && (!Path.IsPathFullyQualified(settings.MediaToolsFolder) || !Directory.Exists(settings.MediaToolsFolder)))
            errors.Add("Media tools folder must be an existing absolute folder, or blank for bundled tools/PATH.");
        if (settings.PollSeconds is < 2 or > 30) errors.Add("Check interval must be between 2 and 30 seconds.");
        if (settings.RecordingStopDelaySeconds is < 0 or > 120) errors.Add("Wait time before stopping must be between 0 and 120 seconds.");
        if (settings.ReplayBufferSeconds is < 15 or > 600) errors.Add("Replay buffer length must be between 15 and 600 seconds.");
        if (!double.IsFinite(settings.LowStorageThresholdGiB) || settings.LowStorageThresholdGiB is < 0 or > 1024 * 1024)
            errors.Add("Low-space threshold must be between 0 and 1,048,576 GiB.");
        if (settings.ObsWebSocketPort is < 1 or > 65535) errors.Add("OBS WebSocket port must be between 1 and 65535.");
        var hotkeys = new[] { ("Save clip", settings.SaveClipHotkey), ("Stutter marker", settings.StutterMarkerHotkey) };
        foreach (var (label, hotkey) in hotkeys)
            if (!string.IsNullOrWhiteSpace(hotkey) && !ValidHotkey(hotkey)) errors.Add($"{label} hotkey is invalid. Use a key with a modifier, such as Ctrl+Alt+F9, or leave it blank.");
        var configuredHotkeys = hotkeys.Where(h => !string.IsNullOrWhiteSpace(h.Item2) && ValidHotkey(h.Item2)).ToArray();
        for (var i = 0; i < configuredHotkeys.Length; i++)
            for (var j = i + 1; j < configuredHotkeys.Length; j++)
                if (NormalizeHotkey(configuredHotkeys[i].Item2) == NormalizeHotkey(configuredHotkeys[j].Item2))
                    errors.Add($"{configuredHotkeys[i].Item1} and {configuredHotkeys[j].Item1} hotkeys must be different.");
        if (settings.DesktopVibrance is < 0 or > 100 || settings.GameVibrance is < 0 or > 100)
            errors.Add("Vibrance must be between 0 and 100%.");
        if (!Enum.IsDefined(settings.VibranceTargetMode)) errors.Add("Choose a valid vibrance target.");
        if (settings.VibranceTargetMode == VibranceTargetMode.SelectedDisplay && string.IsNullOrWhiteSpace(settings.VibranceSelectedDisplay))
            errors.Add("Choose a display for selected-display vibrance.");
        if (settings.RecordingEnabled)
        {
            if (!File.Exists(settings.ObsExecutablePath)) errors.Add("Choose the OBS program file.");
            if (!Directory.Exists(settings.ObsConfigRoot)) errors.Add("Choose the OBS settings folder.");
        }
        if (settings.PowerEnabled && (settings.DesktopPowerPlan == Guid.Empty || settings.GamingPowerPlan == Guid.Empty))
            errors.Add("Choose a normal plan and a gaming plan.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in settings.Games)
        {
            var label = string.IsNullOrWhiteSpace(game.Name) ? "A game" : game.Name;
            if (game.CaptureMode is CaptureMode capture && !Enum.IsDefined(capture)) errors.Add($"{label}: choose a supported capture mode.");
            if (string.IsNullOrWhiteSpace(game.Name)) errors.Add("Every game needs a name.");
            if (!ExecutablePathRules.IsValidAbsoluteExecutable(game.ExecutablePath))
                errors.Add($"Choose a full executable path for {label}.");
            else
            {
                var normalizedPrimary = ExecutablePathRules.Normalize(game.ExecutablePath)!;
                if (!paths.Add(normalizedPrimary))
                    errors.Add($"{label} has a duplicate executable path.");
            }
            if (game.VibrancePercent is int v && v is < 0 or > 100)
                errors.Add($"{label}: vibrance must be between 0 and 100%.");
            if (game.RecordingStopDelaySeconds is int d && d is < 0 or > 120)
                errors.Add($"{label}: wait time before stopping must be between 0 and 120 seconds.");
            if (game.ReplayBufferSeconds is int replay && replay is < 15 or > 600)
                errors.Add($"{label}: replay buffer length must be between 15 and 600 seconds.");
            if (settings.RecordingEnabled && game.Record)
            {
                var effective = EffectiveGameOptionsResolver.Resolve(settings, game);
                if (!Directory.Exists(effective.RecordingDirectory))
                    errors.Add($"{label}: choose an existing recording folder in the game override or global Recording settings.");
            }
        }
        return errors;
    }

    private static bool ValidHotkey(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = parts.Count(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("Alt", StringComparison.OrdinalIgnoreCase) || p.Equals("Shift", StringComparison.OrdinalIgnoreCase) || p.Equals("Win", StringComparison.OrdinalIgnoreCase) || p.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        var modifierTokens = parts.Take(Math.Max(0, parts.Length - 1)).Select(p => p.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "CTRL" : p.Equals("Windows", StringComparison.OrdinalIgnoreCase) ? "WIN" : p.ToUpperInvariant()).ToArray();
        if (modifierTokens.Distinct(StringComparer.Ordinal).Count() != modifierTokens.Length) return false;
        var key = parts.Length == 0 ? "" : parts[^1].ToUpperInvariant();
        var named = key.Length is >= 2 and <= 3 && key[0] == 'F' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24;
        named |= new[] { "ESC", "ESCAPE", "SPACE", "TAB", "HOME", "END", "INSERT", "DELETE", "PGUP", "PGDN", "UP", "DOWN", "LEFT", "RIGHT", "PAUSE", "PRINTSCREEN", "BACKSPACE" }.Contains(key, StringComparer.OrdinalIgnoreCase);
        return modifiers > 0 && parts.Length == modifiers + 1 && (key.Length == 1 && char.IsLetterOrDigit(key[0]) || named);
    }

    private static string NormalizeHotkey(string text) => string.Join("+", text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "CTRL" : p.ToUpperInvariant()).Order(StringComparer.Ordinal));
}
