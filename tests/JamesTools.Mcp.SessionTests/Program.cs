using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using JamesTools.Core.Mcp;

if (args is ["--hold-session", var childDirectory])
{
    await using var childService = new DiagnosticSessionService(new FakeBackend(), childDirectory);
    var childSession = await childService.StartAsync(30, "cpu", 1, default);
    Console.WriteLine("READY " + childSession.Id);
    await Console.In.ReadLineAsync();
    return;
}

await Suite.Run();

static class Suite
{
    static int checks;
    static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref checks);
        if (!condition) throw new Exception(message);
    }
    static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { Interlocked.Increment(ref checks); return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    static async Task<DiagnosticSession> Until(DiagnosticSessionService service, Guid id, Func<DiagnosticSession, bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            var result = await service.GetAsync(id, default);
            if (predicate(result)) return result;
            await Task.Delay(25);
        }
        throw new Exception("Session did not reach expected state.");
    }
    static string DirectoryFor() { var path = Path.Combine(Path.GetTempPath(), "JamesTools-Mcp-SessionTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static async Task InDirectory(Func<string, Task> test)
    {
        var directory = DirectoryFor();
        try { await test(directory); }
        finally { Directory.Delete(directory, true); }
    }
    internal static async Task Run()
    {
        await Task.WhenAll(InDirectory(Lifecycle), InDirectory(SharedPolling), InDirectory(Validation), InDirectory(History), InDirectory(RetentionAndRecovery), InDirectory(CrossProcess), InDirectory(PrioritiesAndFindings), InDirectory(HistoryBudget), InDirectory(MemoryRetention));
        await InDirectory(Failures);
        await InDirectory(Shutdown);
        await InDirectory(PersistenceFailure);
        await InDirectory(NonCooperativeBackend);
        await InDirectory(NonCooperativeEvents);
        Console.WriteLine($"PASS: {checks} assertions (lifecycle, shared polling, validation, history, retention/recovery, cross-process, failures, shutdown, timeout).");
    }

    static async Task Lifecycle(string directory)
    {
        var backend = new FakeBackend();
        Guid completedId;
        await using (var service = new DiagnosticSessionService(backend, directory))
        {
            using var request = new CancellationTokenSource();
            var session = await service.StartAsync(2, "cpu", 1, request.Token);
            completedId = session.Id;
            request.Cancel();
            await Until(service, session.Id, s => s.Samples.Count >= 1);
            var finished = await Until(service, session.Id, s => s.Status == "completed");
            finished = await service.StopAsync(session.Id, default); // Wait for final event/persistence work.
            Check(finished.EndedUtc >= finished.StartedUtc.AddSeconds(1.9), "Completion timestamp was too early.");
            Check(finished.Samples.Count is >= 1 and <= 2, "Duration/interval sample count.");
            var stat = finished.Statistics.Single(s => s.Name == "cpu_percent");
            Check(stat.Count == finished.Samples.Count && stat.Average == 40, "Full statistics incorrect.");
            Check(finished.Statistics.Single(s => s.Name == "gpu_percent").Count == 0, "Missing metric became zero.");
            Check(finished.Samples.All(s => s.Metrics.Any(m => m.Name == "frame_time_ms" && m.Value is null && m.Status == "unsupported")), "Frame scope limitation missing.");
            Check(finished.NotableEvents.Count == 1 && finished.NotableEvents[0].Severity == "warning", "Events not bounded to notable severity/time.");
            Check(backend.LastFilter is { Limit: 50 } filter && filter.FromUtc == finished.StartedUtc && filter.ToUtc == finished.EndedUtc, "Event time bounds incorrect.");
            var listed = await service.ListAsync(default);
            Check(listed.Count == 1 && listed[0].Samples.Count == 0 && listed[0].Statistics.Count > 0, "List must be summary-only.");
            var copy = await service.GetAsync(session.Id, default);
            ((MetricsSnapshot[])copy.Samples)[0] = new(1, DateTimeOffset.UtcNow, [], []);
            Check((await service.GetAsync(session.Id, default)).Samples[0].Metrics.Count > 0, "Mutable response changed service state.");
        }
        Check(!backend.Disposed, "Service disposed host-owned backend.");
        await using var restarted = new DiagnosticSessionService(backend, directory);
        Check((await restarted.GetAsync(completedId, default)).Status == "completed", "Completed recording did not survive restart.");
    }

    static async Task SharedPolling(string directory)
    {
        var backend = new FakeBackend { SampleDelay = 80 };
        await using var service = new DiagnosticSessionService(backend, directory);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => service.GetLiveMetricsAsync(default)));
        Check(backend.Calls == 1 && backend.MaxConcurrent == 1, "On-demand calls did not coalesce.");
        await Task.Delay(1100);
        Check(backend.Calls == 1, "Idle on-demand polling occurred.");
        var sessions = new List<DiagnosticSession>();
        foreach (var profile in new[] { "cpu", "gpu", "storage", "gaming" }) sessions.Add(await service.StartAsync(20, profile, 1, default));
            await Throws<InvalidOperationException>(() => service.StartAsync(20, "general", 1, default));
        await Until(service, sessions[3].Id, s => s.Samples.Count >= 2);
        Check(backend.Calls <= 4 && backend.MaxConcurrent == 1, "Concurrent sessions created independent samplers.");
        var sharedHistory = await service.GetHistoryAsync(new(sessions[0].StartedUtc, DateTimeOffset.UtcNow, ["cpu_percent"]), default);
        Check(sharedHistory.Statistics.Single().Count <= backend.Calls, "Shared observations were counted repeatedly across profiles.");
        await Task.WhenAll(sessions.Select(s => service.StopAsync(s.Id, default)));
        var stoppedCalls = backend.Calls;
        await Task.Delay(1150);
        Check(backend.Calls == stoppedCalls, "Sampling continued after last session stopped.");
        var newSession = await service.StartAsync(10, "general", 1, default);
        await Until(service, newSession.Id, s => s.Samples.Count > 0);
        await service.StopAsync(newSession.Id, default);
        Check(backend.MaxConcurrent == 1, "Restarting lazy worker overlapped samplers.");
        using var request = new CancellationTokenSource();
        backend.EventDelay = 150;
        var canceledStop = await service.StartAsync(20, "gaming", 1, default);
        var stopping = service.StopAsync(canceledStop.Id, request.Token);
        request.Cancel();
        await Throws<OperationCanceledException>(async () => await stopping);
        await Task.Delay(250);
        Check((await service.GetAsync(canceledStop.Id, default)).Status == "stopped", "Stop request cancellation killed accepted finalization.");
    }

    static async Task Validation(string directory)
    {
        await using var service = new DiagnosticSessionService(new FakeBackend(), directory);
        foreach (var duration in new[] { -1, 0, 3601 }) await Throws<ArgumentOutOfRangeException>(() => service.StartAsync(duration, "cpu", 1, default));
        foreach (var interval in new[] { 0, 31 }) await Throws<ArgumentOutOfRangeException>(() => service.StartAsync(1, "cpu", interval, default));
        await Throws<ArgumentException>(() => service.StartAsync(1, "CPU", 1, default));
        await Throws<ArgumentException>(() => service.GetAsync(Guid.Empty, default));
        await Throws<KeyNotFoundException>(() => service.GetAsync(Guid.NewGuid(), default));
        var now = DateTimeOffset.UtcNow;
        foreach (var query in new[] { new HistoryQuery(now, now), new HistoryQuery(now, now.AddSeconds(-1)), new HistoryQuery(now.AddDays(-8), now), new HistoryQuery(now.ToOffset(TimeSpan.FromHours(1)), now.AddSeconds(1)), new HistoryQuery(Limit: 0), new HistoryQuery(Limit: 501), new HistoryQuery(Metrics: []), new HistoryQuery(Metrics: ["cpu* " ]), new HistoryQuery(Metrics: ["cpu", "CPU"]) })
            await Throws<ArgumentException>(() => service.GetHistoryAsync(query, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => service.StartAsync(10, "cpu", 1, canceled.Token));
        Check((await service.ListAsync(default)).Count == 0, "Canceled request was accepted.");
    }

    static DiagnosticSession Seed(DateTimeOffset start, params (string Name, double? Value, int Seconds)[] values)
    {
        var samples = values.Select(v => new MetricsSnapshot(1, start.AddSeconds(v.Seconds), [new(v.Name, v.Value, "%", start.AddSeconds(v.Seconds), v.Value is null ? "unavailable" : "available", "seed")], Array.Empty<string>())).ToArray();
        return new(1, Guid.NewGuid(), "cpu", start, start.AddSeconds(100), "completed", 100, 1, samples, TelemetryData.Aggregate(samples), [], [], []);
    }
    static void Persist(string root, DiagnosticSession session, bool backup = false)
    {
        var directory = Path.Combine(root, "mcp-sessions"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, session.Id.ToString("N") + ".json" + (backup ? ".bak" : "")), JsonSerializer.SerializeToUtf8Bytes(new SessionStore.Envelope(1, session.EndedUtc ?? session.StartedUtc, session)));
    }

    static async Task History(string directory)
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-2); var to = from.AddSeconds(60);
        var seed = Seed(from.AddSeconds(-60), ("cpu_percent", 10, 0), ("gpu_percent", 99, 1), ("cpu_percent", null, 2), ("cpu_percent", 40, 60), ("cpu_percent", 60, 90), ("gpu_percent", 80, 95), ("cpu_percent", 500, 120));
        // Ensure end encompasses every seeded point.
        seed = seed with { EndedUtc = seed.StartedUtc.AddSeconds(121) };
        Persist(directory, seed);
        await using var service = new DiagnosticSessionService(new FakeBackend(), directory);
        var history = await service.GetHistoryAsync(new(from, to, ["cpu_percent"], Limit: 1, ComparePrevious: true), default);
        Check(history.Truncated && history.Samples.Count == 1 && history.Samples[0].Metrics.All(m => m.Name == "cpu_percent"), "Filtered/limited history incorrect.");
        Check(history.Statistics.Single().Count == 2 && history.Statistics.Single().Average == 50, "Stats must include matching data beyond returned sample limit.");
        Check(history.PreviousStatistics.Single().Count == 1 && history.PreviousStatistics.Single().Average == 10, "Previous range/filter/null semantics incorrect.");
        Check(history.Samples.All(s => s.TimestampUtc >= from && s.TimestampUtc < to), "History time bounds not half-open UTC.");
        var index = new { Version = 1, Reports = new[] { new { Summary = new { SchemaVersion = 3, StartedUtc = from.ToString("O"), CpuAverage = 77d, GpuAverage = (double?)null, MemoryAverageMiB = 1024d }, Path = @"C:\never-open-this-file.md" } } };
        var indexPath = Path.Combine(directory, "performance-history.json"); File.WriteAllText(indexPath, JsonSerializer.Serialize(index)); var before = File.ReadAllText(indexPath);
        var legacy = await service.GetHistoryAsync(new(from, to, ["report.cpu_average"]), default);
        Check(legacy.Statistics.Single().Average == 77 && legacy.Warnings.Any(w => w.Contains("not opened")), "Read-only report summary import failed.");
        Check(File.ReadAllText(indexPath) == before, "Existing metadata was changed.");
        File.WriteAllText(indexPath, "{\"Version\":\"secret-token C:\\\\private\"}");
        var broken = await service.GetHistoryAsync(new(from, to), default);
        Check(broken.Warnings.Any(w => w.Contains("Indexed report history")) && !JsonSerializer.Serialize(broken.Warnings).Contains("secret-token"), "Legacy corruption was silent or exposed exception details.");
    }

    static async Task RetentionAndRecovery(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 15; i++) Persist(directory, Seed(now.AddHours(-i - 1), ("cpu_percent", i, 0)));
        var expired = Seed(now.AddDays(-8), ("cpu_percent", 3, 0)); Persist(directory, expired);
        var interrupted = Seed(now.AddSeconds(-20), ("cpu_percent", 12, 0)) with { Status = "recording", EndedUtc = null }; Persist(directory, interrupted);
        var recovered = Seed(now.AddMinutes(-5), ("cpu_percent", 44, 0)); Persist(directory, recovered, backup: true);
        var badPath = Path.Combine(directory, "mcp-sessions", recovered.Id.ToString("N") + ".json"); File.WriteAllText(badPath, "broken");
        var corrupt = Guid.NewGuid(); File.WriteAllText(Path.Combine(directory, "mcp-sessions", corrupt.ToString("N") + ".json"), "broken");
        await using var service = new DiagnosticSessionService(new FakeBackend(), directory);
        var listed = await service.ListAsync(default);
        Check(listed.Count == 12 && listed.All(s => s.Id != expired.Id), "Count/age retention incorrect.");
        var crash = await service.GetAsync(interrupted.Id, default);
        Check(crash.Status == "interrupted" && crash.EndedUtc is not null && crash.Warnings.Any(w => w.Contains("exact interruption")), "Crash recovery fabricated an end time or lost partial data.");
        Check((await service.GetAsync(recovered.Id, default)).Statistics[0].Average == 44, "Backup recovery lost stats.");
        var history = await service.GetHistoryAsync(new(), default);
        Check(history.Warnings.Any(w => w.Contains("corrupt")) && history.Warnings.Any(w => w.Contains("backup")), "Corruption/recovery was silent.");
        Check(Directory.EnumerateFiles(Path.Combine(directory, "mcp-sessions")).Sum(p => new FileInfo(p).Length) <= 32L * 1024 * 1024, "Storage byte quota exceeded.");
        var huge = TelemetryData.Normalize(new(1, now, Enumerable.Range(0, 10000).Select(i => new Metric("metric_" + i, i, "units", now, "available", new string('x', 500), new string('x', 500))).ToArray(), [new string('x', 10000)]));
        Check(JsonSerializer.SerializeToUtf8Bytes(huge).Length <= TelemetryData.SnapshotBytes, "Snapshot bytes unbounded.");
        var bounded = Seed(now.AddMinutes(-2), ("cpu_percent", 1, 0)) with { Samples = Enumerable.Repeat(huge, TelemetryData.MaxSamples).ToArray() };
        Check(JsonSerializer.SerializeToUtf8Bytes(bounded).Length < TelemetryData.SessionBytes, "Full session response exceeds host-safe bound.");
        var hourStart = now.AddHours(-1);
        var hour = Seed(hourStart, ("cpu_percent", 0, 0)) with { DurationSeconds = 3600, Status = "recording", EndedUtc = null, Samples = [], Statistics = [] };
        for (var second = 0; second < 3600; second++)
        {
            var point = hourStart.AddSeconds(second);
            hour = SessionRecording.Append(hour, new(1, point, [new("cpu_percent", second, "%", point, "available", "hour-test")], []));
        }
        var hourStat = hour.Statistics.Single(s => s.Name == "cpu_percent");
        Check(hour.Samples.Count == TelemetryData.MaxSamples && hourStat.Count == 3600 && hourStat.Minimum == 0 && hourStat.Maximum == 3599 && hourStat.Average == 1799.5, "Full-hour recording lost full aggregates when snapshots rolled over.");
        Check(hour.Warnings.Any(w => w.Contains("Older snapshots")) && JsonSerializer.SerializeToUtf8Bytes(hour).Length <= 300 * 1024, "Full-hour recording is not retrievable within host-safe DTO bounds.");
        var verbose = bounded with { NotableEvents = Enumerable.Range(0, 32).Select(i => new DiagnosticEvent(now, new string('漢', 96), i, "warning", new string('漢', 96), new string('漢', 256))).ToArray() };
        var reduced = TelemetryData.BoundSession(verbose);
        Check(JsonSerializer.SerializeToUtf8Bytes(reduced).Length <= 300 * 1024 && reduced.Statistics.SequenceEqual(verbose.Statistics), "Escaped event payload bypassed session bound or changed full stats.");
    }

    static MetricsSnapshot BackendShapedSnapshot(DateTimeOffset now)
    {
        var metrics = new List<Metric>();
        void Add(string name, double? value, string unit = "%", string status = "available") => metrics.Add(new(name, value, unit, now, status, "synthetic backend"));
        Add("cpu.utilization", 40); Add("gpu.utilization", 75); Add("gpu.thermal_limited", 1, "boolean"); Add("gpu.power_limited", 1, "boolean");
        Add("gpu.temperature", 86, "°C"); Add("gpu.clock", 1200, "MHz"); Add("gpu.power", 250, "W");
        Add("gpu.memory_used", 6000, "MiB"); Add("gpu.memory_total", 8000, "MiB");
        Add("cpu.temperature", null, "°C", "unsupported"); Add("cpu.package_power", null, "W", "unsupported");
        for (var i = 0; i < 1024; i++) Add("cpu.logical." + i + ".utilization", i % 100);
        Add("memory.physical_used", 14000, "MiB"); Add("memory.physical_total", 16000, "MiB"); Add("memory.physical_load", 95);
        Add("storage.read", 10, "MiB/s"); Add("storage.write", 5, "MiB/s"); Add("storage.transfer_latency", 60, "ms");
        Add("network.receive", 15000, "bytes/s"); Add("network.send", 2500, "bytes/s");
        return new(1, now, metrics, []);
    }

    static async Task PrioritiesAndFindings(string directory)
    {
        var backend = new FakeBackend { SnapshotFactory = BackendShapedSnapshot };
        await using var service = new DiagnosticSessionService(backend, directory);
        var live = await service.GetLiveMetricsAsync(default);
        Check(live.Metrics.Count == 128 && JsonSerializer.SerializeToUtf8Bytes(live).Length <= TelemetryData.LiveBytes, "Live metrics were restricted to persisted snapshot limits.");
        var essential = new[] { "cpu.utilization", "gpu.utilization", "gpu.temperature", "gpu.clock", "gpu.power", "gpu.memory_used", "memory.physical_used", "storage.read", "network.receive" };
        Check(essential.All(n => live.Metrics.Any(m => m.Name == n && m.Status == "available")), "Logical core details displaced aggregate sensors.");
        foreach (var profile in TelemetryData.Profiles)
        {
            var session = await service.StartAsync(10, profile, 1, default);
            await Until(service, session.Id, s => s.Samples.Count > 0);
            var stopped = await service.StopAsync(session.Id, default);
            Check(essential.All(n => stopped.Samples[0].Metrics.Any(m => m.Name == n)), $"{profile} lost an essential sensor.");
            Check(stopped.Findings.Count == 4 && stopped.Findings.All(f => f.Explanation!.Contains("cannot establish") && f.Evidence.Any(e => e.Contains("Full accepted recording"))), "Backend observations/uncertainty/full aggregate evidence missing.");
            Check(JsonSerializer.SerializeToUtf8Bytes(stopped).Length < 300 * 1024, "Session DTO exceeds transport budget.");
        }
        var cpu = TelemetryData.Normalize(BackendShapedSnapshot(DateTimeOffset.UtcNow), profile: "cpu");
        var gpu = TelemetryData.Normalize(BackendShapedSnapshot(DateTimeOffset.UtcNow), profile: "gpu");
        var storage = TelemetryData.Normalize(BackendShapedSnapshot(DateTimeOffset.UtcNow), profile: "storage");
        Check(cpu.Metrics[0].Name == "cpu.utilization" && gpu.Metrics[0].Name == "gpu.utilization" && storage.Metrics[0].Name == "storage.read", "Profiles do not prioritize their category.");
    }

    static async Task HistoryBudget(string directory)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < 12; i++)
        {
            var samples = Enumerable.Range(0, TelemetryData.MaxSamples).Select(j =>
            {
                var now = start.AddSeconds(i * 100 + j);
                return TelemetryData.Normalize(new(1, now, Enumerable.Range(0, 50).Select(k => new Metric("metric_" + k, k, "%", now, "available", new string('s', 96), new string('r', 256))).ToArray(), []));
            }).ToArray();
            var seed = Seed(start.AddSeconds(i * 100), ("cpu_percent", 2, 0)) with { Samples = samples, Statistics = TelemetryData.Aggregate(samples) };
            Persist(directory, seed);
        }
        await using var service = new DiagnosticSessionService(new FakeBackend(), directory);
        var result = await service.GetHistoryAsync(new(start, start.AddSeconds(1200), Limit: 500), default);
        Check(result.Truncated && result.Samples.Count < 12 * TelemetryData.MaxSamples && result.Statistics.All(s => s.Count == 12 * TelemetryData.MaxSamples || s.Count == 0), "History limit changed matching aggregates.");
        Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 300 * 1024, "History overall DTO is too large for transport expansion.");
        var snapshot = result.Samples[0];
        ((Metric[])snapshot.Metrics)[0] = snapshot.Metrics[0] with { Value = -100 };
        var again = await service.GetHistoryAsync(new(start, start.AddSeconds(1200), Limit: 500), default);
        Check(again.Samples[0].Metrics.All(m => m.Value != -100), "History response arrays mutated retained data.");
    }

    static async Task MemoryRetention(string directory)
    {
        await using var service = new DiagnosticSessionService(new FakeBackend(), directory);
        var ids = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            var session = await service.StartAsync(20, "cpu", 1, default); ids.Add(session.Id);
            await service.StopAsync(session.Id, default);
        }
        Check((await service.ListAsync(default)).Count == SessionStore.MaxSessions, "Persisted count not bounded.");
        var field = typeof(DiagnosticSessionService).GetField("owned", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dictionary = (System.Collections.IDictionary)field.GetValue(service)!;
        Check(dictionary.Count <= SessionStore.MaxSessions, "Completed sessions retained unbounded host memory.");
        await Throws<KeyNotFoundException>(() => service.GetAsync(ids[0], default));
        await Throws<KeyNotFoundException>(() => service.StopAsync(ids[0], default));
        Check((await service.StopAsync(ids[^1], default)).Status == "stopped", "Retained stop is not idempotent.");
    }

    static async Task CrossProcess(string directory)
    {
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--hold-session"); start.ArgumentList.Add(directory);
        using var child = Process.Start(start)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(ready?.StartsWith("READY ") == true, "Child session did not start: " + ready);
            var childId = Guid.Parse(ready![6..]);
            await using var parent = new DiagnosticSessionService(new FakeBackend(), directory);
            Check((await parent.GetAsync(childId, default)).Status == "recording", "Another process's live lease was treated as interrupted.");
            await Throws<NotSupportedException>(() => parent.StopAsync(childId, default));
            for (var i = 0; i < 3; i++) await parent.StartAsync(20, "cpu", 1, default);
            await Throws<InvalidOperationException>(() => parent.StartAsync(20, "cpu", 1, default));
            Check((await parent.ListAsync(default)).Count == 4, "Cross-process sessions not visible.");
            await child.StandardInput.WriteLineAsync("stop"); await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(child.ExitCode == 0 && (await parent.GetAsync(childId, default)).Status == "stopped", "Concurrent clients corrupted final persistence.");
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    static async Task Failures(string directory)
    {
        foreach (var (error, status) in new (Exception, string)[] { (new InvalidOperationException("sensor failed secret-token C:\\private"), "failure"), (new UnauthorizedAccessException("denied secret-token"), "permission_denied"), (new NotSupportedException("no sensor secret-token"), "unsupported") })
        {
            var backend = new FakeBackend { SampleError = error };
            await using var service = new DiagnosticSessionService(backend, directory);
            var session = await service.StartAsync(20, "cpu", 1, default);
            await Until(service, session.Id, s => s.Status == "failed");
            var result = await service.StopAsync(session.Id, default);
            Check(result.Samples.Single().Metrics.Any(m => m.Status == status && m.Value is null), "Explicit backend failure status lost.");
            Check(result.Statistics.All(s => s.Count == 0 && s.Average is null), "Failure became zero statistics.");
            Check(!JsonSerializer.Serialize(result).Contains("secret-token"), "Exception leaked sensitive details.");
        }
        var eventsFailed = new FakeBackend { EventError = new UnauthorizedAccessException("event denied") };
        await using (var service = new DiagnosticSessionService(eventsFailed, directory))
        {
            var session = await service.StartAsync(20, "cpu", 1, default);
            await Until(service, session.Id, s => s.Samples.Count > 0);
            var stopped = await service.StopAsync(session.Id, default);
            Check(stopped.Status == "stopped" && stopped.Warnings.Any(w => w.Contains("permission_denied")), "Event permission failure killed successful recording or was hidden.");
        }
    }

    static async Task Shutdown(string directory)
    {
        var backend = new FakeBackend(); var service = new DiagnosticSessionService(backend, directory);
        var session = await service.StartAsync(20, "storage", 1, default);
        await Until(service, session.Id, s => s.Samples.Count > 0);
        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());
        Check(!backend.Disposed, "Shutdown disposed shared backend.");
        await Throws<ObjectDisposedException>(() => service.GetLiveMetricsAsync(default));
        await using var restart = new DiagnosticSessionService(new FakeBackend(), directory);
        var result = await restart.GetAsync(session.Id, default);
        Check(result.Status == "stopped" && result.EndedUtc is not null && result.Samples.Count > 0, "Shutdown did not finalize/persist partial recording.");
    }

    static async Task PersistenceFailure(string directory)
    {
        var backend = new FakeBackend();
        var service = new DiagnosticSessionService(backend, directory);
        var session = await service.StartAsync(20, "cpu", 1, default);
        await Until(service, session.Id, s => s.Samples.Count > 0);
        var path = Path.Combine(directory, "mcp-sessions", session.Id.ToString("N") + ".json");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = await service.StopAsync(session.Id, default);
            Check(failed.Status == "failed" && failed.Warnings.Any(w => w.Contains("Final persistence failure")), "Final write failure was hidden.");
            Check((await service.GetAsync(session.Id, default)).Status == "failed", "Get hid an in-memory final persistence failure.");
        }
        await service.DisposeAsync();
        await using var restart = new DiagnosticSessionService(backend, directory);
        var recovered = await restart.GetAsync(session.Id, default);
        Check(recovered.Status == "interrupted" && recovered.Samples.Count > 0, "Final write failure destroyed the last durable samples.");
    }

    static async Task NonCooperativeBackend(string directory)
    {
        var backend = new FakeBackend { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new DiagnosticSessionService(backend, directory);
        var timer = Stopwatch.StartNew();
        var failed = await service.GetLiveMetricsAsync(default);
        Check(timer.Elapsed < TimeSpan.FromSeconds(7) && failed.Metrics[0].Status == "unavailable", "Uncooperative sample was not time-bounded.");
        var next = service.GetLiveMetricsAsync(default);
        await service.DisposeAsync();
        await next;
        Check(backend.MaxConcurrent == 1, "Timed-out provider calls overlapped.");
        backend.Block.SetResult();
    }

    static async Task NonCooperativeEvents(string directory)
    {
        var backend = new FakeBackend { EventBlock = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new DiagnosticSessionService(backend, directory);
        var a = await service.StartAsync(20, "cpu", 1, default);
        var b = await service.StartAsync(20, "gpu", 1, default);
        var timer = Stopwatch.StartNew();
        var results = await Task.WhenAll(service.StopAsync(a.Id, default), service.StopAsync(b.Id, default));
        Check(timer.Elapsed < TimeSpan.FromSeconds(5) && backend.EventCalls == 1, "Uncooperative event calls overlapped or escaped the finish budget.");
        Check(results.All(s => s.Status == "stopped" && s.Warnings.Any(w => w.Contains("unavailable"))), "Event timeouts prevented clean session finalization.");
        backend.EventBlock.SetResult();
    }
}

sealed class FakeBackend : IDiagnosticBackend
{
    internal int Calls, MaxConcurrent, SampleDelay = 10, EventDelay, EventCalls;
    int concurrent;
    internal Exception? SampleError, EventError;
    internal Func<DateTimeOffset, MetricsSnapshot>? SnapshotFactory;
    internal TaskCompletionSource? Block;
    internal TaskCompletionSource? EventBlock;
    internal bool Disposed;
    internal EventLogFilter? LastFilter;
    public async Task<MetricsSnapshot> SampleAsync(CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        var current = Interlocked.Increment(ref concurrent);
        MaxConcurrent = Math.Max(MaxConcurrent, current);
        try
        {
            if (Block is not null) await Block.Task;
            else await Task.Delay(SampleDelay, token);
            if (SampleError is not null) throw SampleError;
            var now = DateTimeOffset.UtcNow;
            return SnapshotFactory?.Invoke(now) ?? new(1, now, [new("cpu_percent", 40, "%", now, "available", "fake"), new("gpu_percent", null, "%", now, "unsupported", "fake")], []);
        }
        finally { Interlocked.Decrement(ref concurrent); }
    }
    public async Task<EventLogResult> GetEventLogsAsync(EventLogFilter filter, CancellationToken token)
    {
        Interlocked.Increment(ref EventCalls);
        LastFilter = filter;
        if (EventBlock is not null) await EventBlock.Task;
        if (EventDelay > 0) await Task.Delay(EventDelay, token);
        if (EventError is not null) throw EventError;
        var time = filter.FromUtc!.Value;
        return new(1, DateTimeOffset.UtcNow, "ok", [new(time, "fake", 1, "warning", "system", "notable"), new(time, "fake", 2, "information", "system", "noise"), new(time.AddMinutes(-1), "fake", 3, "error", "system", "out of range")], false, []);
    }
    public void Dispose() => Disposed = true;
    public Task<SystemInventory> GetSystemInfoAsync(CancellationToken token) => throw new NotSupportedException();
    public Task<DiagnosticResult> RunDiagnosticsAsync(string category, CancellationToken token) => throw new NotSupportedException();
    public Task<GameInspection> InspectGameAsync(string game, CancellationToken token) => throw new NotSupportedException();
    public Task<HealthReport> GetHealthReportAsync(CancellationToken token) => throw new NotSupportedException();
    public DiagnosticCapabilities GetCapabilities() => new(1, [], TelemetryData.Profiles, []);
}
