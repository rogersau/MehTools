using System.Text;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;

namespace JamesTools.Core.Automation;

public sealed record AutomationStatus(string Game, string Recording, string Vibrance, string Power, string Notice);

/// <summary>Owns only the recording it starts. All other automation remains independent.</summary>
public sealed class AutomationService : IAsyncDisposable
{
    private readonly IRecordingController _recorder;
    private readonly IAutomationPlatform _platform;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly string _logPath;
    private AppSettings _settings;
    private Task? _loop;
    private IReadOnlyList<SteamGame> _steamGames = [];
    private DateTime _steamRefresh = DateTime.MinValue;
    private DateTime? _recordingEndSince;
    private string? _recordingGamePath;
    private int _recordingGamePid;
    private int _attemptPid;
    private int _blockedPid;
    private int _failedAttempts;
    private int _resetRetries;
    private DateTime _retryAfter;
    private int? _lastVibrance;
    private Guid? _lastPowerPlan;
    private bool _vibranceManaged;
    private bool _powerManaged;
    private string _lastLogMessage = "";
    private AutomationStatus? _lastStatus;

    public AutomationService(AppSettings settings, string dataDirectory)
        : this(settings, dataDirectory, new WindowsAutomationPlatform(), new WindowsRecordingController(), () => DateTime.UtcNow)
    {
    }

    internal AutomationService(AppSettings settings, string dataDirectory, IAutomationPlatform platform,
        IRecordingController recorder, Func<DateTime> utcNow)
    {
        _platform = platform;
        _recorder = recorder;
        _utcNow = utcNow;
        _settings = settings.Snapshot();
        _logPath = Path.Combine(dataDirectory, "activity.log");
    }

    public event Action<AutomationStatus>? StatusChanged;
    public event Action<string>? Activity;

    public void Start() => _loop ??= Task.Run(RunAsync);

