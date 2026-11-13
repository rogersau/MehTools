namespace JamesTools.Core.Configuration;

public static class SettingsValidation
{
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();
        try { Diagnostics.NetworkProbeOptions.Create(settings.NetworkDiagnosticsEnabled, settings.NetworkTargets); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }
        if (!string.IsNullOrWhiteSpace(settings.MediaToolsFolder) && (!Path.IsPathFullyQualified(settings.MediaToolsFolder) || !Directory.Exists(settings.MediaToolsFolder))) errors.Add("Media tools folder must be an existing absolute folder, or blank for PATH.");
        if (settings.PollSeconds is < 2 or > 30) errors.Add("Check interval must be between 2 and 30 seconds.");
        if (settings.RecordingStopDelaySeconds is < 0 or > 120) errors.Add("Wait time before stopping must be between 0 and 120 seconds.");
        if (settings.ReplayBufferSeconds is < 15 or > 600) errors.Add("Replay buffer length must be between 15 and 600 seconds.");
        if (settings.RecordingHealthStallSeconds is < 10 or > 300) errors.Add("Recording health stall threshold must be between 10 and 300 seconds.");
        if (!double.IsFinite(settings.LowStorageThresholdGiB) || settings.LowStorageThresholdGiB is < 0 or > 1024 * 1024)
            errors.Add("Low-space threshold must be between 0 and 1,048,576 GiB.");
        if (settings.CleanupSuggestionAgeDays is int age && age is < 1 or > 36500)
            errors.Add("Cleanup suggestion age must be between 1 and 36,500 days, or blank to disable.");
        if (settings.CleanupSuggestionBudgetGiB is double budget && (!double.IsFinite(budget) || budget <= 0 || budget > 1024 * 1024))
            errors.Add("Cleanup suggestion budget must be greater than 0 and at most 1,048,576 GiB, or blank to disable.");
        if (settings.ObsWebSocketPort is < 1 or > 65535) errors.Add("OBS WebSocket port must be between 1 and 65535.");
        if (settings.RollingBeforeSeconds is < 10 or > 300 || settings.RollingAfterSeconds is < 0 or > 60) errors.Add("Rolling capture requires 10–300 seconds before and 0–60 seconds after.");
        var hotkeys = new[] { ("Rolling capture", settings.RollingHotkey), ("Bookmark", settings.BookmarkHotkey), ("Overlay", settings.OverlayHotkey), ("Save clip", settings.SaveClipHotkey), ("Stutter marker", settings.StutterMarkerHotkey) };
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
        var profileIds = new HashSet<Guid>();
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            var label = string.IsNullOrWhiteSpace(profile.Name) ? "A profile" : profile.Name.Trim();
            if (profile.Id == Guid.Empty) errors.Add($"{label}: profile ID is empty. Duplicate the profile to create a new ID.");
            else if (!profileIds.Add(profile.Id)) errors.Add($"{label}: profile ID is duplicated.");
            if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add("Every game profile needs a name.");
            else if (!profileNames.Add(profile.Name.Trim())) errors.Add($"Game profile name '{profile.Name.Trim()}' is duplicated.");
            if (profile.CaptureMode is CaptureMode capture && !Enum.IsDefined(capture)) errors.Add($"{label}: choose recording, replay, or both for capture mode.");
            if (profile.ReplayBufferSeconds is int replay && replay is < 15 or > 600) errors.Add($"{label}: replay buffer length must be between 15 and 600 seconds.");
            if (profile.RecordingStopDelaySeconds is int delay && delay is < 0 or > 120) errors.Add($"{label}: wait time before stopping must be between 0 and 120 seconds.");
            if (profile.VibrancePercent is int vibrance && vibrance is < 0 or > 100) errors.Add($"{label}: vibrance must be between 0 and 100%.");
            if (profile.OverlayPreset is OverlayPreset overlay && !Enum.IsDefined(overlay)) errors.Add($"{label}: choose a valid overlay preset.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliasPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in settings.Games)
        {
            var label = string.IsNullOrWhiteSpace(game.Name) ? "A game" : game.Name;
            if (game.CaptureMode is CaptureMode capture && !Enum.IsDefined(capture)) errors.Add($"{label}: choose recording, replay, or both for capture mode.");
            if (game.OverlayPreset is OverlayPreset overlay && !Enum.IsDefined(overlay)) errors.Add($"{label}: choose a valid overlay preset.");
            if (string.IsNullOrWhiteSpace(game.Name)) errors.Add("Every game needs a name.");
            if (!ExecutablePathRules.IsValidAbsoluteExecutable(game.ExecutablePath))
                errors.Add($"Choose a full executable path for {label}.");
            else
            {
                var normalizedPrimary = ExecutablePathRules.Normalize(game.ExecutablePath)!;
                if (!paths.Add(normalizedPrimary) || aliasPaths.Contains(normalizedPrimary))
                    errors.Add($"{label} has a duplicate executable path.");
            }
            foreach (var alias in game.ExecutableAliases ?? [])
            {
                if (!ExecutablePathRules.IsValidAbsoluteExecutable(alias))
                {
                    errors.Add($"{label}: alias '{alias}' must be a full .exe path. Correct or remove it.");
                    continue;
                }
                var normalized = ExecutablePathRules.Normalize(alias)!;
                if (ExecutablePathRules.EqualsPath(normalized, game.ExecutablePath))
                    errors.Add($"{label}: alias '{alias}' duplicates the primary executable path.");
                else if (!aliasPaths.Add(normalized))
                    errors.Add($"{label}: alias '{alias}' duplicates another executable alias.");
                else if (paths.Contains(normalized))
                    errors.Add($"{label}: alias '{alias}' duplicates another game's primary executable path.");
            }
            if (game.VibrancePercent is int v && v is < 0 or > 100)
                errors.Add($"{label}: vibrance must be between 0 and 100%.");
            if (game.RecordingStopDelaySeconds is int d && d is < 0 or > 120)
                errors.Add($"{label}: wait time before stopping must be between 0 and 120 seconds.");
            if (game.ReplayBufferSeconds is int replay && replay is < 15 or > 600)
                errors.Add($"{label}: replay buffer length must be between 15 and 600 seconds.");
            if (game.ProfileId is Guid profileId && !settings.Profiles.Any(p => p.Id == profileId))
                errors.Add($"{label}: the selected profile is missing. Reassign this game to an existing profile or clear its profile selection.");
            if (settings.RecordingEnabled && game.Record)
            {
                var effective = EffectiveGameOptionsResolver.Resolve(settings, game);
                if (!Directory.Exists(effective.RecordingDirectory))
                    errors.Add($"{label}: choose an existing recording folder in the game override, its profile, or global Recording settings.");
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
