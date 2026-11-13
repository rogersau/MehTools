namespace JamesTools.Core.Configuration;

/// <summary>The immutable settings used for one selected game after override/profile/global resolution.</summary>
public sealed record EffectiveGameOptions(
    bool Record,
    bool UseVibrance,
    CaptureMode CaptureMode,
    string RecordingDirectory,
    int ReplayBufferSeconds,
    int RecordingStopDelaySeconds,
    int VibrancePercent,
    Guid PowerPlanId,
    bool OverlayShowCpu,
    bool OverlayShowGpu,
    bool OverlayShowFps,
    bool OverlayShowLatency);

/// <summary>Resolves all per-game values in one place. Game permissions remain explicit game settings.</summary>
public static class EffectiveGameOptionsResolver
{
    public static EffectiveGameOptions Resolve(AppSettings settings, GameRule? game)
    {
        var profile = game?.ProfileId is Guid profileId
            ? settings.Profiles.FirstOrDefault(p => p.Id == profileId)
            : null;
        var preset = game?.OverlayPreset ?? profile?.OverlayPreset;
        var (cpu, gpu, fps, latency) = preset switch
        {
            OverlayPreset.Minimal => (false, false, true, false),
            OverlayPreset.Fps => (false, false, true, true),
            OverlayPreset.Full => (true, true, true, true),
            _ => (settings.OverlayShowCpu, settings.OverlayShowGpu, settings.OverlayShowFps, settings.OverlayShowLatency)
        };
        var power = game?.PowerPlanId is Guid gamePlan
            ? gamePlan == Guid.Empty ? settings.GamingPowerPlan : gamePlan
            : profile?.PowerPlanId is Guid profilePlan && profilePlan != Guid.Empty
                ? profilePlan
                : settings.GamingPowerPlan;
        return new EffectiveGameOptions(
            game?.Record ?? false,
            game?.UseVibrance ?? false,
            game?.CaptureMode ?? profile?.CaptureMode ?? global::JamesTools.Core.Configuration.CaptureMode.Recording,
            First(game?.RecordingDirectory, profile?.RecordingDirectory, settings.RecordingDirectory),
            game?.ReplayBufferSeconds ?? profile?.ReplayBufferSeconds ?? settings.ReplayBufferSeconds,
            game?.RecordingStopDelaySeconds ?? profile?.RecordingStopDelaySeconds ?? settings.RecordingStopDelaySeconds,
            game?.VibrancePercent ?? profile?.VibrancePercent ?? settings.GameVibrance,
            power,
            cpu, gpu, fps, latency);
    }

    private static string First(string? game, string? profile, string global)
        => !string.IsNullOrWhiteSpace(game) ? game : !string.IsNullOrWhiteSpace(profile) ? profile : global;
}
