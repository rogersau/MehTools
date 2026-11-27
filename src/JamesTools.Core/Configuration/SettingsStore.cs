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
        settings.SteamHighlights ??= new();
        settings.SteamHighlights.Normalize();
        settings.SchemaVersion = CurrentSchemaVersion;
        if (!double.IsFinite(settings.LowStorageThresholdGiB) || settings.LowStorageThresholdGiB < 0) settings.LowStorageThresholdGiB = 10;
        settings.Games ??= [];
        if (settings.Games.Any(game => game is null))
            throw new InvalidDataException("The settings file contains an empty game entry.");
        settings.PresentMonPath ??= "";
        settings.VibranceSelectedDisplay ??= "";
        settings.SaveClipHotkey ??= "Ctrl+Alt+F10";
        settings.StutterMarkerHotkey ??= "Ctrl+Alt+F9";
        settings.AppsToCloseOnGameLaunch ??= [];
        settings.AppsToCloseOnGameLaunch = new ObservableCollection<string>(
            settings.AppsToCloseOnGameLaunch.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        foreach (var game in settings.Games)
        {
            game.Name ??= "";
            game.ExecutablePath ??= "";
            game.MatchMode = string.IsNullOrWhiteSpace(game.MatchMode) ? "path" : game.MatchMode;
            game.RecordingDirectory = string.IsNullOrWhiteSpace(game.RecordingDirectory) ? null : game.RecordingDirectory.Trim();
            if (game.CaptureMode is CaptureMode mode && !Enum.IsDefined(mode))
                game.CaptureMode = null;
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
