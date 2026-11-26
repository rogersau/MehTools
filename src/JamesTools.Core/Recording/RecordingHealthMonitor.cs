using JamesTools.Core.Configuration;

namespace JamesTools.Core.Recording;

public enum RecordingHealthState
{
    Disabled,
    NeedsObsConnection,
    ConnectionLost,
    WaitingForRecording,
    RecordingProgressing,
    Paused,
    Attention
}

public enum RecordingHealthEventKind { Alert, Recovered }
public sealed record RecordingHealthEvent(RecordingHealthEventKind Kind, string Reason);
public sealed record RecordingHealthSnapshot(RecordingHealthState State, string Reason, RecordingHealthEvent? Event = null);

/// <summary>Deterministic output-health policy. Callers supply monotonic elapsed time and confirmed OBS counters.</summary>
public sealed class RecordingHealthMonitor
{
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumSupportingSampleGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopIntentLifetime = TimeSpan.FromSeconds(30);

    private bool _enabled;
    private int _stallSeconds = 30;
    private bool _connected;
    private bool _connectionLost;
    private long _connectionGeneration;
    private long _observationGeneration;
    private Guid? _requestId;
    private CaptureMode _requestedMode;
    private TimeSpan _requestSince;
    private bool _intentionalStop;
    private TimeSpan _stopIntentSince;
    private bool _activeObserved;
    private bool _paused;
    private bool _hasSample;
    private TimeSpan _lastSampleAt;
    private TimeSpan _stagnantSince;
    private long _lastBytes;
    private long _lastDuration;
    private string? _issue;

    public RecordingHealthSnapshot Snapshot => BuildSnapshot(null);
    public Guid? CurrentRequestId => _requestId;
    public long ObservationGeneration => _observationGeneration;

    public RecordingHealthSnapshot Configure(bool enabled, int stallSeconds, TimeSpan now)
    {
        if (stallSeconds is < 10 or > 300) throw new ArgumentOutOfRangeException(nameof(stallSeconds));
        if (_enabled == enabled && _stallSeconds == stallSeconds) return Snapshot;
        _enabled = enabled;
        _stallSeconds = stallSeconds;
        ResetObservation(clearRequest: false);
        if (_requestId is not null) _requestSince = now;
        return BuildSnapshot(null);
    }

    public RecordingHealthSnapshot Connected(long generation, TimeSpan now)
    {
        if (generation < _connectionGeneration) return Snapshot;
        if (!_connected || generation != _connectionGeneration)
        {
            _connected = true;
            _connectionLost = false;
            _connectionGeneration = generation;
            ResetObservation(clearRequest: false);
            if (_requestId is not null) _requestSince = now;
        }
        return BuildSnapshot(null);
    }

    public RecordingHealthSnapshot Disconnected(long generation, bool connectionLost = false)
    {
        if (generation != _connectionGeneration) return Snapshot;
        _connected = false;
        _connectionLost = connectionLost;
        _intentionalStop = false;
        ResetObservation(clearRequest: false);
        _issue = null;
        return BuildSnapshot(null);
    }

