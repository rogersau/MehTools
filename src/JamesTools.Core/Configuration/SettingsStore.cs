using System.Text.Json;

namespace JamesTools.Core.Configuration;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsStore(string directory) => DirectoryPath = directory;
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public AppSettings? Load()
    {
        if (!File.Exists(SettingsPath)) return null;
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? throw new InvalidDataException("Settings are empty.");
        if (settings.SchemaVersion != 1)
            throw new InvalidDataException($"Settings version {settings.SchemaVersion} is not supported.");
        settings.Games ??= [];
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
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
