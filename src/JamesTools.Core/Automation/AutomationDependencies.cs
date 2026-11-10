using JamesTools.Core.Platform;
using JamesTools.Core.Recording;

namespace JamesTools.Core.Automation;

internal interface IAutomationPlatform : IDisposable
{
    IReadOnlyList<RunningProcess> CaptureProcesses();
    IReadOnlyList<SteamGame> DiscoverSteamGames();
    IReadOnlyList<SteamGame> FindRunningSteamGames(IReadOnlyList<SteamGame> catalog, IReadOnlyList<RunningProcess> processes);
    IReadOnlyList<VibranceDisplay> ReadVibrance();
    void SetVibrance(int percent);
    void ResetVibrance();
    Guid GetActivePowerPlan();
    void SetPowerPlan(Guid id);
}

internal interface IRecordingController : IDisposable
{
    bool IsOwnedRecording { get; }
    RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath);
    Task<bool> StopOwnedAsync();
}

internal sealed class WindowsAutomationPlatform : IAutomationPlatform
{
    private NvidiaVibrance? _nvidia;
    public IReadOnlyList<RunningProcess> CaptureProcesses() => ProcessSnapshot.Capture();
    public IReadOnlyList<SteamGame> DiscoverSteamGames() => SteamGameDetector.Discover();
    public IReadOnlyList<SteamGame> FindRunningSteamGames(IReadOnlyList<SteamGame> catalog, IReadOnlyList<RunningProcess> processes)
        => SteamGameDetector.FindRunning(catalog, processes);
    public IReadOnlyList<VibranceDisplay> ReadVibrance() => (_nvidia ??= new NvidiaVibrance()).Read();
    public void SetVibrance(int percent) => (_nvidia ??= new NvidiaVibrance()).SetAll(percent);
    public void ResetVibrance() { _nvidia?.Dispose(); _nvidia = null; }
    public Guid GetActivePowerPlan() => PowerPlans.GetActive();
    public void SetPowerPlan(Guid id) => PowerPlans.SetActive(id);
    public void Dispose() => ResetVibrance();
}

internal sealed class WindowsRecordingController : IRecordingController
{
    private readonly ObsRecorder _recorder = new();
    public bool IsOwnedRecording => _recorder.IsOwnedRecording;
    public RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath) => _recorder.TryStart(settings, gamePid, gameExePath);
    public Task<bool> StopOwnedAsync() => _recorder.StopOwnedAsync();
    public void Dispose() => _recorder.Dispose();
}
