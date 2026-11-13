using JamesTools.Core.Configuration;
using JamesTools.Core.Recording;

internal static class RecordingHealthTests
{
    public static Task Run()
    {
        SettingsRoundTripAndValidation();
        StartupAndRecoveryAreDeduplicated();
        ProgressStallPauseAndJitter();
        GapsResetsAndGenerationsAreConservative();
        IntentionalStopsReplayAndDisabledMode();
        DisconnectAndReconnectResetSamples();
        SettingsAndExpiredStopsPreserveIntent();
        return Task.CompletedTask;
    }

    private static void SettingsRoundTripAndValidation()
    {
        var source = new AppSettings { RecordingHealthAlertsEnabled = true, RecordingHealthSoundEnabled = true, RecordingHealthStallSeconds = 45 };
        var store = new SettingsStore("unused");
        var snapshot = source.Snapshot();
        source.RecordingHealthAlertsEnabled = false;
        source.RecordingHealthSoundEnabled = false;
        source.RecordingHealthStallSeconds = 10;
        Assert(snapshot.RecordingHealthAlertsEnabled && snapshot.RecordingHealthSoundEnabled && snapshot.RecordingHealthStallSeconds == 45,
            "health settings snapshot shared mutable values with its draft");
        var roundTrip = store.Deserialize(store.Serialize(snapshot));
        Assert(roundTrip.RecordingHealthAlertsEnabled && roundTrip.RecordingHealthSoundEnabled && roundTrip.RecordingHealthStallSeconds == 45,
            "health settings did not round-trip or snapshot");
        Assert(new AppSettings().RecordingHealthAlertsEnabled == false && new AppSettings().RecordingHealthSoundEnabled == false,
            "health alerts or sound should default off");
        foreach (var value in new[] { 9, 301 })
        {
            roundTrip.RecordingHealthStallSeconds = value;
            Assert(SettingsValidation.Validate(roundTrip).Any(error => error.Contains("stall threshold", StringComparison.OrdinalIgnoreCase)),
                $"invalid threshold {value} was accepted");
        }
        roundTrip.RecordingHealthStallSeconds = 10;
        Assert(!SettingsValidation.Validate(roundTrip).Any(error => error.Contains("stall threshold", StringComparison.OrdinalIgnoreCase)), "minimum threshold rejected");
        roundTrip.RecordingHealthStallSeconds = 300;
        Assert(!SettingsValidation.Validate(roundTrip).Any(error => error.Contains("stall threshold", StringComparison.OrdinalIgnoreCase)), "maximum threshold rejected");
    }

    private static void StartupAndRecoveryAreDeduplicated()
    {
        var monitor = Connected();
        var request = Guid.NewGuid();
        monitor.BeginRequest(request, CaptureMode.Recording, At(0));
        Check(monitor.Observe(1, Status(false, 0, 0), At(0)).Event is null, "alerted before first observation");
        for (var second = 1; second < 20; second++)
            Check(monitor.Observe(1, Status(false, 0, 0), At(second)).Event is null, "alerted before the 20-second grace");
        Check(monitor.Observe(1, Status(false, 0, 0), At(20)).Event?.Kind == RecordingHealthEventKind.Alert, "missing recording did not alert at grace");
        Check(monitor.Observe(1, Status(false, 0, 0), At(21)).Event is null, "repeated missing output alert");
        var recovered = monitor.Observe(1, Status(true, 1, 100), At(22));
        Check(recovered.State == RecordingHealthState.RecordingProgressing && recovered.Event?.Kind == RecordingHealthEventKind.Recovered,
            "confirmed output did not recover the missing-start episode");
        Check(monitor.Observe(1, Status(false, 2, 110), At(23)).Event?.Kind == RecordingHealthEventKind.Alert,
            "a later stop episode was not re-armed");
        Check(monitor.Observe(1, Status(false, 3, 120), At(24)).Event is null, "stop episode was not deduplicated");
        Check(monitor.Snapshot.State == RecordingHealthState.Attention && monitor.Snapshot.Reason == "Recording stopped.",
            "unexpected stop wording was not neutral");
    }

