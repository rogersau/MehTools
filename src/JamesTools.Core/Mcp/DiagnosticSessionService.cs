using System.Diagnostics;

namespace JamesTools.Core.Mcp;

/// <summary>
/// Shared, demand-driven telemetry. Profiles choose default intervals (general/storage 5s,
/// gaming/cpu/gpu 1s), not additional collectors. The latest 40 snapshots (6 KiB each)
/// and 64 metric aggregates over the full recording are retained; session JSON is <=300 KiB.
/// Live responses retain up to 128 prioritized metrics within 96 KiB. History is <=300 KiB.
/// At most four recordings and twelve retained sessions share a storage directory.
/// Retention is seven days, with a 32 MiB ceiling including backups/staging files.
/// The backend belongs to the host and is never disposed here.
/// </summary>
public sealed class DiagnosticSessionService : IDiagnosticSessions
{
    readonly IDiagnosticBackend backend;
    readonly SessionStore store;
    readonly object gate = new();
    readonly Dictionary<Guid, State> owned = [];
    readonly SemaphoreSlim wake = new(0, 1), sampleGate = new(1, 1), eventGate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    Task? worker, disposal;
    Task<MetricsSnapshot>? flight;
    MetricsSnapshot? cached;
    long cachedAt;
    bool disposed;
    sealed class State(DiagnosticSession session, long startedAt)
    {
        internal DiagnosticSession Value = session;
        internal long Deadline = startedAt + (long)session.DurationSeconds * Stopwatch.Frequency;
        internal long Due = startedAt;
        internal Task? Finish;
    }

