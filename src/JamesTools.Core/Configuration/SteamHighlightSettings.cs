using System.Text.Json.Serialization;
using JamesTools.Core.Recording;

namespace JamesTools.Core.Configuration;

public sealed class SteamHighlightSettings
{
    public const int DefaultPaddingSeconds = 10;
    public const int MaxPaddingSeconds = 60;
    /// <summary>Fallback for settings saved before the Games page had per-game controls.</summary>
    public bool AutoExport { get; set; }
    public int BeforeSeconds { get; set; } = DefaultPaddingSeconds;
    public int AfterSeconds { get; set; } = DefaultPaddingSeconds;
    public string RecordingFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public bool IncludeKills { get; set; } = true;
    public bool IncludeAssists { get; set; } = true;
    public bool IncludeRevives { get; set; } = true;
    public bool IncludeOther { get; set; } = true;

    [JsonIgnore]
    public SteamWardogsEventCategory IncludedCategories =>
        (IncludeKills ? SteamWardogsEventCategory.Kills : 0) |
        (IncludeAssists ? SteamWardogsEventCategory.Assists : 0) |
        (IncludeRevives ? SteamWardogsEventCategory.Revives : 0) |
        (IncludeOther ? SteamWardogsEventCategory.Other : 0);

    public SteamHighlightSettings Copy() => (SteamHighlightSettings)MemberwiseClone();

    public static bool SupportsGame(GameRule game) => SteamWardogsLibrary.IsWardogs(game.Name, game.ExecutablePath);

    public bool AutoExportFor(GameRule game) => SupportsGame(game) && (game.SteamAutoHighlights ?? AutoExport);

    public static bool ValidPadding(int before, int after)
        => before is >= 0 and <= MaxPaddingSeconds && after is >= 0 and <= MaxPaddingSeconds && before + after > 0;

    internal static bool IsLocalFolder(string? folder) => !string.IsNullOrWhiteSpace(folder)
        && Path.IsPathFullyQualified(folder) && !folder.StartsWith(@"\\", StringComparison.Ordinal) && !folder.AsSpan(2).Contains(':');

    internal void Normalize()
    {
        RecordingFolder = RecordingFolder?.Trim() ?? "";
        OutputFolder = OutputFolder?.Trim() ?? "";
        if (!ValidPadding(BeforeSeconds, AfterSeconds)) BeforeSeconds = AfterSeconds = DefaultPaddingSeconds;
    }
}