    private static void ProgressStallPauseAndJitter()
    {
        var monitor = Connected(stallSeconds: 30);
        monitor.Observe(1, Status(true, 0, 100), At(0));
        for (var second = 1; second <= 29; second++)
            Check(monitor.Observe(1, Status(true, second * 1000, 100), At(second)).Event is null, "short byte jitter produced a stall alert");
        var recovered = monitor.Observe(1, Status(true, 29_000, 101), At(29));
        Check(recovered.Event is null && monitor.Snapshot.State == RecordingHealthState.RecordingProgressing, "increased bytes did not refresh the baseline");
        for (var second = 30; second <= 58; second++)
            Check(monitor.Observe(1, Status(true, second * 1000, 101), At(second)).Event is null, "stall fired before the configured duration");
        Check(monitor.Observe(1, Status(true, 59_000, 101), At(59)).Event?.Kind == RecordingHealthEventKind.Alert,
            "unchanged bytes did not alert after 30 supported seconds");
        Check(monitor.Observe(1, Status(true, 60_000, 101), At(60)).Event is null, "stall episode emitted repeated alerts");
        Check(monitor.Observe(1, Status(true, 61_000, 105), At(61)).Event?.Kind == RecordingHealthEventKind.Recovered,
            "byte progress did not publish recovery");

        monitor = Connected(stallSeconds: 30);
        monitor.Observe(1, Status(true, 0, 100), At(0));
        monitor.Observe(1, Status(true, 15_000, 100, paused: true), At(15));
        Check(monitor.Snapshot.State == RecordingHealthState.Paused, "paused health state was not shown");
        monitor.Observe(1, Status(true, 45_000, 100, paused: true), At(45));
        for (var second = 46; second <= 75; second++)
            Check(monitor.Observe(1, Status(true, second * 1000, 100), At(second)).Event is null, "resume did not start a fresh stall window");
        Check(monitor.Observe(1, Status(true, 76_000, 100), At(76)).Event?.Kind == RecordingHealthEventKind.Alert,
            "resumed output did not alert after a fresh sustained window");
    }

    private static void GapsResetsAndGenerationsAreConservative()
    {
        var monitor = Connected(stallSeconds: 10);
        monitor.Observe(1, Status(true, 0, 200), At(0));
        Check(monitor.Observe(1, Status(true, 40_000, 200), At(40)).Event is null, "long observation gap caused a confident stall");
        for (var second = 41; second <= 49; second++)
            Check(monitor.Observe(1, Status(true, second * 1000, 200), At(second)).Event is null, "long gap retained stale stall evidence");
        Check(monitor.Observe(1, Status(true, 50_000, 200), At(50)).Event?.Kind == RecordingHealthEventKind.Alert,
            "fresh samples did not establish a later stall");

        monitor = Connected();
        monitor.Observe(1, Status(true, 0, 100), At(0));
        Check(monitor.Observe(1, Status(false, 1, 100), At(40)).Event is null,
            "one sample after a long gap was treated as a confidently observed stop");
        monitor.Observe(1, Status(true, 2, 120), At(41));
        Check(monitor.Observe(1, Status(false, 3, 125), At(42)).Event?.Kind == RecordingHealthEventKind.Alert,
            "freshly observed stop was missed after the gap reset");

        monitor = Connected(stallSeconds: 10);
        monitor.Observe(1, Status(true, 20_000, 800), At(0));
        monitor.Observe(1, Status(true, 21_000, 810), At(1));
        Check(monitor.Observe(1, Status(true, 0, 0), At(2)).Event is null, "counter reset caused an instant stall");
        for (var second = 3; second <= 11; second++)
            Check(monitor.Observe(1, Status(true, second * 1000, 0), At(second)).Event is null, "counter reset failed to establish a fresh baseline");
        Check(monitor.Observe(1, Status(true, 12_000, 0), At(12)).Event?.Kind == RecordingHealthEventKind.Alert,
            "stall did not trigger after reset baseline matured");

        monitor = Connected(stallSeconds: 10);
        monitor.Observe(1, Status(true, 20_000, 800), At(0));
        monitor.Observe(1, Status(true, 21_000, 810), At(1));
        Check(monitor.Observe(1, Status(true, 22_000, 0), At(2)).Event is null, "byte counter decrease did not reset the baseline");
        for (var second = 3; second <= 11; second++)
            Check(monitor.Observe(1, Status(true, 22_000 + (second - 2) * 1000, 0), At(second)).Event is null, "byte reset baseline matured too early");
        Check(monitor.Observe(1, Status(true, 32_000, 0), At(12)).Event?.Kind == RecordingHealthEventKind.Alert,
            "byte reset baseline did not mature");

        monitor = Connected(stallSeconds: 10);
        monitor.Observe(1, Status(true, 20_000, 800), At(0));
        monitor.Observe(1, Status(true, 21_000, 810), At(1));
        Check(monitor.Observe(1, Status(true, 0, 820), At(2)).Event is null, "duration reset did not reset the baseline");
        for (var second = 3; second <= 11; second++)
            Check(monitor.Observe(1, Status(true, second * 1000, 820), At(second)).Event is null, "duration reset baseline matured too early");
        Check(monitor.Observe(1, Status(true, 12_000, 820), At(12)).Event?.Kind == RecordingHealthEventKind.Alert,
            "duration reset baseline did not mature");

        monitor = Connected();
        var oldGeneration = monitor.ObservationGeneration;
        monitor.Observe(1, Status(true, 0, 10), At(0), oldGeneration);
        monitor.BeginRequest(Guid.NewGuid(), CaptureMode.Recording, At(1));
        Check(monitor.Observe(1, Status(false, 0, 0), At(2), oldGeneration).Event is null,
            "late sample from a previous recording generation was accepted");
    }

