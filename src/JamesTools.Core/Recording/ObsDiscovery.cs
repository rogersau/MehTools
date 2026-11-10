namespace JamesTools.Core.Recording;

public sealed record DiscoveredObs(string ConfigRoot, bool Portable, string? RecordingDirectory);

/// <summary>Finds a selected OBS installation's configuration without modifying it.</summary>
public static class ObsDiscovery
{
    public static DiscoveredObs? FromExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return null;
        var directory = Path.GetDirectoryName(executable);
        var root = directory is null ? null : Directory.GetParent(directory)?.Parent?.FullName;
        if (root is null) return null;
        var portableConfig = Path.Combine(root, "config", "obs-studio");
        var regularConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        var portable = Directory.Exists(portableConfig);
        var config = portable ? portableConfig : regularConfig;
        if (!Directory.Exists(config)) return null;
        return new DiscoveredObs(config, portable, ReadRecordingDirectory(config));
    }

    private static string? ReadRecordingDirectory(string root)
    {
        try
        {
            var user = IniDocument.Read(Path.Combine(root, "user.ini"));
            var profile = user.GetValue("Basic", "ProfileDir");
            if (string.IsNullOrWhiteSpace(profile) || profile != Path.GetFileName(profile) ||
                profile is "." or "..") return null;
            var basic = IniDocument.Read(Path.Combine(root, "basic", "profiles", profile, "basic.ini"));
            var advanced = string.Equals(basic.GetValue("Output", "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase);
            return advanced ? basic.GetValue("AdvOut", "RecFilePath") : basic.GetValue("SimpleOutput", "FilePath");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }
}
