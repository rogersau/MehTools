using JamesTools.Core.Diagnostics;

static class BenchmarkTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run()
    {
        var recipe = new BenchmarkRecipe(Guid.NewGuid(), @"C:\Games\example.exe", "Scene", "Reset manually", "High", 2, 10, 2).Validate();
        foreach (var invalid in new[] { recipe with { Name = "" }, recipe with { WarmupSeconds = 301 }, recipe with { CaptureSeconds = 9 }, recipe with { Runs = 11 }, recipe with { Settings = new string('x', 4001) } })
        { bool rejected = false; try { invalid.Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected, "Invalid recipe accepted"); }
        double time = 0, startAt = -1; int starts = 0, stops = 0, validations = 0;
        var runner = new BenchmarkRunner(() => time, (span, ct) => { ct.ThrowIfCancellationRequested(); time += span.TotalSeconds; return Task.CompletedTask; });
        var outcome = await runner.RunAsync(recipe, _ => { starts++; startAt = time; return Task.CompletedTask; }, () => { stops++; return Task.CompletedTask; }, _ => { validations++; return Task.FromResult(true); }, default);
        Check(outcome.Complete && starts == 1 && stops == 1 && startAt == 2 && time == 12 && !runner.Busy, "Warmup contaminated capture or duplicate capture");
        Check(validations <= 15, "Target checks ran faster than once per second");
        time = 0;
        var delayed = new BenchmarkRunner(() => time, (_, _) => { time += 5; return Task.CompletedTask; });
        outcome = await delayed.RunAsync(recipe, _ => { starts++; return Task.CompletedTask; }, () => Task.CompletedTask, _ => Task.FromResult(true), default);
        Check(!outcome.Complete && starts == 1, "Delayed warmup tick started capture");
        using var cancel = new CancellationTokenSource(); time = 0; stops = 0;
        runner = new BenchmarkRunner(() => time, (span, ct) => { time += span.TotalSeconds; if (time > 2) cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        outcome = await runner.RunAsync(recipe, _ => Task.CompletedTask, () => { stops++; return Task.CompletedTask; }, _ => Task.FromResult(true), cancel.Token);
        Check(!outcome.Complete && stops == 1 && !runner.Busy, "Cancellation did not stop capture");
        time = 0; bool valid = true;
        runner = new BenchmarkRunner(() => time, (span, _) => { time += span.TotalSeconds; if (time > 2) valid = false; return Task.CompletedTask; });
        outcome = await runner.RunAsync(recipe, _ => Task.CompletedTask, () => Task.CompletedTask, _ => Task.FromResult(valid), default);
        Check(!outcome.Complete, "Target invalidation completed run");
        var gate = new TaskCompletionSource();
        runner = new BenchmarkRunner(() => 0, (_, ct) => gate.Task.WaitAsync(ct));
        using var cancellation = new CancellationTokenSource();
        var pending = runner.RunAsync(recipe, _ => Task.CompletedTask, () => Task.CompletedTask, _ => Task.FromResult(true), cancellation.Token);
        bool conflict = false;
        try { await runner.RunAsync(recipe, _ => Task.CompletedTask, () => Task.CompletedTask, _ => Task.FromResult(true), default); } catch (InvalidOperationException) { conflict = true; }
        cancellation.Cancel(); await pending; Check(conflict && !runner.Busy, "Concurrent run accepted");
        time=0; stops=0;
        runner=new BenchmarkRunner(()=>time,(span,_)=> { time+=span.TotalSeconds; return Task.CompletedTask; });
        outcome=await runner.RunAsync(recipe,_=>throw new IOException("Partially started"),()=>{ stops++; return Task.CompletedTask; },_=>Task.FromResult(true),default);
        Check(!outcome.Complete && stops==1 && !runner.Busy, "Partial start was not cleaned up");
        using var blockedCancel=new CancellationTokenSource();
        var blockedValidation=new TaskCompletionSource<bool>();
        var blocked=runner.RunAsync(recipe,_=>Task.CompletedTask,()=>Task.CompletedTask,_=>blockedValidation.Task,blockedCancel.Token);
        blockedCancel.Cancel(); outcome=await blocked.WaitAsync(TimeSpan.FromSeconds(1));
        Check(!outcome.Complete && !runner.Busy,"Cancellation waited indefinitely for target inspection");
        var directory = Path.Combine(Path.GetTempPath(), "meh-benchmark-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BenchmarkStore(directory);
            var summary = new ReportMachineSummary(2, 10, "method", null, null, 20, null, 100, 5, null, null, "scene", null);
            var session = Guid.NewGuid();
            var set = new BenchmarkSet(Guid.NewGuid(), recipe with { }, DateTime.UtcNow,
                [new(Guid.NewGuid(), 1, "Completed", ReportPath: "synthetic.md", ActualSeconds: 10, SessionId: session, Target: "pinned", Summary: summary), new(Guid.NewGuid(), 2, "Capturing")]);
            store.Put(set);
            var mutable=store.Data; mutable.Sets[0].Runs[0]=mutable.Sets[0].Runs[0] with { Status="Incomplete" };
            Check(store.Data.Sets[0].Runs[0].Status=="Completed","Getter exposed mutable stored arrays");
            store.Save(store.Data with { Recipes = [recipe with { Name = "Edited" }] });
            store = new BenchmarkStore(directory);
            var restored = store.Data.Sets.Single();
            Check(restored.Recipe.Name == "Scene" && restored.Runs[0].SessionId == session && restored.Runs[0].ReportPath == "synthetic.md" && restored.Runs[0].ActualSeconds == 10, "Snapshot or pinned metadata changed");
            Check(restored.Runs[1].Status == "Incomplete", "Interrupted capture marked complete");
            var text = BenchmarkResults.Describe(restored);
            Check(text.Contains("FPS unavailable") && !text.Contains("typical"), "Missing values averaged as zero");
            var comparable = restored with { Runs = [restored.Runs[0], restored.Runs[0] with { Id = Guid.NewGuid(), Index=2, Summary = summary with { CpuAverage = 40 } }] };
            text = BenchmarkResults.Describe(comparable);
            Check(text.Contains("median) 30") && text.Contains("20–40") && text.Contains("aggregation unavailable"), "Aggregate comparability/median failed");
            var frameSummary=summary with { FrameStream="123:abc",FrameStreamCount=1,FrameCount=600,FpsAverage=60,FrameMsAverage=1000.0/60 };
            var frames=comparable with { Runs=comparable.Runs.Select(r=>r with { Summary=frameSummary }).ToArray() };
            Check(BenchmarkResults.Describe(frames).Contains("FPS: typical (median) 60"),"Comparable stream results were not summarized");
            frames=frames with { Runs=[frames.Runs[0],frames.Runs[1] with { Summary=frameSummary with { FrameStream="123:def" } }] };
            Check(!BenchmarkResults.Describe(frames).Contains("FPS: typical"),"Different frame streams were combined");
            frames=frames with { Runs=[frames.Runs[0],frames.Runs[1] with { Summary=frameSummary with { FpsMethod="legacy" } }] };
            Check(!BenchmarkResults.Describe(frames).Contains("FPS: typical"),"Different frame methods were combined");
            var parsed=ReportTimeline.ReadSummary("## Machine summary (JSON)\n```json\n{\"schemaVersion\":2,\"frameStream\":\"123:abc\",\"frameStreamCount\":1}\n```");
            Check(parsed.FrameStream=="123:abc" && parsed.FrameStreamCount==1,"Frame quality metadata did not round trip");
            // Force replacement failure while preserving the committed in-memory snapshot.
            using (new FileStream(Path.Combine(directory, "benchmarks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool failed = false; try { store.Save(store.Data with { Recipes = [] }); } catch (IOException) { failed = true; }
                Check(failed && store.Data.Recipes.Length == 1, "Save failure did not roll back");
            }
            var backupBefore=File.ReadAllText(Path.Combine(directory,"benchmarks.json.bak"));
            File.WriteAllText(Path.Combine(directory, "benchmarks.json"), "broken");
            store = new BenchmarkStore(directory); Check(store.Notice != null && store.Data.Sets.Length == 1, "Backup recovery failed");
            Check(File.ReadAllText(Path.Combine(directory,"benchmarks.json.bak"))==backupBefore,"Recovery overwrote the good backup with corrupt data");
            var primaryBefore=File.ReadAllText(Path.Combine(directory,"benchmarks.json"));
            store=new BenchmarkStore(directory);
            Check(File.ReadAllText(Path.Combine(directory,"benchmarks.json"))==primaryBefore && File.ReadAllText(Path.Combine(directory,"benchmarks.json.bak"))==backupBefore,"Read-only open rewrote benchmark history");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
