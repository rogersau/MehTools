using JamesTools.Core.Diagnostics;
internal static class PerformanceHistoryTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "meh-history-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "report.md");
            await File.WriteAllTextAsync(path, "- Started (UTC): 2026-01-01 00:00:00 UTC\n- NVIDIA driver: unknown\n## Machine summary (JSON)\n```json\n{\"schemaVersion\":2,\"durationSeconds\":10,\"fpsMethod\":\"1000/meanFrameMs\",\"frameMsAvg\":10,\"frameP99Ms\":30,\"framesOver50Ms\":2,\"frameCount\":100,\"frameStreamCount\":1,\"frameStream\":\"PID1\"}\n```\n## Sample timeline (CSV)\n```csv\nsample_start_s,sample_end_s,cpu_pct\n0,1,15\n```\n");
            var a = await PerformanceHistoryStore.IndexAsync(@"C:\Games\a.exe", path, "Synthetic session", DateTime.UtcNow);
            Check(a.Summary.FrameP99Ms == 30 && a.Summary.FramesOver50Ms == 2 && a.Summary.DriverVersion == "unknown", "Additive summary");
            Check(a.RecordingContext.StartsWith("Unknown") && a.Date.Contains("captured"), "Honest context/date");
            var unknownDate=a with { Summary=a.Summary with {StartedUtc="not a date"},ObservedUtc=new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc) };
            Check(unknownDate.CapturedUtc is null && unknownDate.Date.Contains("observed/imported") && unknownDate.SortTime<a.SortTime,"Invalid capture dates were not labelled/sorted by observed time");
            var old = ReportTimeline.ReadSummary("## Machine summary (JSON)\n```json\n{\"schemaVersion\":2}\n```");
            Check(old.FrameP99Ms == null && old.FramesOver50Ms == null && !PerformanceHistoryStore.FrameValid(old), "Old/missing fields");
            var nullable=ReportTimeline.ReadSummary("## Machine summary (JSON)\n```json\n{\"schemaVersion\":2,\"durationSeconds\":10,\"cpuAvg\":15,\"frameStreamCount\":null,\"framesOver50Ms\":null,\"frameP99Ms\":1e999,\"gpuAvg\":-1}\n```");
            Check(nullable.CpuAverage==15 && nullable.DurationSeconds==10 && nullable.FrameP99Ms is null && nullable.GpuAverage is null,"Null/non-finite frame fields hid valid hardware summary");
            Check(PerformanceHistoryStore.Compatible(a.Summary, a.Summary with { FrameStream = "different PID" }), "Stream IDs may change");
            Check(!PerformanceHistoryStore.Compatible(a.Summary, a.Summary with { FrameStreamCount = 2 }) && !PerformanceHistoryStore.Compatible(a.Summary, a.Summary with { FpsMethod = "legacy" }) && !PerformanceHistoryStore.Compatible(a.Summary, a.Summary with { NetworkConfiguration = "different" }), "Compatibility");
            Check(PerformanceHistoryStore.Percent(2, 0) == null && PerformanceHistoryStore.Difference(2, 0) == 2 && PerformanceHistoryStore.Difference(null, 1) == null && PerformanceHistoryStore.StutterRate(a.Summary) == 20, "Deltas/denominators");
            var store = new PerformanceHistoryStore(dir); Check(!File.Exists(Path.Combine(dir, "performance-history.json")), "Read-only open");
            store.Change(d => d with { Reports = [a with { Notes = "User: OBS off" }] });
            var group = new HistoryGroup(Guid.NewGuid(), a.Game, "Scene A", "1080p preset / repeat scene", []);
            store.Change(d => d with { Groups = [group] }); Check(store.Snapshot().Groups[0].Members.Length == 0, "Explicit membership");
            store.Change(d => d with { Groups = [group with { Members = [a.Id] }] });
            var copy = store.Snapshot(); copy.Groups[0].Members[0] = Guid.Empty; Check(store.Snapshot().Groups[0].Members[0] == a.Id, "Deep isolation");
            bool rejected = false;
            try { store.Change(d => d with { Groups = [group with { Game = @"C:\OTHER.EXE", Members = [a.Id] }] }); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && store.Snapshot().Groups[0].Game == a.Game, "Game isolation/rollback");
            rejected=false;try{store.Change(d=>d with {Reports=d.Reports.Select(r=>r with {Summary=r.Summary with {FrameMsAverage=1}}).ToArray()});}catch(InvalidDataException){rejected=true;}
            Check(rejected,"An indexed version's measurements were mutated in place");
            await File.AppendAllTextAsync(path, "changed"); Check((await PerformanceHistoryStore.VerifyAsync(a)).StartsWith("Changed"), "Changed source");
            var b = await PerformanceHistoryStore.IndexAsync(a.Game, path, "reindex", DateTime.UtcNow, a.Id);
            store.Change(d => d with { Reports = d.Reports.Append(b).ToArray(), Groups = [] });
            Check(store.Snapshot().Reports[0].Notes == "User: OBS off" && store.Snapshot().Reports[1].PreviousVersion == a.Id, "Notes and history preservation");
            File.Delete(path); Check((await PerformanceHistoryStore.VerifyAsync(b)).StartsWith("Missing"), "Missing source");
            var metadata = Path.Combine(dir, "performance-history.json"); var before = File.ReadAllText(metadata);
            using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rejected = false; try { store.Change(d => d with { Reports = d.Reports.Select(r=>r with {Notes="test failed write"}).ToArray() }); } catch (IOException) { rejected = true; }
                Check(rejected && store.Snapshot().Reports.Length == 2, "Save failure rollback");
            }
            Check(File.ReadAllText(metadata) == before, "Failed save preserves disk");
            Check(new PerformanceHistoryStore(dir).Snapshot().Reports.Length == 2, "Persistence roundtrip");
            var stale=new PerformanceHistoryStore(dir);store.Change(d=>d with {Groups=[group]});
            rejected=false;try{stale.Change(d=>d with {Groups=[]});}catch(IOException){rejected=true;}
            Check(rejected && new PerformanceHistoryStore(dir).Snapshot().Groups.Length==1,"Stale writer overwrote newer group metadata");
            File.WriteAllText(metadata, "bad JSON"); rejected = false;
            try { _ = new PerformanceHistoryStore(dir); } catch (System.Text.Json.JsonException) { rejected = true; }
            Check(rejected && File.ReadAllText(metadata) == "bad JSON", "Malformed metadata preserved");
        }
        finally { Directory.Delete(dir, true); }
    }
}
