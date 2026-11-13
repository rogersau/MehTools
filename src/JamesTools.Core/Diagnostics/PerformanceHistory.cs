using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JamesTools.Core.Sessions;
using System.Globalization;
using JamesTools.Core.ConfigurationSnapshots;
using System.Text.Json.Serialization;

namespace JamesTools.Core.Diagnostics;

public sealed record HistoryReport(Guid Id, string Game, string Path, string Reference, DateTime ObservedUtc,
    string? Fingerprint, ReportMachineSummary Summary, string IndexStatus, string Notes = "", Guid? PreviousVersion = null)
{
    [JsonIgnore] public string RecordingContext => "Unknown (no confirmed capture context)";
    [JsonIgnore] public DateTimeOffset? CapturedUtc => DateTimeOffset.TryParseExact(Summary.StartedUtc,
        new[]{"yyyy-MM-dd HH:mm:ss 'UTC'","O"},CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,out var date) ? date : null;
    [JsonIgnore] public DateTimeOffset SortTime => CapturedUtc ?? new DateTimeOffset(DateTime.SpecifyKind(ObservedUtc,DateTimeKind.Utc));
    [JsonIgnore] public string Date => CapturedUtc is { } date ? date.ToString("u") + " (captured)" : ObservedUtc.ToString("u") + " (observed/imported)";
}
public sealed record HistoryGroup(Guid Id, string Game, string Name, string Declaration, Guid[] Members);
public sealed record HistoryData(int Version, HistoryReport[] Reports, HistoryGroup[] Groups);

