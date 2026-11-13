using System.Text;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using JamesTools.Core.Storage;

namespace JamesTools.Core.Automation;

public sealed record AutomationStatus(string Game, string Recording, string Vibrance, string Power, string Notice, string Headline, string[] ActiveGamePaths);

public sealed record AutomationNotification(string Title, string Message, bool Warning);

/// <summary>Owns only the recording it starts. All other automation remains independent.</summary>
public sealed class AutomationService : IAsyncDisposable
{
    private readonly IRecordingController _recorder;
    private readonly IAutomationPlatform _platform;
    private readonly Func<DateTime> _utcNow;
    private readonly IStorageCapacityProvider _storageCapacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly string _logPath;
    private AppSettings _settings;
    private Task? _loop;
    private IReadOnlyList<LibraryGame> _libraryGames = [];
    private DateTime _libraryRefresh = DateTime.MinValue;
    private DateTime? _recordingEndSince;
    private DateTime _nextStorageCheckUtc;
    private DateTime _lastStorageWarningUtc = DateTime.MinValue;
    private string _lastStorageWarningKey = "";
    private string? _recordingGamePath;
    private string? _recordingDirectory;
    private int _recordingGamePid;
    private string _recordingRequested = "recording";
    private int _attemptPid;
    private int _blockedPid;
    private int _failedAttempts;
    private int _resetRetries;
    private DateTime _retryAfter;
    private int? _lastVibrance;
    private Guid? _lastPowerPlan;
    private bool _vibranceManaged;
    private readonly Dictionary<string, int> _targetedVibrance = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vibranceManualOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restoreFailureLogged = new(StringComparer.OrdinalIgnoreCase);
    private bool _powerManaged;
    private bool _wasGaming;
    private string _lastLogMessage = "";
    private AutomationStatus? _lastStatus;
    private string _lastMatchSignature = "";

    public AutomationService(AppSettings settings, string dataDirectory)
        : this(settings, dataDirectory, new WindowsAutomationPlatform(), new WindowsRecordingController(), () => DateTime.UtcNow)
    {
    }

    internal AutomationService(AppSettings settings, string dataDirectory, IAutomationPlatform platform,
        IRecordingController recorder, Func<DateTime> utcNow, IStorageCapacityProvider? storageCapacity = null)
    {
        _platform = platform;
        _recorder = recorder;
        _utcNow = utcNow;
        _storageCapacity = storageCapacity ?? new FileSystemStorageCapacityProvider();
        _settings = settings.Snapshot();
        _logPath = Path.Combine(dataDirectory, "activity.log");
    }

