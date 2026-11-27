namespace JamesTools.Core.Configuration;

/// <summary>The resolved settings used for a selected game (per-game overrides fall back to global defaults).</summary>
public sealed record EffectiveGameOptions(
    bool Record,
    bool UseVibrance,
    CaptureMode CaptureMode,
    string RecordingDirectory,
    int ReplayBufferSeconds,
    int RecordingStopDelaySeconds,
    int VibrancePercent,
    Guid PowerPlanId);

public static class EffectiveGameOptionsResolver
{
    public static EffectiveGameOptions Resolve(AppSettings settings, GameRule? game)
    {
        var power = game?.PowerPlanId is Guid gamePlan && gamePlan != Guid.Empty
            ? gamePlan
            : settings.GamingPowerPlan;
        return new EffectiveGameOptions(
            game?.Record ?? false,
            game?.UseVibrance ?? false,
            game?.CaptureMode ?? global::JamesTools.Core.Configuration.CaptureMode.Recording,
            !string.IsNullOrWhiteSpace(game?.RecordingDirectory) ? game.RecordingDirectory : settings.RecordingDirectory,
            game?.ReplayBufferSeconds ?? settings.ReplayBufferSeconds,
            game?.RecordingStopDelaySeconds ?? settings.RecordingStopDelaySeconds,
            game?.VibrancePercent ?? settings.GameVibrance,
            power);
    }
}
