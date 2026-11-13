using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JamesTools.Core.Diagnostics;

public sealed record NetworkProbeOptions(bool Enabled, string[] Targets)
{
    public static NetworkProbeOptions Create(bool enabled, string? text)
    {
        if (text?.Length > 1024) throw new ArgumentException("Enter at most three network targets (maximum 1024 characters).");
        var targets = (text ?? "").Split(new[] { '\r', '\n', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length > 3) throw new ArgumentException("Enter at most three network targets.");
        targets = targets.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (enabled && targets.Length == 0) throw new ArgumentException("Enter a hostname or IP address before enabling network diagnostics.");
        return new(enabled, targets);
    }
    public static string Normalize(string value)
    {
        var s = value.Trim();
        if (s.Length == 0 || s.Length > 253 || s.Any(c => char.IsWhiteSpace(c) || "/\\@?#[]%".Contains(c))) throw new ArgumentException("Network targets must be plain hostnames or IP addresses (maximum 253 characters).");
        if (IPAddress.TryParse(s, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && s != ip.ToString())
                throw new ArgumentException("IPv4 targets must use four decimal parts without leading zeroes.");
            return ip.ToString();
        }
        if (s.Contains(':') || s.All(c => char.IsDigit(c) || c == '.') || !s.Split('.').All(l => Regex.IsMatch(l, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z")))
            throw new ArgumentException("Invalid network target; URLs, ports, credentials and paths are not allowed.");
        return s.ToLowerInvariant();
    }
}
public sealed record NetworkObservation(string Target, string? Address, double SendSeconds, double ReceiveSeconds, string Status, double? RttMs);
public sealed record NetworkCapture(bool Enabled, string[] Targets, NetworkObservation[] Observations);
public sealed record NetworkSelection(int Attempts, int Successes, int Timeouts, int Errors, double? RttMs, double? JitterMs, int JitterPairs)
{
    public override string ToString() => $"{Attempts} observations; {Successes} replies; {Timeouts} timeouts; {Errors} errors/unavailable/cancelled. RTT {RttMs?.ToString("F2") ?? "unavailable"} ms; jitter {JitterMs?.ToString("F2") ?? "unavailable"} ms ({JitterPairs} pairs).";
}
public static class NetworkReports
{
    public const string Heading = "## Network diagnostics (JSON)";
    public const string Explanation = "ICMP probes to explicitly selected destinations; target names and resolved addresses appear in reports. These may behave differently from the game's connection. No game packet loss, server performance or causation is inferred. RTT uses successful replies only; jitter is the mean absolute difference of adjacent successful replies to the same address, resetting on failures or send gaps over 2.5 seconds. Counts use retained observations only; errors are not packet loss. Cadence: at least 1 second between sends, timeout 1 second, DNS timeout 2 seconds; one address pinned per target; at most 3 concurrent probes; last 3600 observations and last hour per target.";
    public static NetworkCapture? Read(string text)
    {
        var at = text.IndexOf(Heading, StringComparison.Ordinal); if (at < 0) return null;
        var a = text.IndexOf("```json", at, StringComparison.Ordinal); var b = a < 0 ? -1 : text.IndexOf("```", a + 7, StringComparison.Ordinal);
        if (b < 0) return null;
        if (b-a > 4_000_000) return null;
        try
        {
            var capture=JsonSerializer.Deserialize<NetworkCapture>(text[(a + 7)..b]);
            if (capture?.Targets is null || capture.Observations is null || capture.Targets.Length > 3 || capture.Observations.Length > 10800 ||
                capture.Targets.Any(t=>t is null) || capture.Observations.Any(r=>r is null)) return null;
            var normalized=NetworkProbeOptions.Create(capture.Enabled,string.Join("\n",capture.Targets));
            if (!normalized.Targets.SequenceEqual(capture.Targets)) return null;
            foreach (var row in capture.Observations)
            {
                if (!capture.Targets.Contains(row.Target,StringComparer.Ordinal) || !double.IsFinite(row.SendSeconds) || row.SendSeconds<0 ||
                    !double.IsFinite(row.ReceiveSeconds) || row.ReceiveSeconds<row.SendSeconds || string.IsNullOrWhiteSpace(row.Status) || row.Status.Length>64 ||
                    (row.Address is not null && (row.Address.Length>64 || !IPAddress.TryParse(row.Address,out _))) ||
                    (row.RttMs is double rtt && (!double.IsFinite(rtt) || rtt<0)) ||
                    (row.Status=="Success" && (row.Address is null || row.RttMs is null)) || (row.Status!="Success" && row.RttMs is not null)) return null;
            }
            return capture;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return null; }
    }
    public static NetworkSelection AnalyzeSelection(IEnumerable<NetworkObservation> observations, string target,
        double start, double end, IReadOnlyList<ReportExclusion> exclusions)
    {
        var rows = observations.Where(r => r.Target == target && r.SendSeconds >= start && r.ReceiveSeconds <= end).ToArray();
        bool Included(NetworkObservation r) => !exclusions.Any(x => r.ReceiveSeconds >= x.StartSeconds && r.SendSeconds < x.EndSeconds);
        var result = Summarize(rows.Select(r => Included(r) ? r : r with { Status = "Excluded", RttMs = null }),exclusions);
        return result with { Attempts = rows.Count(Included), Errors = rows.Count(r => Included(r) && r.Status != "Success" && r.Status != "TimedOut") };
    }
    public static NetworkSelection Summarize(IEnumerable<NetworkObservation> observations, IReadOnlyList<ReportExclusion>? barriers = null)
    {
        var rows = observations.OrderBy(x => x.SendSeconds).ToArray(); var diffs = new List<double>(); NetworkObservation? previous = null;
        foreach (var r in rows)
        {
            if (r.Status == "Success" && r.RttMs is >= 0 && double.IsFinite(r.RttMs.Value))
            {
                if (previous is { Status: "Success", RttMs: not null } p && p.Target == r.Target && p.Address == r.Address &&
                    r.SendSeconds > p.SendSeconds && r.SendSeconds - p.SendSeconds <= 2.5 &&
                    !(barriers?.Any(x=>x.StartSeconds<r.SendSeconds && x.EndSeconds>p.ReceiveSeconds) ?? false)) diffs.Add(Math.Abs(r.RttMs.Value-p.RttMs.Value));
                previous = r;
            }
            else previous = null;
        }
        var ok = rows.Where(r=>r.Status=="Success" && r.RttMs is >= 0 && double.IsFinite(r.RttMs.Value)).ToArray();
        return new(rows.Length, ok.Length, rows.Count(r=>r.Status=="TimedOut"), rows.Count(r=>r.Status!="Success" && r.Status!="TimedOut"), ok.Length==0?null:ok.Average(r=>r.RttMs!.Value), diffs.Count==0?null:diffs.Average(), diffs.Count);
    }
}
public interface INetworkProbeBackend
{
    Task<IPAddress[]> ResolveAsync(string target, CancellationToken token);
    Task<(string Status, double? RttMs)> ProbeAsync(IPAddress address, CancellationToken token);
}
public interface INetworkProbeClock
{
    double Seconds { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken token);
}
public sealed class NetworkProbeCollector : IAsyncDisposable
{
    readonly NetworkProbeOptions options;
    readonly INetworkProbeBackend backend;
    readonly INetworkProbeClock clock;
    readonly CancellationTokenSource stop;
    readonly Task work;
    readonly object gate = new();
    readonly List<NetworkObservation> rows = [];
    Task? completion;
    public NetworkProbeCollector(NetworkProbeOptions options, INetworkProbeBackend backend, INetworkProbeClock clock, CancellationToken token = default)
    {
        this.options = NetworkProbeOptions.Create(options.Enabled, string.Join("\n", options.Targets)); this.backend=backend; this.clock=clock;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        work = this.options.Enabled ? Task.WhenAll(this.options.Targets.Select(RunAsync)) : Task.CompletedTask;
    }
    public NetworkCapture Snapshot() { lock(gate) return new(options.Enabled, options.Targets.ToArray(), rows.ToArray()); }
    void Add(NetworkObservation row)
    {
        lock(gate) { rows.Add(row); rows.RemoveAll(r=>r.Target==row.Target && r.SendSeconds < row.ReceiveSeconds-3600); while(rows.Count(r=>r.Target==row.Target)>3600) rows.RemoveAt(rows.FindIndex(r=>r.Target==row.Target)); }
    }
    async Task RunAsync(string target)
    {
        IPAddress? address=null; double sent=clock.Seconds; bool outstanding=true;
        try
        {
            try { address=(await BoundedAsync(token=>backend.ResolveAsync(target,token),TimeSpan.FromSeconds(2)).ConfigureAwait(false)).FirstOrDefault(a=>a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6); }
            catch (TimeoutException) { Add(new(target,null,sent,clock.Seconds,"DnsTimeout",null)); return; }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { Add(new(target,null,sent,clock.Seconds,"DnsTimeout",null)); return; }
            catch (Exception) when (!stop.IsCancellationRequested) { Add(new(target,null,sent,clock.Seconds,"DnsFailure",null)); return; }
            if(address==null) { Add(new(target,null,sent,clock.Seconds,"Unsupported",null)); return; }
            while(true)
            {
                stop.Token.ThrowIfCancellationRequested(); sent=clock.Seconds; outstanding=true;
                (string Status,double? RttMs) result;
                try { result=await BoundedAsync(token=>backend.ProbeAsync(address,token),TimeSpan.FromSeconds(1.5)).ConfigureAwait(false); }
                catch(TimeoutException) { Add(new(target,address.ToString(),sent,clock.Seconds,"LocalTimeout",null)); return; }
                catch(OperationCanceledException) when(stop.IsCancellationRequested) { throw; }
                catch { result=("LocalError",null); }
                stop.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(result.Status) || result.Status.Length>64 ||
                    (result.Status=="Success" && (result.RttMs is not double rtt || !double.IsFinite(rtt) || rtt<0))) result=("LocalError",null);
                Add(new(target,address.ToString(),sent,clock.Seconds,result.Status,result.Status=="Success"?result.RttMs:null));
                outstanding=false;
                await clock.DelayAsync(TimeSpan.FromSeconds(Math.Max(0.01,1-(clock.Seconds-sent))),stop.Token).WaitAsync(stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { if (outstanding) Add(new(target,address?.ToString(),sent,clock.Seconds,"Cancelled",null)); }
        catch { Add(new(target,address?.ToString(),sent,clock.Seconds,"LocalError",null)); }
    }
    async Task<T> BoundedAsync<T>(Func<CancellationToken,Task<T>> create,TimeSpan timeout)
    {
        using var request=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var task=create(request.Token);
        try { return await task.WaitAsync(timeout,stop.Token).ConfigureAwait(false); }
        finally
        {
            if (!task.IsCompleted)
            {
                request.Cancel();
                try { await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
                if (!task.IsCompleted) _=task.ContinueWith(t=>{ _=t.Exception; },CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
    }
    public ValueTask DisposeAsync() { lock(gate) return new(completion ??= StopAsync()); }
    async Task StopAsync() { stop.Cancel(); await work.ConfigureAwait(false); stop.Dispose(); }
}
internal sealed class SystemNetworkProbeBackend : INetworkProbeBackend
{
    public Task<IPAddress[]> ResolveAsync(string target,CancellationToken token) => IPAddress.TryParse(target,out var ip) ? Task.FromResult(new[]{ip}) : Dns.GetHostAddressesAsync(target,token);
    public async Task<(string Status,double? RttMs)> ProbeAsync(IPAddress address,CancellationToken token)
    {
        using var ping=new Ping();
        var reply=await ping.SendPingAsync(address,TimeSpan.FromSeconds(1), cancellationToken:token).ConfigureAwait(false);
        return (reply.Status.ToString(), reply.Status==IPStatus.Success ? reply.RoundtripTime : null);
    }
}
internal sealed class StopwatchNetworkClock(Stopwatch stopwatch) : INetworkProbeClock
{
    public double Seconds => stopwatch.Elapsed.TotalSeconds;
    public Task DelayAsync(TimeSpan delay,CancellationToken token)=>Task.Delay(delay,token);
}