    public RecordingHealthSnapshot BeginRequest(Guid requestId, CaptureMode mode, TimeSpan now)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A recording request needs an identity.", nameof(requestId));
        if (_requestId == requestId) return Snapshot;
        _requestId = requestId;
        _requestedMode = mode;
        _requestSince = now;
        _intentionalStop = false;
        ResetObservation(clearRequest: false);
        _issue = null;
        return BuildSnapshot(null);
    }

    public RecordingHealthSnapshot SetStopIntent(Guid requestId, bool intentional, TimeSpan now)
    {
        if (_requestId != requestId) return Snapshot;
        _intentionalStop = intentional;
        if (intentional) _stopIntentSince = now;
        return BuildSnapshot(null);
    }

    public RecordingHealthSnapshot EndRequest(Guid requestId, bool intentional, TimeSpan now)
    {
        if (_requestId != requestId) return Snapshot;
        _intentionalStop = intentional;
        if (intentional) _stopIntentSince = now;
        _requestId = null;
        _issue = null;
        _observationGeneration++;
        return BuildSnapshot(null);
    }

    public RecordingHealthSnapshot Observe(long connectionGeneration, ObsRecordStatus status, TimeSpan now, long? observationGeneration = null)
    {
        if (!_enabled || !_connected || connectionGeneration != _connectionGeneration ||
            observationGeneration is long generation && generation != _observationGeneration) return Snapshot;
        if (_requestId is not null && _requestedMode is CaptureMode.Replay or CaptureMode.WardogsHighlights)
        {
            _hasSample = false;
            _activeObserved = false;
            _issue = null;
            return BuildSnapshot(null);
        }
        if (_hasSample)
        {
            var gap = now - _lastSampleAt;
            if (gap < TimeSpan.Zero) return Snapshot; // Ignore stale async continuations.
            if (gap > MaximumSupportingSampleGap)
            {
                _stagnantSince = now;
                _activeObserved = false;
                _paused = false;
                if (_requestId is not null) _requestSince = now;
                if (_issue?.StartsWith("Possible stalled output", StringComparison.Ordinal) == true) _issue = null;
            }
        }

        if (_intentionalStop && now - _stopIntentSince > StopIntentLifetime)
        {
            _intentionalStop = false;
        }

        RecordingHealthEvent? transition = null;
        if (!status.Active)
        {
            if (_activeObserved && !_intentionalStop)
                transition = SetIssue("Recording stopped.");
            else if (_requestId is not null && RequestsRecording(_requestedMode) && !_intentionalStop && now - _requestSince >= StartGrace)
                transition = SetIssue("OBS has not confirmed recording output after the 20-second start grace.");

            _activeObserved = false;
            _hasSample = true;
            _lastSampleAt = now;
            _lastBytes = status.Bytes;
            _lastDuration = status.DurationMilliseconds;
            if (_intentionalStop)
            {
                _requestId = null;
                _intentionalStop = false;
                transition = ClearIssue();
            }
            return BuildSnapshot(transition);
        }

        var starting = !_activeObserved;
        _activeObserved = true;
        if (status.Paused)
        {
            _paused = true;
            _hasSample = true;
            _lastSampleAt = now;
            _lastBytes = status.Bytes;
            _lastDuration = status.DurationMilliseconds;
            _stagnantSince = now;
            transition = ClearIssue();
            return BuildSnapshot(transition);
        }

        var resumed = _paused;
        _paused = false;
        if (_issue is not null && !_issue.StartsWith("Possible stalled output", StringComparison.Ordinal))
            transition = ClearIssue();
        var reset = starting || resumed || !_hasSample || status.Bytes < _lastBytes || status.DurationMilliseconds < _lastDuration;
        var progressed = _hasSample && status.Bytes > _lastBytes;
        if (_intentionalStop)
        {
            _stagnantSince = now;
        }
        else if (reset || progressed)
        {
            _stagnantSince = now;
            if (_issue?.StartsWith("Possible stalled output", StringComparison.Ordinal) == true)
                transition = ClearIssue();
        }
        else if (now - _stagnantSince >= TimeSpan.FromSeconds(_stallSeconds))
        {
            transition = SetIssue($"Possible stalled output: OBS-reported bytes have not increased for {_stallSeconds} seconds. Muxer buffering can cause this signal.");
        }

        _hasSample = true;
        _lastSampleAt = now;
        _lastBytes = status.Bytes;
        _lastDuration = status.DurationMilliseconds;
        return BuildSnapshot(transition);
    }

    private static bool RequestsRecording(CaptureMode mode) => mode is CaptureMode.Recording or CaptureMode.Both;

    private RecordingHealthEvent? SetIssue(string reason)
    {
        if (_issue is not null)
        {
            if (_issue.StartsWith("Possible stalled output", StringComparison.Ordinal) && reason == "Recording stopped.")
                _issue = reason;
            return null;
        }
        _issue = reason;
        return new RecordingHealthEvent(RecordingHealthEventKind.Alert, reason);
    }

    private RecordingHealthEvent? ClearIssue()
    {
        if (_issue is null) return null;
        var prior = _issue;
        _issue = null;
        return new RecordingHealthEvent(RecordingHealthEventKind.Recovered, "Recording health recovered. " + prior);
    }

    private void ResetObservation(bool clearRequest)
    {
        _observationGeneration++;
        _activeObserved = false;
        _paused = false;
        _hasSample = false;
        _lastSampleAt = _stagnantSince = TimeSpan.Zero;
        _lastBytes = _lastDuration = 0;
        if (clearRequest) _requestId = null;
        _intentionalStop = false;
        _issue = null;
    }

    private RecordingHealthSnapshot BuildSnapshot(RecordingHealthEvent? transition)
    {
        if (!_enabled) return new(RecordingHealthState.Disabled, "Recording health alerts are off.", transition);
        if (!_connected) return _connectionLost
            ? new(RecordingHealthState.ConnectionLost, "OBS connection lost; output health is unknown.", transition)
            : new(RecordingHealthState.NeedsObsConnection, "Connect explicitly to OBS to monitor recording output.", transition);
        if (_issue is not null) return new(RecordingHealthState.Attention, _issue, transition);
        if (_activeObserved && _paused)
            return new(RecordingHealthState.Paused, "Recording is paused; output-stall monitoring is suspended.", transition);
        if (_activeObserved && _hasSample)
            return new(RecordingHealthState.RecordingProgressing, "OBS reports recording active; output counters are being monitored.", transition);
        if (_activeObserved) return new(RecordingHealthState.RecordingProgressing, "OBS reports recording active.", transition);
        return new(RecordingHealthState.WaitingForRecording, _requestId is null
            ? "Waiting for a recording; replay-only output is not monitored."
            : "Waiting for OBS to confirm recording output.", transition);
    }
}