    public DiagnosticSessionService(IDiagnosticBackend backend, string storageDirectory)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        store = new SessionStore(storageDirectory);
        try { _ = store.Snapshot(); } catch { store.Dispose(); throw; }
    }
    void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    void Signal() { if (wake.CurrentCount == 0) wake.Release(); }

    public async Task<MetricsSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken)
    {
        lock (gate) Check(cancellationToken);
        var result = await SharedSampleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return result with { Metrics = result.Metrics.ToArray(), Warnings = result.Warnings.ToArray() };
    }

    Task<MetricsSnapshot> SharedSampleAsync(DateTimeOffset? notBefore = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cached is not null && Stopwatch.GetElapsedTime(cachedAt) < TimeSpan.FromSeconds(1) && (notBefore is null || cached.TimestampUtc >= notBefore)) return Task.FromResult(cached);
            if (flight is { IsCompleted: false }) return flight;
            // Task.Run also bounds a backend which blocks before returning its Task.
            return flight = Task.Run(async () =>
            {
                MetricsSnapshot sample;
                try { sample = await SampleBackendAsync().ConfigureAwait(false); }
                catch (Exception ex) { sample = TelemetryData.Failure(ex); }
                lock (gate) { cached = sample; cachedAt = Stopwatch.GetTimestamp(); }
                return sample;
            });
        }
    }

    async Task<MetricsSnapshot> SampleBackendAsync()
    {
        if (!await sampleGate.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token).ConfigureAwait(false))
            throw new TimeoutException("The telemetry backend is busy or unresponsive.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Task<MetricsSnapshot>? operation = null;
        var deferred = false;
        try
        {
            operation = Task.Run(() => backend.SampleAsync(timeout.Token));
            return TelemetryData.Normalize(await operation.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token).ConfigureAwait(false), live: true);
        }
        finally
        {
            timeout.Cancel();
            if (operation is { IsCompleted: false })
            {
                deferred = true;
                // Do not allow a second backend call if a provider ignores cancellation.
                _ = operation.ContinueWith(t => { _ = t.Exception; timeout.Dispose(); sampleGate.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (!deferred) { timeout.Dispose(); sampleGate.Release(); }
        }
    }

    public Task<DiagnosticSession> StartAsync(int duration, string profile, int? samplingIntervalSeconds, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Check(cancellationToken);
            if (duration is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be 1..3600 seconds.");
            if (profile is null || !TelemetryData.Profiles.Contains(profile)) throw new ArgumentException("Profile must be general, gaming, cpu, gpu or storage.", nameof(profile));
            var interval = samplingIntervalSeconds ?? (profile is "general" or "storage" ? 5 : 1);
            if (interval is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(samplingIntervalSeconds), "Interval must be 1..30 seconds.");
            var startedAt = Stopwatch.GetTimestamp();
            var session = new DiagnosticSession(1, Guid.NewGuid(), profile, DateTimeOffset.UtcNow, null, "recording", duration, interval,
                [], [], [], [], ["Frame timing is unsupported without a scoped game capture. Statistics cover all accepted samples; only the latest 40 snapshots are retained."]);
            store.Begin(session); // Durable acceptance before returning; request cancellation never owns the recording.
            PruneOwned(reserve: 1);
            owned.Add(session.Id, new State(session, startedAt));
            worker ??= Task.Run(RunAsync);
            Signal();
            return Task.FromResult(TelemetryData.Copy(session));
        }
    }

    public async Task<DiagnosticSession> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        Task? finish;
        DiagnosticSession session;
        State? state;
        lock (gate)
        {
            Check(cancellationToken);
            session = Find(id);
            owned.TryGetValue(id, out state);
            finish = state?.Finish;
        }
        if (finish is not null) await finish.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate) return TelemetryData.Copy(state?.Value ?? session);
    }
    DiagnosticSession Find(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty session ID is required.", nameof(id));
        PruneOwned();
        if (owned.TryGetValue(id, out var state)) return state.Value;
        return store.Snapshot().FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException("The session is unknown or has expired from retention.");
    }

    void PruneOwned(int reserve = 0)
    {
        foreach (var state in owned.Values.Where(s => s.Value.Status != "recording" && s.Finish is { IsCompleted: true } &&
                     s.Value.StartedUtc < DateTimeOffset.UtcNow - SessionStore.Retention).ToArray()) owned.Remove(state.Value.Id);
        while (owned.Count + reserve > SessionStore.MaxSessions)
        {
            var oldest = owned.Values.Where(s => s.Value.Status != "recording" && s.Finish is { IsCompleted: true }).MinBy(s => s.Value.EndedUtc);
            if (oldest is null) break;
            owned.Remove(oldest.Value.Id);
        }
    }

    public async Task<DiagnosticSession> StopAsync(Guid id, CancellationToken cancellationToken)
    {
        Task? finish;
        State? stoppedState;
        lock (gate)
        {
            Check(cancellationToken);
            var session = Find(id);
            if (!owned.TryGetValue(id, out var state))
            {
                if (session.Status == "recording") throw new NotSupportedException("Stopping a session owned by another host is unsupported; use its originating connection.");
                return TelemetryData.Copy(session);
            }
            stoppedState = state;
            finish = state.Finish;
            if (state.Value.Status == "recording") finish = BeginFinish(state, "stopped", null);
            Signal();
        }
        if (finish is not null) await finish.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate) return TelemetryData.Copy(stoppedState.Value);
    }

    public Task<IReadOnlyList<DiagnosticSession>> ListAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Check(cancellationToken);
            IReadOnlyList<DiagnosticSession> result = store.Snapshot().Select(s => TelemetryData.Copy(s, summary: true)).ToArray();
            return Task.FromResult(result);
        }
    }

    async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                State[] due;
                TimeSpan delay;
                lock (gate)
                {
                    var now = Stopwatch.GetTimestamp();
                    var active = owned.Values.Where(s => s.Value.Status == "recording").ToArray();
                    foreach (var state in active.Where(s => now >= s.Deadline)) BeginFinish(state, "completed", null);
                    active = active.Where(s => s.Value.Status == "recording").ToArray();
                    if (active.Length == 0) return;
                    due = active.Where(s => now >= s.Due).ToArray();
                    delay = TimeSpan.FromSeconds(Math.Max(0, (active.Min(s => Math.Min(s.Due, s.Deadline)) - now) / (double)Stopwatch.Frequency));
                }
                if (due.Length == 0) { await wake.WaitAsync(delay, lifetime.Token).ConfigureAwait(false); continue; }
                var sample = await SharedSampleAsync(due.Max(s => s.Value.StartedUtc)).ConfigureAwait(false);
                lock (gate)
                {
                    var now = Stopwatch.GetTimestamp();
                    foreach (var state in due.Where(s => s.Value.Status == "recording"))
                    {
                        if (now >= state.Deadline) { BeginFinish(state, "completed", "The last telemetry operation exceeded the recording deadline and was omitted."); continue; }
                        if (sample.TimestampUtc < state.Value.StartedUtc)
                        {
                            var warnings = state.Value.Warnings.ToList();
                            AddWarning(warnings, "A cached telemetry snapshot predating the recording start was omitted.");
                            state.Value = state.Value with { Warnings = warnings.ToArray() };
                            state.Due = now + Stopwatch.Frequency;
                            continue;
                        }
                        state.Value = SessionRecording.Append(state.Value, sample);
                        state.Due = now + (long)state.Value.SamplingIntervalSeconds * Stopwatch.Frequency;
                        var failed = sample.Metrics.Any(m => m.Name == "telemetry" && m.Status is "failure" or "permission_denied" or "unavailable" or "unsupported");
                        try { store.Save(state.Value); }
                        catch (Exception ex) { BeginFinish(state, "failed", "Persistence " + TelemetryData.Status(ex) + ": " + TelemetryData.SafeReason(ex)); continue; }
                        if (failed) BeginFinish(state, "failed", "Telemetry collection failed; see the explicit metric status and reason.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (gate) foreach (var state in owned.Values.Where(s => s.Value.Status == "recording")) BeginFinish(state, "failed", "Sampling loop " + TelemetryData.Status(ex) + ": " + TelemetryData.SafeReason(ex));
        }
        finally
        {
            lock (gate) worker = !disposed && owned.Values.Any(s => s.Value.Status == "recording") ? Task.Run(RunAsync) : null;
        }
    }

    static void AddWarning(List<string> warnings, string warning)
    {
        warning = TelemetryData.Text(warning);
        if (!warnings.Contains(warning)) { if (warnings.Count == 16) warnings.RemoveAt(0); warnings.Add(warning); }
    }
    Task BeginFinish(State state, string status, string? warning)
    {
        if (state.Finish is not null) return state.Finish;
        var warnings = state.Value.Warnings.ToList();
        if (warning is not null) AddWarning(warnings, warning);
        state.Value = state.Value with { Status = status, EndedUtc = DateTimeOffset.UtcNow, Warnings = warnings.ToArray() };
        // Capture the end now; event lookup and durable I/O are not part of the recording interval.
        return state.Finish = Task.Run(() => FinalizeAsync(state));
    }
    async Task FinalizeAsync(State state)
    {
        DiagnosticEvent[] events = [];
        var warnings = state.Value.Warnings.ToList();
        if (!lifetime.IsCancellationRequested)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            Task<EventLogResult>? query = null;
            var acquired = false;
            var deferred = false;
            try
            {
                var start = state.Value.StartedUtc; var end = state.Value.EndedUtc!.Value;
                await eventGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                acquired = true;
                query = Task.Run(() => backend.GetEventLogsAsync(new(start, end, Limit: 50), timeout.Token));
                var result = await query.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token).ConfigureAwait(false);
                if (!TelemetryData.Good(result.Status)) AddWarning(warnings, $"Notable events: {TelemetryData.Text(result.Status, 32)}.");
                if (result.Truncated) AddWarning(warnings, "Notable event lookup was truncated at 50 events.");
                events = result.Events.Where(e => e is not null && e.TimestampUtc >= start && e.TimestampUtc <= end &&
                    e.Severity?.ToLowerInvariant() is "warning" or "error" or "critical").Take(32)
                    .Select(e => e with { TimestampUtc = e.TimestampUtc.ToUniversalTime(), Source = TelemetryData.Text(e.Source, 96),
                        Severity = e.Severity.ToLowerInvariant(), Category = TelemetryData.Text(e.Category, 96), Summary = TelemetryData.Text(e.Summary) }).ToArray();
                foreach (var w in result.Warnings.Take(4)) AddWarning(warnings, "Notable events: " + TelemetryData.Text(w, 220));
            }
            catch (Exception ex) { AddWarning(warnings, $"Notable events {TelemetryData.Status(ex)}: {TelemetryData.SafeReason(ex)}"); }
            finally
            {
                timeout.Cancel();
                if (query is { IsCompleted: false })
                {
                    deferred = true;
                    _ = query.ContinueWith(t => { _ = t.Exception; timeout.Dispose(); eventGate.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (!deferred) { timeout.Dispose(); if (acquired) eventGate.Release(); }
            }
        }
        else AddWarning(warnings, "Notable events unavailable during service shutdown.");
        lock (gate)
        {
            state.Value = TelemetryData.BoundSession(state.Value with { NotableEvents = events, Warnings = warnings.ToArray(), Findings = SessionFindings.WithAggregates(state.Value.Findings, state.Value.Statistics) });
            try { store.Save(state.Value); }
            catch (Exception ex)
            {
                AddWarning(warnings, "Final persistence " + TelemetryData.Status(ex) + ": " + TelemetryData.SafeReason(ex));
                state.Value = state.Value with { Status = "failed", Warnings = warnings.ToArray() };
                // Keep the last valid primary/backup. A later start recovers it as interrupted.
            }
            finally { try { store.Release(state.Value.Id); } catch (Exception ex) { AddWarning(warnings, "Storage lease release " + TelemetryData.Status(ex) + ": " + TelemetryData.SafeReason(ex)); state.Value = state.Value with { Warnings = warnings.ToArray() }; } }
        }
    }

    public Task<HistoricalMetrics> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Check(cancellationToken);
            ArgumentNullException.ThrowIfNull(query);
            if (query.Limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query), "History limit must be 1..500.");
            var to = query.ToUtc ?? DateTimeOffset.UtcNow;
            var from = query.FromUtc ?? to - SessionStore.Retention;
            if (!TelemetryData.Utc(from) || !TelemetryData.Utc(to) || from >= to || to - from > SessionStore.Retention ||
                (query.ComparePrevious && from.UtcTicks < (to - from).Ticks)) throw new ArgumentException("History requires an increasing UTC range of at most seven days.", nameof(query));
            var names = query.Metrics?.ToArray();
            if (names is not null && (names.Length is < 1 or > 32 || names.Any(n => !TelemetryData.Key(n)) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length))
                throw new ArgumentException("Specify 1..32 distinct exact metric names; wildcards are unsupported.", nameof(query));
            var sessions = store.Snapshot();
            var warnings = store.Warnings().ToList();
            if (query.ComparePrevious) AddWarning(warnings, "Previous statistics use only available retained data in the adjacent UTC range. Missing coverage is not a zero baseline.");
            if (sessions.Any(s => s.Warnings.Any(w => w.Contains("Older snapshots", StringComparison.Ordinal))))
                AddWarning(warnings, "History contains retained sample windows only. Per-session full-recording aggregates cannot be filtered into arbitrary time ranges.");
            var snapshots = sessions.SelectMany(s => s.Samples).Concat(SessionHistory.ReadLegacy(store.Root, warnings))
                .GroupBy(s => s.TimestampUtc).Select(g => new MetricsSnapshot(1, g.Key,
                    g.SelectMany(s => s.Metrics).Distinct().ToArray(), g.SelectMany(s => s.Warnings).Distinct().Take(8).ToArray())).ToArray();
            MetricsSnapshot[] Select(DateTimeOffset a, DateTimeOffset b) => snapshots.Where(s => s.TimestampUtc >= a && s.TimestampUtc < b)
                .Select(s => s with { Metrics = s.Metrics.Where(m => m.TimestampUtc >= a && m.TimestampUtc < b && (names is null || names.Contains(m.Name, StringComparer.OrdinalIgnoreCase))).ToArray(), Warnings = s.Warnings.ToArray() })
                .Where(s => s.Metrics.Count > 0).OrderByDescending(s => s.TimestampUtc).ToArray();
            var selected = Select(from, to);
            var previous = query.ComparePrevious ? Select(from - (to - from), from) : [];
            if (selected.Concat(previous).SelectMany(s => s.Metrics).Select(m => (m.Name, m.Unit)).Distinct().Take(TelemetryData.MaxStatistics + 1).Count() > TelemetryData.MaxStatistics)
                AddWarning(warnings, "History aggregate identities are limited to 64 per range; returned metric samples retain their explicit names and statuses.");
            // Limit samples and bytes independently. Aggregates use all matching retained data.
            var bounded = new List<MetricsSnapshot>(); var bytes = 0;
            foreach (var sample in selected.Take(query.Limit))
            {
                var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sample).Length;
                if (bytes + size > 240 * 1024) break;
                bytes += size; bounded.Add(sample);
            }
            if (bounded.Count < selected.Length) AddWarning(warnings, "Returned history samples were limited; statistics use all matching retained samples with the same filters and UTC bounds.");
            var result = new HistoricalMetrics(1, bounded, TelemetryData.Aggregate(selected), TelemetryData.Aggregate(previous), bounded.Count < selected.Length, warnings.Take(16).ToArray());
            while (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result).Length > 300 * 1024 && bounded.Count > 0)
            {
                bounded.RemoveAt(bounded.Count - 1);
                result = result with { Truncated = true, Warnings = result.Warnings.Append("History response reached its 300 KiB DTO budget; aggregates are unchanged.").Distinct().Take(16).ToArray() };
            }
            return Task.FromResult(result);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            lifetime.Cancel(); Signal();
            foreach (var state in owned.Values.Where(s => s.Value.Status == "recording")) BeginFinish(state, "stopped", "Recording finalized during service shutdown.");
            var pending = owned.Values.Select(s => s.Finish).Where(t => t is not null).Cast<Task>().Append(worker ?? Task.CompletedTask).Append((Task?)flight ?? Task.CompletedTask).ToArray();
            disposal = Task.Run(async () =>
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
                lock (gate) store.Dispose();
                wake.Dispose(); lifetime.Dispose();
                // Do not dispose the backend or semaphore still used by an uncooperative provider.
            });
            return new(disposal);
        }
    }
}