    private static void IntentionalStopsReplayAndDisabledMode()
    {
        var monitor = Connected();
        var id = Guid.NewGuid();
        monitor.BeginRequest(id, CaptureMode.Recording, At(0));
        monitor.Observe(1, Status(true, 0, 100), At(0));
        monitor.SetStopIntent(id, true, At(1));
        Check(monitor.Observe(1, Status(false, 1, 110), At(2)).Event is null, "intentional stop alerted");

        id = Guid.NewGuid();
        monitor.BeginRequest(id, CaptureMode.Recording, At(3));
        monitor.Observe(1, Status(true, 2, 120), At(3));
        monitor.SetStopIntent(id, true, At(4));
        monitor.SetStopIntent(id, false, At(5)); // Stop command failed; monitoring is restored.
        Check(monitor.Observe(1, Status(false, 3, 120), At(6)).Event?.Kind == RecordingHealthEventKind.Alert,
            "failed stop request suppressed an unexpected stop");

        monitor = Connected();
        monitor.BeginRequest(Guid.NewGuid(), CaptureMode.Replay, At(0));
        for (var second = 0; second <= 60; second++)
            Check(monitor.Observe(1, Status(false, 0, 0), At(second)).Event is null, "replay-only request raised a recording alert");

        monitor = new RecordingHealthMonitor();
        monitor.Connected(1, At(0));
        monitor.BeginRequest(Guid.NewGuid(), CaptureMode.Recording, At(0));
        Check(monitor.Observe(1, Status(true, 0, 100), At(0)).Event is null && monitor.Snapshot.State == RecordingHealthState.Disabled,
            "disabled mode produced an alert or active health state");
    }

    private static void DisconnectAndReconnectResetSamples()
    {
        var monitor = Connected(stallSeconds: 10);
        monitor.Observe(1, Status(true, 0, 100), At(0));
        monitor.Disconnected(1, connectionLost: true);
        Check(monitor.Snapshot.State == RecordingHealthState.ConnectionLost, "disconnect did not show health unknown");
        Check(monitor.Observe(1, Status(false, 0, 0), At(30)).Event is null, "stale disconnected sample was accepted");
        monitor.Connected(2, At(31));
        Check(monitor.Observe(2, Status(true, 1, 100), At(31)).Event is null, "reconnect treated old counters as a stall");
        for (var second = 32; second <= 40; second++)
            Check(monitor.Observe(2, Status(true, second * 1000, 100), At(second)).Event is null, "reconnect did not reset stall evidence");
        Check(monitor.Observe(2, Status(true, 41_000, 100), At(41)).Event?.Kind == RecordingHealthEventKind.Alert,
            "reconnected output did not alert after sustained fresh samples");
    }

    private static RecordingHealthMonitor Connected(int stallSeconds = 30)
    {
        var monitor = new RecordingHealthMonitor();
        monitor.Configure(true, stallSeconds, At(0));
        monitor.Connected(1, At(0));
        return monitor;
    }

    private static void SettingsAndExpiredStopsPreserveIntent()
    {
        var monitor = Connected(stallSeconds: 10);
        var request = Guid.NewGuid();
        monitor.BeginRequest(request, CaptureMode.Recording, At(0));
        monitor.Configure(true, 15, At(1));
        Check(monitor.CurrentRequestId == request, "changing thresholds lost an outstanding start request");
        for (var second = 1; second <= 21; second++) monitor.Observe(1, Status(false, 0, 0), At(second));
        Check(monitor.Snapshot.State == RecordingHealthState.Attention, "saved settings stopped monitoring a pending start");
        var paused = monitor.Observe(1, Status(true, 1000, 100, paused: true), At(22));
        Check(paused.State == RecordingHealthState.Paused && paused.Event?.Kind == RecordingHealthEventKind.Recovered,
            "confirmed paused output did not recover a missing-start alert");
        monitor.EndRequest(request, intentional: true, At(23));
        monitor.Configure(true, 10, At(24));
        monitor.Disconnected(1);
        monitor.Connected(2, At(25));
        for (var second = 25; second <= 50; second++)
            Check(monitor.Observe(2, Status(false, 0, 0), At(second)).Event is null, "settings/reconnect revived an intentionally ended request");

        request = Guid.NewGuid();
        monitor.BeginRequest(request, CaptureMode.Recording, At(51));
        monitor.Observe(2, Status(true, 1000, 100), At(51));
        monitor.SetStopIntent(request, intentional: true, At(52));
        RecordingHealthEvent? lastEvent = null;
        for (var second = 52; second <= 95; second++)
            lastEvent = monitor.Observe(2, Status(true, second * 1000, 100), At(second)).Event ?? lastEvent;
        Check(lastEvent?.Kind == RecordingHealthEventKind.Alert, "unfinished stop intent suppressed monitoring forever");
    }

    private static ObsRecordStatus Status(bool active, long duration, long bytes, bool paused = false) => new(active, paused, duration, bytes);
    private static TimeSpan At(int second) => TimeSpan.FromSeconds(second);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Assert(bool condition, string message) => Check(condition, message);
}