/// <summary>Immutable indexed report versions; report files are never modified.</summary>
public sealed class PerformanceHistoryStore
{
    readonly object gate = new();
    readonly string path;
    HistoryData data = new(1, [], []);
    string? diskHash;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public PerformanceHistoryStore(string directory)
    {
        path = System.IO.Path.Combine(directory, "performance-history.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("History metadata exceeds 16 MiB.");
            var bytes=Read(path,16*1024*1024);
            data = JsonSerializer.Deserialize<HistoryData>(bytes, Json) ?? throw new InvalidDataException("Empty history metadata.");
            Validate(data); // Fail visibly; never reset corrupt history.
            diskHash=Convert.ToHexString(SHA256.HashData(bytes));
        }
        else if(File.Exists(path+".bak")) throw new IOException("History metadata is missing; its backup was retained. Restore the metadata before opening history.");
    }
    static HistoryData Copy(HistoryData value) => value with { Reports=value.Reports.ToArray(),Groups=value.Groups.Select(g=>g with {Members=g.Members.ToArray()}).ToArray() };
    public HistoryData Snapshot() { lock (gate) return Copy(data); }
    static string Key(string game) => SessionLibraryStore.NormalizeExecutable(game);
    static void Validate(HistoryData d)
    {
        if (d.Version != 1 || d.Reports is null || d.Groups is null || d.Reports.Length > 10000 || d.Groups.Length > 1000)
            throw new InvalidDataException("Invalid history format or limits.");
        if (d.Reports.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Game) || r.Game != Key(r.Game) || !System.IO.Path.IsPathFullyQualified(r.Path) || r.Summary is null || r.ObservedUtc==default || r.Notes is null || r.Notes.Length>4000 || r.Reference is null || r.Reference.Length>2000 || r.IndexStatus is null || r.IndexStatus.Length>4096 || (r.Fingerprint is not null && (r.Fingerprint.Length!=64 || r.Fingerprint.Any(c=>!Uri.IsHexDigit(c))))) || d.Reports.Select(r => r.Id).Distinct().Count() != d.Reports.Length)
            throw new InvalidDataException("Invalid history report.");
        var earlier=new Dictionary<Guid,HistoryReport>();
        foreach(var report in d.Reports)
        {
            if(report.PreviousVersion is Guid id && (!earlier.TryGetValue(id,out var previous) || previous.Game!=report.Game || !string.Equals(previous.Path,report.Path,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Invalid report version chain.");
            earlier.Add(report.Id,report);
        }
        foreach (var g in d.Groups)
            if (g is null || g.Id == Guid.Empty || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length>120 || string.IsNullOrWhiteSpace(g.Declaration) || g.Declaration.Length>4000 || g.Members is null || string.IsNullOrWhiteSpace(g.Game) || g.Game != Key(g.Game) || g.Members.Distinct().Count() != g.Members.Length || g.Members.Any(id => !d.Reports.Any(r => r.Id == id && r.Game == g.Game)))
                throw new InvalidDataException("Invalid comparison group or game ownership.");
        if (d.Groups.Select(g => g.Id).Distinct().Count() != d.Groups.Length) throw new InvalidDataException("Duplicate group IDs.");
    }
    public void Change(Func<HistoryData, HistoryData> change)
    {
        lock (gate)
        {
            var changed=change(Copy(data)); Validate(changed); var next=Copy(changed);
            foreach(var existing in data.Reports)
            {
                var replacement=next.Reports.FirstOrDefault(r=>r.Id==existing.Id);
                if(replacement is null || (replacement with {Notes=existing.Notes})!=existing) throw new InvalidDataException("Indexed versions are immutable; reindex as a new version. Only notes can change.");
            }
            var observed=File.Exists(path) ? Convert.ToHexString(SHA256.HashData(Read(path,16*1024*1024))) : null;
            if(observed!=diskHash) throw new IOException("History changed in another window; reopen it before saving.");
            var content = JsonSerializer.Serialize(next, Json);
            if (Encoding.UTF8.GetByteCount(content) > 16 * 1024 * 1024) throw new IOException("History metadata exceeds 16 MiB.");
            ConfigSnapshotStore.Safe(path);ConfigSnapshotStore.Safe(path+".bak");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) { var bytes = Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
                data = next; // Publish only after atomic persistence succeeds.
                diskHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public static async Task<HistoryReport> IndexAsync(string game, string path, string reference, DateTime observed, Guid? previous = null)
    {
        return await Task.Run(() =>
        {
            path = System.IO.Path.GetFullPath(path);
            string? hash = null; var summary = ReportTimeline.ReadSummary(""); string status;
            try
            {
                var bytes = Read(path); hash = Convert.ToHexString(SHA256.HashData(bytes));
                var text=new UTF8Encoding(false,true).GetString(bytes);
                summary = ReportTimeline.ReadSummary(text);
                status = summary.SchemaVersion == 2 && ReportTimeline.TryParse(text,out _,out _) ? "Indexed" : "Unsupported or incomplete report format";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { status = ex.Message; }
            return new HistoryReport(Guid.NewGuid(), Key(game), path, reference, observed, hash, summary, status, PreviousVersion: previous);
        });
    }
    static byte[] Read(string path,int maximum=32*1024*1024)
    {
        path=ConfigSnapshotStore.LocalPath(path); ConfigSnapshotStore.Safe(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new IOException("File exceeds the supported size limit.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    public static Task<string> VerifyAsync(HistoryReport report) => Task.Run(() =>
    {
        try { return report.Fingerprint == Convert.ToHexString(SHA256.HashData(Read(report.Path))) ? report.IndexStatus == "Indexed" ? "Verified" : report.IndexStatus : "Changed content — reindex explicitly"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Missing/unreadable: " + ex.Message; }
    });
    public static bool FrameValid(ReportMachineSummary s) => s.SchemaVersion == 2 && s.DurationSeconds is >0 && double.IsFinite(s.DurationSeconds.Value) && s.FrameCount > 0 && s.FrameStreamCount == 1 && !string.IsNullOrWhiteSpace(s.FrameStream) && s.FpsMethod == "1000/meanFrameMs" && s.FrameMsAverage is > 0 && double.IsFinite(s.FrameMsAverage.Value);
    public static bool Compatible(ReportMachineSummary a, ReportMachineSummary b) => FrameValid(a) && FrameValid(b) && a.FpsMethod == b.FpsMethod && a.NetworkConfiguration == b.NetworkConfiguration;
    public static double? Difference(double? value, double? baseline) => value is double v && baseline is double b && double.IsFinite(v) && double.IsFinite(b) ? v - b : null;
    public static double? Percent(double? value, double? baseline) => baseline is > 0 && Difference(value, baseline) is double d ? d / baseline.Value * 100 : null;
    public static double? StutterRate(ReportMachineSummary s) => s.FramesOver50Ms is >= 0 && s.FrameCount > 0 && s.FramesOver50Ms <= s.FrameCount ? 1000.0 * s.FramesOver50Ms / s.FrameCount : null;
    public static async Task<string> ReadVerifiedAsync(HistoryReport report)
    {
        return await Task.Run(() => { var bytes = Read(report.Path); if (report.Fingerprint != Convert.ToHexString(SHA256.HashData(bytes))) throw new IOException("Report content changed; reindex first."); return Encoding.UTF8.GetString(bytes); });
    }
}