    public void UpdateSettings(AppSettings settings)
    {
        Volatile.Write(ref _settings, settings.Snapshot());
        Interlocked.Exchange(ref _resetRetries, 1);
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref _settings);
                try { await TickAsync(settings); }
                catch (Exception ex) { Log("Monitoring error: " + ex.Message); }
                await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 2, 30)), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_recorder.IsOwnedRecording)
            {
                var stopped = await _recorder.StopOwnedAsync();
                Log(stopped ? "Recording stopped." : "OBS didn't close. It's still open.");
            }
            RestoreManagedSettings(Volatile.Read(ref _settings));
        }
    }

    private async Task TickAsync(AppSettings settings)
    {
        if (!settings.AutomationEnabled)
        {
            if (_recorder.IsOwnedRecording)
            {
                var stopped = await _recorder.StopOwnedAsync();
                Log(stopped ? "Recording stopped." : "OBS didn't close. It's still open.");
            }
            _recordingEndSince = null;
            _recordingGamePid = 0;
            _recordingGamePath = null;
            _attemptPid = _blockedPid = _failedAttempts = 0;
            RestoreManagedSettings(settings);
            Publish(new("None", "Off", "Off", "Off", "Watching is off."));
            return;
        }

        var processes = _platform.CaptureProcesses();
        var matched = AutomationPolicy.MatchGames(settings.Games, processes);
        if (settings.PowerEnabled && settings.PowerForAllSteamGames && _utcNow() >= _steamRefresh)
        {
            _steamGames = _platform.DiscoverSteamGames();
            _steamRefresh = _utcNow().AddSeconds(30);
        }
        var steamRunning = settings.PowerEnabled && settings.PowerForAllSteamGames
            ? _platform.FindRunningSteamGames(_steamGames, processes)
            : [];
        var notice = new StringBuilder();
        var gameName = matched.Count > 0 ? string.Join(", ", matched.Select(m => m.Rule.Name).Distinct())
            : steamRunning.Count > 0 ? string.Join(", ", steamRunning.Select(g => g.Name).Distinct()) : "None";

        var recording = await UpdateRecordingAsync(settings, matched, notice);
        var vibrance = UpdateVibrance(settings, matched, notice);
        var power = UpdatePower(settings, matched.Count > 0 || steamRunning.Count > 0, notice);
        Publish(new(gameName, recording, vibrance, power, notice.ToString().Trim()));
    }

    private async Task<string> UpdateRecordingAsync(AppSettings settings, IReadOnlyList<MatchedGame> matched, StringBuilder notice)
    {
        if (Interlocked.Exchange(ref _resetRetries, 0) != 0)
        {
            _attemptPid = _blockedPid = _failedAttempts = 0;
            _retryAfter = DateTime.MinValue;
        }
        var selected = settings.RecordingEnabled ? matched.FirstOrDefault(g => g.Rule.Record) : null;
        if (_recorder.IsOwnedRecording)
        {
            var ownerStillRunning = settings.RecordingEnabled && matched.Any(g => g.Rule.Record &&
                g.Process.Id == _recordingGamePid &&
                string.Equals(g.Rule.ExecutablePath, _recordingGamePath, StringComparison.OrdinalIgnoreCase));
            if (ownerStillRunning) { _recordingEndSince = null; return "Recording"; }
            _recordingEndSince ??= _utcNow();
            var delay = settings.RecordingEnabled ? Math.Clamp(settings.RecordingStopDelaySeconds, 0, 120) : 0;
            if (_utcNow() - _recordingEndSince < TimeSpan.FromSeconds(delay)) return "Stopping soon";
            var stopped = await _recorder.StopOwnedAsync();
            _recordingEndSince = null;
            _recordingGamePath = null;
            _recordingGamePid = 0;
            Log(stopped ? "Recording stopped." : "OBS didn't close. It's still open.");
            return stopped ? "Ready" : "OBS still open";
        }

        _recordingEndSince = null;
        if (_recordingGamePid != 0 && matched.Any(g => g.Rule.Record && g.Process.Id == _recordingGamePid))
        {
            _blockedPid = _recordingGamePid;
            Log("OBS closed while the game was running. Save settings or restart the game to try again.");
        }
        _recordingGamePath = null;
        _recordingGamePid = 0;
        if (selected is null)
        {
            _attemptPid = _blockedPid = _failedAttempts = 0;
            return settings.RecordingEnabled ? "Ready" : "Off";
        }
        if (_attemptPid != selected.Process.Id)
        {
            _attemptPid = selected.Process.Id;
            _failedAttempts = 0;
            _retryAfter = DateTime.MinValue;
            if (_blockedPid != _attemptPid) _blockedPid = 0;
        }
        if (_blockedPid == selected.Process.Id)
        {
            Append(notice, "Recording didn't start. Check OBS, then save settings or restart the game.");
            return "Needs attention";
        }
        if (_utcNow() < _retryAfter) return "Trying again soon";
        var result = _recorder.TryStart(new ObsSettings(settings.ObsExecutablePath, settings.ObsConfigRoot,
            settings.RecordingDirectory, settings.ObsPortable), selected.Process.Id, selected.Rule.ExecutablePath);
        switch (result.Status)
        {
            case RecorderStartStatus.Started:
                _recordingGamePath = selected.Rule.ExecutablePath;
                _recordingGamePid = selected.Process.Id;
                Log("Started recording for " + selected.Rule.Name + ".");
                return "Recording";
            case RecorderStartStatus.WaitingForWindow:
                return "Waiting for game";
            case RecorderStartStatus.AlreadyRunning:
                Append(notice, result.Message);
                return "OBS already open";
            default:
                _failedAttempts++;
                _retryAfter = _utcNow().AddSeconds(30);
                if (_failedAttempts >= 3) _blockedPid = selected.Process.Id;
                Append(notice, result.Message);
                Log("Recorder: " + result.Message);
                return "Needs attention";
        }
    }

    private string UpdateVibrance(AppSettings settings, IReadOnlyList<MatchedGame> matched, StringBuilder notice)
    {
        if (!settings.VibranceEnabled)
        {
            RestoreVibrance(settings);
            return "Off";
        }
        var target = matched.Any(g => g.Rule.UseVibrance) ? settings.GameVibrance : settings.DesktopVibrance;
        if (target is < 0 or > 100) { Append(notice, "Colour level must be between 0 and 100."); return "Needs attention"; }
        try
        {
            if (_lastVibrance != target)
            {
                _platform.SetVibrance(target);
                _lastVibrance = target;
                _vibranceManaged = true;
                Log($"Colour level set to {target}%.");
            }
            return $"{target}%";
        }
        catch (Exception ex)
        {
            _lastVibrance = null;
            _platform.ResetVibrance();
            Append(notice, "Colours: " + ex.Message);
            Log("Colours: " + ex.Message);
            return "Unavailable";
        }
    }

    private string UpdatePower(AppSettings settings, bool gaming, StringBuilder notice)
    {
        if (!settings.PowerEnabled)
        {
            RestorePower(settings);
            return "Off";
        }
        var target = gaming ? settings.GamingPowerPlan : settings.DesktopPowerPlan;
        try
        {
            if (_platform.GetActivePowerPlan() != target)
            {
                _platform.SetPowerPlan(target);
                Log(gaming ? "Gaming power plan activated." : "Desktop power plan activated.");
            }
            _lastPowerPlan = target;
            _powerManaged = true;
            return gaming ? "Gaming plan" : "Desktop plan";
        }
        catch (Exception ex)
        {
            Append(notice, "Power plan: " + ex.Message);
            Log("Power plan: " + ex.Message);
            return "Unavailable";
        }
    }

    private void RestoreManagedSettings(AppSettings settings)
    {
        RestoreVibrance(settings);
        RestorePower(settings);
    }

    private void RestoreVibrance(AppSettings settings)
    {
        if (!_vibranceManaged) return;
        try
        {
            // Respect a later manual change from the user or another application.
            if (_lastVibrance is int previous &&
                _platform.ReadVibrance().All(display => Math.Abs(display.Percent - previous) <= 1))
                _platform.SetVibrance(Math.Clamp(settings.DesktopVibrance, 0, 100));
        }
        catch (Exception ex) { Log("Couldn't restore colours: " + ex.Message); }
        finally { _vibranceManaged = false; _lastVibrance = null; }
    }

    private void RestorePower(AppSettings settings)
    {
        if (!_powerManaged) return;
        try
        {
            if (_lastPowerPlan is Guid previous && _platform.GetActivePowerPlan() == previous && previous != settings.DesktopPowerPlan)
                _platform.SetPowerPlan(settings.DesktopPowerPlan);
        }
        catch (Exception ex) { Log("Could not restore power plan: " + ex.Message); }
        finally { _powerManaged = false; _lastPowerPlan = null; }
    }

    private static void Append(StringBuilder builder, string message)
    {
        if (builder.Length > 0) builder.Append("  ");
        builder.Append(message);
    }

    private void Publish(AutomationStatus status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private void Log(string message)
    {
        if (message == _lastLogMessage) return;
        _lastLogMessage = message;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Activity?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        else
        {
            if (_recorder.IsOwnedRecording) await _recorder.StopOwnedAsync();
            RestoreManagedSettings(Volatile.Read(ref _settings));
        }
        _platform.Dispose();
        _recorder.Dispose();
        _wake.Dispose();
        _stop.Dispose();
    }

    internal Task TickForTestsAsync() => TickAsync(Volatile.Read(ref _settings));
}
public sealed record MatchedGame(GameRule Rule, RunningProcess Process);

public static class AutomationPolicy
{
    public static IReadOnlyList<MatchedGame> MatchGames(IEnumerable<GameRule> rules, IReadOnlyList<RunningProcess> processes)
    {
        var matches = new List<MatchedGame>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ExecutablePath)) continue;
            var process = processes.FirstOrDefault(p => p.FullPath is not null &&
                string.Equals(p.FullPath, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (process is not null) matches.Add(new MatchedGame(rule, process));
        }
        return matches;
    }
}
