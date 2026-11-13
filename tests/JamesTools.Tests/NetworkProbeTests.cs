using System.Net;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Configuration;
using System.Text.Json;

static class NetworkProbeTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run()
    {
        foreach (var bad in new[] { "https://example.test", "example.test:80", "user@host", "a/b", "a?x", new string('a',254), "-bad.test", "999.0.0.1", "a..b", "[::1]", "127.1", "010.0.0.1", "0x7f000001" })
        { try { NetworkProbeOptions.Create(true,bad); throw new Exception("Accepted " + bad); } catch(ArgumentException) { } }
        Check(NetworkProbeOptions.Create(true,"EXAMPLE.TEST\n::1").Targets.SequenceEqual(new[]{"example.test","::1"}),"Normalization");
        try { NetworkProbeOptions.Create(true,"a,b,c,d"); throw new Exception("Target limit"); } catch(ArgumentException) { }
        var settings=new AppSettings { NetworkDiagnosticsEnabled=true, NetworkTargets="example.test" };
        var copy=settings.Snapshot(); settings.NetworkTargets="changed.test"; Check(copy.NetworkTargets=="example.test","Settings snapshot");
        NetworkObservation R(double t,string status,double? rtt)=>new("example.test","192.0.2.1",t,t+.1,status,rtt);
        var rows=new[]{R(0,"Success",10),R(1,"Success",14),R(2,"TimedOut",null),R(3,"Success",20),R(4,"LocalError",null),R(5,"Success",50),R(9,"Success",60)};
        var summary=NetworkReports.Summarize(rows);
        Check(summary.Successes==5 && summary.Timeouts==1 && summary.Errors==1 && summary.JitterPairs==1 && summary.JitterMs==4,"Summary denominators and jitter gaps");
        var selected=NetworkReports.AnalyzeSelection(rows,"example.test",0,10,new[]{new ReportExclusion(.5,2.5,"loading")});
        Check(selected.Attempts==5 && selected.Successes==4 && selected.Timeouts==0 && selected.JitterPairs==0,"Selection and exclusions");
        selected=NetworkReports.AnalyzeSelection(rows.Take(2),"example.test",0,2,new[]{new ReportExclusion(.4,.6,"excluded gap")});
        Check(selected.Attempts==2 && selected.JitterPairs==0,"Jitter bridged an excluded interval between observations");
        var capture=new NetworkCapture(true,new[]{"example.test"},rows);
        var text=NetworkReports.Heading+"\n```json\n"+JsonSerializer.Serialize(capture)+"\n```";
        Check(NetworkReports.Read(text)?.Observations.Length==7 && NetworkReports.Read("old report")==null,"Report compatibility");
        foreach (var malformed in new[]{"null","{\"Enabled\":true,\"Targets\":[\"example.test\"],\"Observations\":null}","{\"Enabled\":true,\"Targets\":[\"example.test\"],\"Observations\":[null]}"})
            Check(NetworkReports.Read(NetworkReports.Heading+"\n```json\n"+malformed+"\n```")==null,"Malformed network metadata was accepted");
        var badCapture=capture with { Observations=[R(-1,"Success",10)] };
        Check(NetworkReports.Read(NetworkReports.Heading+"\n```json\n"+JsonSerializer.Serialize(badCapture)+"\n```")==null,"Invalid observation timing was accepted");
        var portable = "- Report format: 2\n## Sample timeline (CSV)\n```csv\nsample_start_s,sample_end_s,frame_window_start_s,frame_window_end_s,frame_count,fps\n0,2,0,2,20,10\n```\n"+text;
        Check(ReportTimeline.TryParse(portable,out var parsed,out _) && parsed!.Network!.Observations.Length==7,"Additive format-2 parser");
        Check(ReportTimeline.Analyze(parsed!,0,2).Frames==20,"Network must not multiply frame counts");
        await using(var dnsFailure=new NetworkProbeCollector(new(true,new[]{"example.test"}),new FailingBackend(),new FakeClock()))
            Check(dnsFailure.Snapshot().Observations.Single().Status=="DnsFailure","DNS errors stay unavailable");
        await using(var unsupported=new NetworkProbeCollector(new(true,new[]{"example.test"}),new FailingBackend { Empty=true },new FakeClock()))
            Check(unsupported.Snapshot().Observations.Single().Status=="Unsupported","Unsupported address status");
        var fake=new FakeBackend(); var clock=new FakeClock();
        await using(var disabled=new NetworkProbeCollector(new(false,new[]{"example.test"}),fake,clock))
            Check(disabled.Snapshot().Observations.Length==0 && fake.Resolves==0,"Disabled does no IO");
        var options=new NetworkProbeOptions(true,new[]{"a.test","b.test","c.test"});
        var collector=new NetworkProbeCollector(options,fake,clock);
        options.Targets[0]="mutated.test";
        Check(fake.Resolves==3 && fake.Peak==3 && collector.Snapshot().Targets[0]=="a.test","Pinned options and concurrency");
        await collector.DisposeAsync();
        await collector.DisposeAsync();
        Check(fake.Active==0 && collector.Snapshot().Observations.All(r=>r.Status=="Cancelled"),"Cancellation drained");
        var sequence=new SequenceBackend(); var fast=new FastClock();
        var retained=new NetworkProbeCollector(new(true,new[]{"example.test"}),sequence,fast);
        await sequence.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await retained.DisposeAsync();
        Check(retained.Snapshot().Observations.Length<=3600 && sequence.Resolves==1 && sequence.Calls>=3700,"Retention and resolve once");
        var stubborn=new StubbornBackend();
        var interrupted=new NetworkProbeCollector(new(true,new[]{"example.test"}),stubborn,new FakeClock());
        try
        {
            await interrupted.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(interrupted.Snapshot().Observations.Single().Status=="Cancelled","Uncooperative DNS blocked cancellation");
        }
        finally { stubborn.Done.TrySetResult([]); }
    }
    sealed class FailingBackend : INetworkProbeBackend
    {
        public bool Empty;
        public Task<IPAddress[]> ResolveAsync(string t,CancellationToken c) => Empty ? Task.FromResult(Array.Empty<IPAddress>()) : Task.FromException<IPAddress[]>(new InvalidOperationException());
        public Task<(string Status,double? RttMs)> ProbeAsync(IPAddress a,CancellationToken c)=>throw new Exception("Must not probe unresolved target");
    }
    sealed class StubbornBackend : INetworkProbeBackend
    {
        public TaskCompletionSource<IPAddress[]> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPAddress[]> ResolveAsync(string target,CancellationToken token)=>Done.Task;
        public Task<(string Status,double? RttMs)> ProbeAsync(IPAddress address,CancellationToken token)=>throw new Exception("Must not probe unresolved target");
    }
    sealed class FakeClock : INetworkProbeClock
    {
        public double Seconds=>0;
        public Task DelayAsync(TimeSpan delay,CancellationToken token)=>Task.Delay(Timeout.Infinite,token);
    }
    sealed class FakeBackend : INetworkProbeBackend
    {
        public int Resolves,Active,Peak;
        public Task<IPAddress[]> ResolveAsync(string t,CancellationToken c) { Resolves++; return Task.FromResult(new[]{IPAddress.Parse("192.0.2.1")}); }
        public async Task<(string Status,double? RttMs)> ProbeAsync(IPAddress a,CancellationToken c)
        { Active++; Peak=Math.Max(Peak,Active); try { await Task.Delay(Timeout.Infinite,c); return ("Success",1); } finally { Active--; } }
    }
    sealed class FastClock : INetworkProbeClock
    {
        public double Seconds { get; private set; }
        public async Task DelayAsync(TimeSpan delay,CancellationToken token) { token.ThrowIfCancellationRequested(); Seconds+=delay.TotalSeconds; await Task.Yield(); }
    }
    sealed class SequenceBackend : INetworkProbeBackend
    {
        public int Resolves,Calls; public TaskCompletionSource Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPAddress[]> ResolveAsync(string t,CancellationToken c) { Resolves++; return Task.FromResult(new[]{IPAddress.Parse("192.0.2.1")}); }
        public async Task<(string Status,double? RttMs)> ProbeAsync(IPAddress a,CancellationToken c)
        { if(++Calls>=3700) { Done.TrySetResult(); await Task.Delay(Timeout.Infinite,c); } return Calls%3==0?("TimedOut",null):Calls%3==1?("Success",12):("LocalError",null); }
    }
}
