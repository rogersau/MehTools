using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JamesTools.Core.Configuration;

public sealed class SettingsStore
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public SettingsStore(string directory) => DirectoryPath = directory;
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public AppSettings? Load()
    {
        if (!File.Exists(SettingsPath)) return null;
        return Deserialize(File.ReadAllText(SettingsPath));
    }

    public string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    public AppSettings Deserialize(string json)
    {
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new InvalidDataException("Settings are empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The settings file is not valid JSON.", ex);
        }
        Normalize(settings);
        return settings;
    }

    public static void Normalize(AppSettings settings)
    {
        if (settings.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidDataException($"Settings version {settings.SchemaVersion} is not supported.");
        settings.Diagnostics ??= new();
        settings.Diagnostics.Validate();
        settings.LiveWardogs ??= new(); settings.LiveWardogs.Normalize();
        settings.SteamHighlights ??= new(); settings.SteamHighlights.Normalize();
        settings.SchemaVersion = CurrentSchemaVersion;
        if (!double.IsFinite(settings.LowStorageThresholdGiB) || settings.LowStorageThresholdGiB < 0) settings.LowStorageThresholdGiB = 10;
        if (settings.CleanupSuggestionAgeDays is < 1) settings.CleanupSuggestionAgeDays = null;
        if (settings.CleanupSuggestionBudgetGiB is double budget && (!double.IsFinite(budget) || budget <= 0)) settings.CleanupSuggestionBudgetGiB = null;
        settings.Games ??= [];
        settings.Profiles ??= [];
        if (settings.Games.Any(game => game is null))
            throw new InvalidDataException("The settings file contains an empty game entry.");
        if (settings.Profiles.Any(profile => profile is null))
            throw new InvalidDataException("The settings file contains an empty game profile entry.");
        settings.PresentMonPath ??= "";
        settings.VibranceSelectedDisplay ??= "";
        settings.OverlayHotkey ??= "";
        settings.SaveClipHotkey ??= "Ctrl+Alt+F10";
        settings.StutterMarkerHotkey ??= "Ctrl+Alt+F9";
        settings.LastOverlayGamePath ??= "";
        settings.AppsToCloseOnGameLaunch ??= [];
        settings.AppsToCloseOnGameLaunch = new ObservableCollection<string>(
            settings.AppsToCloseOnGameLaunch.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        if (double.IsNaN(settings.OverlayOpacity) || settings.OverlayOpacity <= 0 || settings.OverlayOpacity > 1)
            settings.OverlayOpacity = 0.92;
        foreach (var game in settings.Games)
        {
            game.Name ??= "";
            game.ExecutablePath ??= "";
            game.ExecutableAliases ??= [];
            var aliases = new List<string>();
            var aliasKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in game.ExecutableAliases)
            {
                var trimmed = alias?.Trim() ?? "";
                var normalized = ExecutablePathRules.Normalize(trimmed);
                var key = normalized ?? trimmed;
                if (string.IsNullOrWhiteSpace(trimmed) || aliasKeys.Add(key)) aliases.Add(trimmed);
            }
            game.ExecutableAliases = new ObservableCollection<string>(aliases);
            game.MatchMode = string.IsNullOrWhiteSpace(game.MatchMode) ? "path" : game.MatchMode;
            if (string.IsNullOrWhiteSpace(game.RecordingDirectory)) game.RecordingDirectory = null;
            else game.RecordingDirectory = game.RecordingDirectory.Trim();
        }
        foreach (var profile in settings.Profiles)
        {
            profile.Name ??= "";
            profile.RecordingDirectory = string.IsNullOrWhiteSpace(profile.RecordingDirectory) ? null : profile.RecordingDirectory.Trim();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, Serialize(settings));
            if (File.Exists(SettingsPath))
                File.Replace(temporaryPath, SettingsPath, SettingsPath + ".bak");
            else
                File.Move(temporaryPath, SettingsPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
