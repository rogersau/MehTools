using JamesTools.Core.Diagnostics;
using JamesTools.Core.Configuration;
internal static class RollingTests
{
    static void Check(bool b) { if(!b) throw new Exception("Rolling diagnostics assertion failed"); }
    public static Task Run()
    {
        var b=new RollingDiagnosticBuffer(); var owner=Guid.NewGuid();
        b.Configure("game",owner,10,2);
        Check(!b.Trigger(0)); // No empty reports that the timeline viewer cannot open.
        for(int i=0;i<30;i++) b.Add(new(i,i));
        Check(b.Count==11); Check(b.Trigger(29.5)); Check(!b.Trigger(30));
        Check(b.Add(new(30,null)) is null);
        var c=b.Add(new(32,12))!;
        Check(c.SessionId==owner && c.Samples.Count==11 && c.Trigger==29.5);
        Check(ReportTimeline.TryParse(c.Report(),out var parsed,out _) && parsed!.Markers[0].OffsetSeconds==9.5);
        Check(parsed!.Summary.SystemSampleCount==11 && parsed.Summary.CpuAverage is not null);
        Check(parsed!.Rows.Last().CpuPercent is null && parsed.Rows.All(x=>x.Fps is null));
        Check(b.Trigger(32)); var partial=b.Configure("other",null,10,0)!;
        Check(partial.SessionId==owner && partial.Reason.Contains("Invalidated") && b.Count==0);
        b.Add(new(33)); Check(b.Trigger(33)); Check(b.Advance(33)!.Samples.Count==1);
        b.Configure(null,null,10,0); Check(!b.Trigger(34));
        b.Configure("game",null,300,60);
        for(int i=0;i<1000;i++) b.Add(new(100+i*.001));
        Check(b.Count==RollingDiagnosticBuffer.MaximumSamples);
        Check(b.Trigger(102)); Check(b.Finish("Cancelled")!.SessionId is null); Check(!b.Capturing);
        Check(!b.Trigger(110)); // A stale last sample does not imply current buffering.
        Check(!b.Trigger(99)); // Trigger times cannot precede the latest sample.
        b.Configure("missing",owner,10,2);
        b.Add(new(200,double.NaN,double.PositiveInfinity,-1,Read:-1));
        Check(b.Trigger(200.1));
        var missing=b.Advance(205)!;
        Check(missing.Samples.Single().Cpu is null && missing.Samples.Single().Gpu is null && missing.Samples.Single().Memory is null && missing.Samples.Single().Read is null);
        Check(ReportTimeline.TryParse(missing.Report(),out var missingReport,out _) && missingReport!.Rows.Count==1 && missingReport.Summary.CpuAverage is null);
        Check(missing.Report().Contains("actual pre/post: 0.1/0")); // No invented post readings during a gap.
        b.Add(new(206,10));
        Check(b.Trigger(206));
        var changed=b.Configure("next",Guid.NewGuid(),20,4)!;
        Check(changed.SessionId==owner && changed.Before==10 && changed.After==2 && changed.Samples.Count==2);
        b.Add(new(207,80));
        Check(changed.Samples.Count==2 && changed.Samples.All(x=>x.Seconds<207)); // Detached immutable capture.
        Check(!b.Capturing && b.Finish("again") is null);
        var rejected=false; try { b.Add(new(double.NaN)); } catch(ArgumentOutOfRangeException) { rejected=true; } Check(rejected);
        var settings=new AppSettings { RollingBeforeSeconds=9 }; Check(SettingsValidation.Validate(settings).Any(x=>x.Contains("Rolling")));
        settings.RollingBeforeSeconds=60; settings.RollingHotkey=settings.BookmarkHotkey; Check(SettingsValidation.Validate(settings).Any(x=>x.Contains("must be different")));
        return Task.CompletedTask;
    }
}