    public event Action<AutomationStatus>? StatusChanged;
    public event Action<string>? Activity;
    public event Action<AutomationNotification>? Notify;
    /// <summary>Configured game path that currently owns automation-launched OBS, when ownership is known.</summary>
    public string? RecordingGamePath => Volatile.Read(ref _recordingGamePath);

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
                Log(stopped ? "OBS closed after automation stopped." : "OBS didn't close. It's still open.");
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
                Log(stopped ? "OBS closed after automation stopped." : "OBS didn't close. It's still open.");
            }
            _recordingEndSince = null;
            _recordingGamePid = 0;
            Volatile.Write(ref _recordingGamePath, null);
            _recordingDirectory = null;
            _attemptPid = _blockedPid = _failedAttempts = 0;
            _wasGaming = false;
            RestoreManagedSettings(settings);
            Publish(new("None", "Off", "Off", "Off", "Watching is off.", "Off", Array.Empty<string>()));
            return;
        }

        var processes = _platform.CaptureProcesses();
        var matched = AutomationPolicy.MatchAllGames(settings.Games, processes);
        var detection = new AppDetectionOptions(settings.PowerForAllSteamGames, settings.DetectEpicGames, settings.DetectGogGames, settings.DetectEaGames, settings.DetectUbisoftGames, settings.DetectXboxGames);
        var detectAny = detection.Steam || detection.Epic || detection.Gog || detection.Ea || detection.Ubisoft || detection.Xbox;
        if (settings.PowerEnabled && detectAny && _utcNow() >= _libraryRefresh)
        {
            _libraryGames = _platform.DiscoverLibraryGames(detection);
            _libraryRefresh = _utcNow().AddSeconds(30);
        }
        var libraryRunning = settings.PowerEnabled && detectAny
            ? _platform.FindRunningLibraryGames(_libraryGames, processes)
            : [];
        var gamingNow = matched.Count > 0 || libraryRunning.Count > 0;
        if (settings.CloseAppsOnGameLaunch && !_wasGaming && gamingNow && settings.AppsToCloseOnGameLaunch.Count > 0)
        {
            try
            {
                var closed = _platform.CloseApplications(settings.AppsToCloseOnGameLaunch);
                if (closed > 0) Log($"Asked {closed} app window(s) to close before gaming.");
            }
            catch (Exception ex) { Log("Couldn't close apps: " + ex.Message); }
        }
        _wasGaming = gamingNow;
        var notice = new StringBuilder();
        var gameName = matched.Count > 0 ? string.Join(", ", matched.Select(m => m.Rule.Name).Distinct())
            : libraryRunning.Count > 0 ? string.Join(", ", libraryRunning.Select(g => g.Name).Distinct()) : "None";
        var matchSignature = matched.Count == 0 ? "" : string.Join("|", matched.Select(m => $"{m.Rule.Name}:{m.Rule.MatchMode}:{m.Process.Name}:{m.Process.Id}"));
        if (matchSignature != _lastMatchSignature)
        {
            _lastMatchSignature = matchSignature;
            if (matched.Count > 0)
            {
                var details = string.Join("; ", matched.Select(m => $"{m.Rule.Name} [{m.Rule.MatchMode}] -> {m.Process.Name} (PID {m.Process.Id})"));
                Log("Matched: " + details);
            }
            else Log("No selected game matched.");
        }

        var recording = await UpdateRecordingAsync(settings, matched, notice);
        var vibrance = UpdateVibrance(settings, matched, notice);
        var power = UpdatePower(settings, matched, libraryRunning.Count > 0, notice);
        var headline = !settings.AutomationEnabled ? "Off"
            : recording.StartsWith("OBS launched (", StringComparison.Ordinal) ? "OBS launched"
            : recording is "Needs attention" or "OBS still open" ? "Needs attention"
            : matched.Count > 0 || libraryRunning.Count > 0 ? "Game detected"
            : "Watching";
        var activePaths = matched.Select(m => m.Rule.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Publish(new(gameName, recording, vibrance, power, notice.ToString().Trim(), headline, activePaths));
    }

    private async Task<string> UpdateRecordingAsync(AppSettings settings, IReadOnlyList<MatchedGame> matched, StringBuilder notice)
    {
        if (Interlocked.Exchange(ref _resetRetries, 0) != 0)
        {
            _attemptPid = _blockedPid = _failedAttempts = 0;
            _retryAfter = DateTime.MinValue;
        }
        var selected = settings.RecordingEnabled ? matched.FirstOrDefault(g => g.Rule.Record) : null;
        var ownerMatch = settings.RecordingEnabled
            ? matched.FirstOrDefault(g => g.Rule.Record && g.Process.Id == _recordingGamePid &&
                string.Equals(g.Rule.ExecutablePath, _recordingGamePath, StringComparison.OrdinalIgnoreCase))
            : null;
        if (_recorder.IsOwnedRecording)
        {
            if (settings.LowStorageWarningsEnabled && _utcNow() >= _nextStorageCheckUtc && !string.IsNullOrWhiteSpace(_recordingDirectory))
            {
                _nextStorageCheckUtc = _utcNow().AddSeconds(30);
                await CheckStorageAsync(_recordingDirectory, settings, notice, false);
            }
            if (ownerMatch is not null) { _recordingEndSince = null; return $"OBS launched ({_recordingRequested} requested)"; }
            _recordingEndSince ??= _utcNow();
            var delaySource = settings.RecordingStopDelaySeconds;
            if (_recordingGamePath is not null)
            {
                var ownerRule = settings.Games.FirstOrDefault(g => string.Equals(g.ExecutablePath, _recordingGamePath, StringComparison.OrdinalIgnoreCase));
                if (ownerRule is not null) delaySource = EffectiveGameOptionsResolver.Resolve(settings, ownerRule).RecordingStopDelaySeconds;
            }
            var delay = settings.RecordingEnabled ? Math.Clamp(delaySource, 0, 120) : 0;
            if (_utcNow() - _recordingEndSince < TimeSpan.FromSeconds(delay)) return "Stopping soon";
            var stopped = await _recorder.StopOwnedAsync();
            var captureDirectory = _recordingDirectory;
            _recordingEndSince = null;
            Volatile.Write(ref _recordingGamePath, null);
            _recordingDirectory = null;
            _recordingGamePid = 0;
            Log(stopped ? "OBS closed after the game ended." : "OBS didn't close. It's still open.");
            var msg = stopped ? "OBS closed after the game ended. Actual recording output wasn't checked here." : "OBS didn't close. It's still open.";
            if (stopped && settings.OpenRecordingsAfterStop && !string.IsNullOrWhiteSpace(captureDirectory) && Directory.Exists(captureDirectory))
            {
                msg += " Opening recordings folder.";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(captureDirectory) { UseShellExecute = true }); } catch { }
            }
            RaiseNotification(stopped ? "OBS closed" : "OBS needs attention", msg, !stopped);
            return stopped ? "Ready" : "OBS still open";
        }

        _recordingEndSince = null;
        if (_recordingGamePid != 0 && matched.Any(g => g.Rule.Record && g.Process.Id == _recordingGamePid))
        {
            _blockedPid = _recordingGamePid;
            Log("OBS closed while the game was running; recording state is unknown. Save settings or restart the game to try again.");
            RaiseNotification("OBS closed unexpectedly", "OBS closed while the game was running; recording state is unknown. Save settings or restart the game to try again.", true);
        }
        Volatile.Write(ref _recordingGamePath, null);
        _recordingDirectory = null;
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
        var effective = EffectiveGameOptionsResolver.Resolve(settings, selected.Rule);
        var folderPlan = RecordingFolderPlanner.Plan(effective.RecordingDirectory, selected.Rule.Name, _utcNow().ToLocalTime(), settings.OrganiseCapturesByGameAndDate);
        if (settings.LowStorageWarningsEnabled)
            await CheckStorageAsync(folderPlan.PlannedDirectory, settings, notice, true);
        if (folderPlan.Organised)
        {
            try { Directory.CreateDirectory(folderPlan.PlannedDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _failedAttempts++;
                _retryAfter = _utcNow().AddSeconds(30);
                var message = "Couldn't prepare the planned recording folder: " + ex.Message;
                Append(notice, message);
                Log(message);
                return "Needs attention";
            }
        }
        if (string.IsNullOrWhiteSpace(selected.Process.FullPath))
        {
            var message = $"Can't start capture for {selected.Rule.Name}: the process path is inaccessible, so its PID can't be paired safely with an executable path.";
            Append(notice, message);
            Log(message);
            return "Needs attention";
        }
        var result = _recorder.TryStart(new ObsSettings(settings.ObsExecutablePath, settings.ObsConfigRoot,
            folderPlan.PlannedDirectory, settings.ObsPortable, effective.CaptureMode, effective.ReplayBufferSeconds),
            selected.Process.Id, selected.Process.FullPath);
        switch (result.Status)
        {
            case RecorderStartStatus.Started:
                Volatile.Write(ref _recordingGamePath, selected.Rule.ExecutablePath);
                _recordingDirectory = folderPlan.PlannedDirectory;
                _nextStorageCheckUtc = _utcNow().AddSeconds(30);
                _recordingGamePid = selected.Process.Id;
                var requested = effective.CaptureMode switch
                {
                    CaptureMode.Replay => "replay buffer",
                    CaptureMode.Both => "recording and replay buffer",
                    _ => "recording"
                };
                Log($"OBS launched with {requested} requested for {selected.Rule.Name}.");
                _recordingRequested = requested;
                RaiseNotification("OBS launched", $"{requested} requested for {selected.Rule.Name}. Check the Recording page for confirmed OBS output status.", false);
                return $"OBS launched ({requested} requested)";
            case RecorderStartStatus.WaitingForWindow:
                return "Waiting for game";
            case RecorderStartStatus.AlreadyRunning:
                Append(notice, result.Message);
                RaiseNotification("OBS already open", result.Message, true);
                return "OBS already open";
            default:
                _failedAttempts++;
                _retryAfter = _utcNow().AddSeconds(30);
                if (_failedAttempts >= 3) _blockedPid = selected.Process.Id;
                Append(notice, result.Message);
                Log("Recorder: " + result.Message);
                if (_failedAttempts >= 3) RaiseNotification("Recording needs attention", result.Message, true);
                return "Needs attention";
        }
    }

    private async Task CheckStorageAsync(string path, AppSettings settings, StringBuilder notice, bool beforeStart)
    {
        var result = await Task.Run(() => _storageCapacity.Query(path)).ConfigureAwait(false);
        if (!result.IsKnown)
        {
            var reason = result.Reason ?? "The volume did not report capacity.";
            var message = $"Recording storage is unknown for '{path}': {reason}";
            Append(notice, message);
            Log(message);
            return;
        }
        var capacity = result.Capacity!;
        var threshold = Math.Max(0, settings.LowStorageThresholdGiB) * 1024d * 1024 * 1024;
        if (capacity.AvailableBytes >= threshold) { _lastStorageWarningKey = ""; return; }
        var key = $"{path}|{threshold}";
        var now = _utcNow();
        var messageText = $"Low recording space: {capacity.AvailableBytes / 1073741824d:0.0} GiB free of {capacity.TotalBytes / 1073741824d:0.0} GiB in '{path}'.";
        Append(notice, messageText);
        if (key == _lastStorageWarningKey && now - _lastStorageWarningUtc < TimeSpan.FromMinutes(10)) return;
        _lastStorageWarningKey = key;
        _lastStorageWarningUtc = now;
        Log(messageText);
        RaiseNotification("Low recording space", messageText, true);
    }

    private string UpdateVibrance(AppSettings settings, IReadOnlyList<MatchedGame> matched, StringBuilder notice)
    {
        if (!settings.VibranceEnabled)
        {
            RestoreVibrance(settings);
            return "Off";
        }
        var game = matched.FirstOrDefault(g => g.Rule.UseVibrance);
        var foregroundPid = 0;
        if (game is not null && settings.VibranceOnlyWhenFocused)
        {
            try { foregroundPid = _platform.GetForegroundProcessId() ?? 0; }
            catch { foregroundPid = 0; }
            game = matched.FirstOrDefault(m => m.Rule.UseVibrance && m.Process.Id == foregroundPid);
        }
        if (game is not null && settings.DisableVibranceWhenHdr)
        {
            bool? hdr;
            try { hdr = _platform.GetHdrState(); } catch { hdr = null; }
            if (hdr is null) { RestoreVibrance(settings); Append(notice, "Colours waiting for HDR status."); return "HDR status unknown"; }
            if (hdr == true)
            {
                RestoreVibrance(settings);
                Append(notice, "Colours paused because HDR is on.");
                return "HDR";
            }
        }
        var target = game is null ? settings.DesktopVibrance : EffectiveGameOptionsResolver.Resolve(settings, game.Rule).VibrancePercent;
        if (target is < 0 or > 100) { Append(notice, "Colour level must be between 0 and 100."); return "Needs attention"; }
        if (!Enum.IsDefined(settings.VibranceTargetMode)) { Append(notice, "Choose a valid colour target."); return "Needs attention"; }
        if (game is null && settings.VibranceTargetMode != VibranceTargetMode.AllDisplays)
        {
            RestoreVibrance(settings);
            return settings.VibranceOnlyWhenFocused ? "Waiting for game focus" : "Desktop";
        }
        if (settings.VibranceTargetMode == VibranceTargetMode.AllDisplays)
            return UpdateAllDisplayVibrance(settings, target, notice);
        else
        {
            RestoreAllDisplaysVibrance(settings);
            if (_vibranceManaged) { Append(notice, "Couldn't restore prior all-display colour ownership yet."); return "Unavailable"; }
            string? device;
            try
            {
                device = settings.VibranceTargetMode switch
                {
                    VibranceTargetMode.SelectedDisplay => settings.VibranceSelectedDisplay,
                    VibranceTargetMode.GameWindowDisplay when game is not null => _platform.GetVisibleGameMonitorIdentity(game.Process.Id),
                    _ => null
                };
            }
            catch { device = null; }
            var desired = device is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>([device], StringComparer.OrdinalIgnoreCase);
            RestoreTargetedVibrance(settings, desired);
            if (device is null)
            {
                Append(notice, "The game window display is unavailable or ambiguous; colour changes are waiting.");
                return "Target unavailable";
            }
            try
            {
                var reads = _platform.ReadVibrance();
                var current = reads.FirstOrDefault(d => string.Equals(d.DeviceName, device, StringComparison.OrdinalIgnoreCase));
                if (current is null) { Append(notice, $"Display '{device}' is unavailable or cannot be mapped."); return "Target unavailable"; }
                if (_vibranceManualOverrides.Contains(device)) return $"Manual value on {device}";
                if (_targetedVibrance.TryGetValue(device, out var previous))
                {
                    if (Math.Abs(current.Percent - previous) > 1)
                    {
                        _targetedVibrance.Remove(device); // A manual/other-app value now owns this display.
                        _vibranceManualOverrides.Add(device);
                        return $"Manual value on {device}";
                    }
                    else if (Math.Abs(previous - target) <= 1) return $"{target}%";
                }
                _platform.SetVibrance(target, device);
                _targetedVibrance[device] = target;
                Log($"Colour level set to {target}% on {device}.");
                return $"{target}% on {device}";
            }
            catch (Exception ex)
            {
                Append(notice, "Colours: " + ex.Message); Log("Colours: " + ex.Message); return "Unavailable";
            }
        }
    }

    private string UpdateAllDisplayVibrance(AppSettings settings, int target, StringBuilder notice)
    {
        try
        {
            var displays = _platform.ReadVibrance();
            if (displays.Count > 0 && displays.All(d => !string.IsNullOrWhiteSpace(d.DeviceName)) &&
                displays.Select(d => d.DeviceName!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == displays.Count)
            {
                if (_vibranceManaged)
                {
                    RestoreAllDisplaysVibrance(settings);
                    if (_vibranceManaged) { Append(notice, "Couldn't restore prior all-display colour ownership yet."); return "Unavailable"; }
                    displays = _platform.ReadVibrance();
                }
                RestoreTargetedVibrance(settings, displays.Select(d => d.DeviceName!).ToHashSet(StringComparer.OrdinalIgnoreCase));
                foreach (var display in displays)
                {
                    var identity = display.DeviceName!;
                    if (_targetedVibrance.TryGetValue(identity, out var previous) && Math.Abs(display.Percent - previous) > 1)
                    {
                        _targetedVibrance.Remove(identity);
                        _vibranceManualOverrides.Add(identity);
                    }
                }
                var changes = displays.Where(d => !_vibranceManualOverrides.Contains(d.DeviceName!) && Math.Abs(d.Percent - target) > 1).ToArray();
                var hasManualDisplay = displays.Any(d => _vibranceManualOverrides.Contains(d.DeviceName!));
                if (changes.Length == 0) return hasManualDisplay ? "Manual display values preserved" : $"{target}%";
                if (!hasManualDisplay)
                {
                    // Preserve NVAPI's all-display transaction and rollback behavior when every target is still ours.
                    _platform.SetVibrance(target);
                    var applied = _platform.ReadVibrance();
                    foreach (var requested in changes)
                    {
                        var changed = applied.FirstOrDefault(d => string.Equals(d.DeviceName, requested.DeviceName, StringComparison.OrdinalIgnoreCase));
                        if (changed is not null && Math.Abs(changed.Percent - target) <= 1)
                            _targetedVibrance[requested.DeviceName!] = target;
                    }
                    Log($"Colour level set to {target}% on all displays.");
                    return $"{target}%";
                }
                // A manual value on one display must not be overwritten by SetAll; update only the remaining screens.
                foreach (var display in changes)
                {
                    _platform.SetVibrance(target, display.DeviceName!);
                    _targetedVibrance[display.DeviceName!] = target;
                }
                return $"{target}% on remaining displays; manual values preserved";
            }

            // Older drivers may not expose Windows display identities. Keep the legacy all-display operation in that case.
            RestoreTargetedVibrance(settings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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
            Append(notice, "Colours: " + ex.Message);
            Log("Colours: " + ex.Message);
            return "Unavailable";
        }
    }

    private string UpdatePower(AppSettings settings, IReadOnlyList<MatchedGame> matched, bool libraryRunning, StringBuilder notice)
    {
        if (!settings.PowerEnabled)
        {
            RestorePower(settings);
            return "Off";
        }
        var gaming = matched.Count > 0 || libraryRunning;
        var target = settings.DesktopPowerPlan;
        var label = "Desktop plan";
        if (gaming)
        {
            target = settings.GamingPowerPlan;
            label = "Gaming plan";
            var match = matched.FirstOrDefault();
            if (match is not null)
            {
                target = EffectiveGameOptionsResolver.Resolve(settings, match.Rule).PowerPlanId;
                var customPowerPlan = match.Rule.PowerPlanId is Guid gamePlan
                    ? gamePlan != Guid.Empty
                    : match.Rule.ProfileId is Guid profileId && settings.Profiles.FirstOrDefault(p => p.Id == profileId)?.PowerPlanId is Guid profilePlan && profilePlan != Guid.Empty;
                if (customPowerPlan)
                    label = match.Rule.Name + " plan";
            }
        }
        try
        {
            if (_platform.GetActivePowerPlan() != target)
            {
                _platform.SetPowerPlan(target);
                Log(gaming ? "Gaming power plan activated." : "Desktop power plan activated.");
            }
            _lastPowerPlan = target;
            _powerManaged = true;
            return label;
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
        RestoreTargetedVibrance(settings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        RestoreAllDisplaysVibrance(settings);
    }

    private void RestoreAllDisplaysVibrance(AppSettings settings)
    {
        if (!_vibranceManaged) return;
        try
        {
            // Respect a later manual change from the user or another application.
            if (_lastVibrance is int previous &&
                _platform.ReadVibrance().All(display => Math.Abs(display.Percent - previous) <= 1))
                _platform.SetVibrance(Math.Clamp(settings.DesktopVibrance, 0, 100));
            _vibranceManaged = false;
            _lastVibrance = null;
        }
        catch (Exception ex) { Log("Couldn't restore colours: " + ex.Message); }
    }

    private void RestoreTargetedVibrance(AppSettings settings, HashSet<string> keep)
    {
        foreach (var (device, applied) in _targetedVibrance.ToArray())
        {
            if (keep.Contains(device)) continue;
            try
            {
                var current = _platform.ReadVibrance().FirstOrDefault(d => string.Equals(d.DeviceName, device, StringComparison.OrdinalIgnoreCase));
                if (current is null) continue; // Keep ownership for a reconnect/retry.
                if (Math.Abs(current.Percent - applied) <= 1)
                    _platform.SetVibrance(Math.Clamp(settings.DesktopVibrance, 0, 100), device);
                else _vibranceManualOverrides.Add(device);
                _targetedVibrance.Remove(device); // Restore succeeded, or a later manual value is respected.
                _restoreFailureLogged.Remove(device);
            }
            catch (Exception ex)
            {
                if (_restoreFailureLogged.Add(device)) Log("Couldn't restore colours on " + device + ": " + ex.Message);
            }
        }
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
        var previous = _lastStatus;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
        if (previous is not null && status.Notice != previous.Notice && !string.IsNullOrWhiteSpace(status.Notice))
            RaiseNotification("Meh Tools needs attention", status.Notice, true);
    }

    private void RaiseNotification(string title, string message, bool warning)
    {
        if (!Volatile.Read(ref _settings).ShowNotifications) return;
        Notify?.Invoke(new AutomationNotification(title, message, warning));
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
public static class AutomationPolicy
{
    public static IReadOnlyList<MatchedGame> MatchGames(IEnumerable<GameRule> rules, IReadOnlyList<RunningProcess> processes)
    {
        var matches = new List<MatchedGame>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ExecutablePath)) continue;
            var match = EvaluateRule(rule, processes).Match;
            if (match is not null) matches.Add(match);
        }
        return matches;
    }

    /// <summary>Returns every process that satisfies each rule so recording ownership can retain its PID.</summary>
    public static IReadOnlyList<MatchedGame> MatchAllGames(IEnumerable<GameRule> rules, IReadOnlyList<RunningProcess> processes)
        => rules.Where(rule => !string.IsNullOrWhiteSpace(rule.ExecutablePath))
            .SelectMany(rule => EvaluateRule(rule, processes).Processes
                .Where(item => item.MatchKind is not null)
                .Select(item => new MatchedGame(rule, item.Process, item.MatchKind)))
            .ToArray();

    public static RunningProcess? MatchRule(GameRule rule, IReadOnlyList<RunningProcess> processes)
        => EvaluateRule(rule, processes).Match?.Process;

    /// <summary>Resolves a unique accessible executable path for report/monitor targeting.</summary>
    public static string? ResolveObservedTarget(GameRule rule, IReadOnlyList<RunningProcess> processes, out string explanation)
    {
        var matched = EvaluateRule(rule, processes).Processes.Where(item => item.MatchKind is not null).ToArray();
        if (matched.Length == 0)
        {
            explanation = "No process matched this game's rule in the opening snapshot; frame capture is unavailable until reopened or restarted.";
            return null;
        }
        var observedPaths = matched.Select(item => ExecutablePathRules.Normalize(item.Process.FullPath)).ToArray();
        if (observedPaths.Any(path => path is null))
        {
            explanation = "At least one matching process has an inaccessible executable path; no unique PresentMon target can be confirmed.";
            return null;
        }
        var paths = observedPaths.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != 1)
        {
            explanation = paths.Length == 0
                ? "A matching process has no accessible executable path; no PresentMon target was selected."
                : "Several matching processes have different executable paths; no unique PresentMon target was selected.";
            return null;
        }
        explanation = "Performance target resolved from the observed matching process: " + paths[0];
        return paths[0];
    }

    /// <summary>One evaluator is used by runtime matching and the read-only detection diagnostics.</summary>
    public static GameDetectionEvaluation EvaluateRule(GameRule rule, IReadOnlyList<RunningProcess> processes,
        IEnumerable<GameRule>? allRules = null)
    {
        var mode = (rule.MatchMode ?? "path").Trim().ToLowerInvariant();
        var exe = rule.ExecutablePath ?? "";
        var aliasPaths = (rule.ExecutableAliases ?? []).Select(ExecutablePathRules.Normalize)
            .Where(path => path is not null).Cast<string>().ToArray();
        var evaluated = processes.Select(process => (Process: process, Kind: MatchKind(rule, process, mode, aliasPaths)))
            .OrderBy(item => item.Kind switch
            {
                GameProcessMatchKind.PrimaryPath => 0,
                GameProcessMatchKind.AliasPath => 1,
                GameProcessMatchKind.NameMode => 2,
                GameProcessMatchKind.FolderMode => 2,
                _ => 3
            })
            .ThenBy(item => item.Process.Id).ToArray();
        var match = evaluated.FirstOrDefault(item => item.Kind is not null);
        MatchedGame? matched = match.Kind is GameProcessMatchKind kind ? new MatchedGame(rule, match.Process, kind) : null;
        var relevant = evaluated.Where(item => item.Kind is not null || IsLikelyCandidate(rule, item.Process, mode))
            .Select(item => new GameProcessExplanation(item.Process, item.Kind,
                item.Kind is GameProcessMatchKind matchKind ? MatchDescription(matchKind) : MismatchDescription(rule, item.Process, mode)))
            .ToArray();
        var competitors = matched is null || allRules is null ? Array.Empty<string>() : allRules
            .Where(other => !ReferenceEquals(other, rule) && !string.IsNullOrWhiteSpace(other.ExecutablePath))
            .Where(other => MatchRule(other, processes)?.Id == matched.Process.Id)
            .Select(other => other.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var summary = matched is not null
            ? $"Matched by {MatchDescription(matched.MatchKind!.Value)}."
            : relevant.Length == 0 ? "No matching or likely candidate process was observed."
            : "No process met this game's matching rule; likely candidates are shown with the reason they did not match.";
        var matchingCount = relevant.Count(item => item.MatchKind is not null);
        if (matched is not null && matchingCount > 1)
            summary += $" {matchingCount - 1} other process(es) also matched; selection prefers primary path, then alias path, then name/folder mode, with lowest PID as tie-break.";
        if (competitors.Length > 0) summary += " Also matches: " + string.Join(", ", competitors) + ". Game rules are evaluated in settings order.";
        return new GameDetectionEvaluation(rule, mode, matched, relevant, competitors, summary);
    }

    private static GameProcessMatchKind? MatchKind(GameRule rule, RunningProcess process, string mode, IReadOnlyList<string> aliasPaths)
    {
        var processPath = ExecutablePathRules.Normalize(process.FullPath);
        var primary = ExecutablePathRules.Normalize(rule.ExecutablePath);
        if (processPath is not null && primary is not null && string.Equals(processPath, primary, StringComparison.OrdinalIgnoreCase))
            return GameProcessMatchKind.PrimaryPath;
        if (processPath is not null && aliasPaths.Any(alias => string.Equals(processPath, alias, StringComparison.OrdinalIgnoreCase)))
            return GameProcessMatchKind.AliasPath;
        if (mode == "name")
        {
            var name = Path.GetFileName(rule.ExecutablePath ?? "");
            if (!string.IsNullOrWhiteSpace(name) && (string.Equals(process.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (processPath is not null && string.Equals(Path.GetFileName(processPath), name, StringComparison.OrdinalIgnoreCase))))
                return GameProcessMatchKind.NameMode;
        }
        else if (mode == "folder")
        {
            var folder = ExecutablePathRules.Normalize(rule.ExecutablePath);
            var parent = folder is null ? null : Path.GetDirectoryName(folder);
            if (processPath is not null && !string.IsNullOrWhiteSpace(parent) && IsInFolder(processPath, parent))
                return GameProcessMatchKind.FolderMode;
        }
        return null;
    }

    private static bool IsLikelyCandidate(GameRule rule, RunningProcess process, string mode)
    {
        var expectedNames = new[] { Path.GetFileName(rule.ExecutablePath ?? "") }
            .Concat((rule.ExecutableAliases ?? []).Select(Path.GetFileName))
            .Where(name => !string.IsNullOrWhiteSpace(name));
        if (expectedNames.Any(name => string.Equals(name, process.Name, StringComparison.OrdinalIgnoreCase) ||
            (process.FullPath is not null && string.Equals(name, Path.GetFileName(process.FullPath), StringComparison.OrdinalIgnoreCase)))) return true;
        if (mode == "folder" && process.FullPath is not null)
        {
            var primary = ExecutablePathRules.Normalize(rule.ExecutablePath);
            var folder = primary is null ? null : Path.GetDirectoryName(primary);
            return !string.IsNullOrWhiteSpace(folder) && IsInFolder(process.FullPath, folder);
        }
        return false;
    }

    private static string MismatchDescription(GameRule rule, RunningProcess process, string mode)
    {
        if (string.IsNullOrWhiteSpace(process.FullPath)) return "Executable path is unknown or inaccessible; an exact path or folder match cannot be confirmed.";
        if (mode == "path") return "Process name is plausible, but its full path does not equal the primary path or any exact alias.";
        if (mode == "folder") return "This process is outside the selected game's folder.";
        return "This process name does not equal the canonical executable name.";
    }

    private static string MatchDescription(GameProcessMatchKind kind) => kind switch
    {
        GameProcessMatchKind.PrimaryPath => "primary executable path",
        GameProcessMatchKind.AliasPath => "exact executable alias path",
        GameProcessMatchKind.NameMode => "process name mode",
        GameProcessMatchKind.FolderMode => "folder mode",
        _ => "unknown rule"
    };

    private static bool IsInFolder(string path, string folder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, folder, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

}

public enum GameProcessMatchKind { PrimaryPath, AliasPath, NameMode, FolderMode }
public sealed record MatchedGame(GameRule Rule, RunningProcess Process, GameProcessMatchKind? MatchKind = null);
public sealed record GameProcessExplanation(RunningProcess Process, GameProcessMatchKind? MatchKind, string Reason);
public sealed record GameDetectionEvaluation(GameRule Rule, string MatchMode, MatchedGame? Match,
    IReadOnlyList<GameProcessExplanation> Processes, IReadOnlyList<string> CompetingGames, string Summary);
