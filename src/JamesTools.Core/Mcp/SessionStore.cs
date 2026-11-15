using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JamesTools.Core.Mcp;

// Only this private directory is mutable. Report metadata beside it is read-only.
internal sealed class SessionStore : IDisposable
{
    internal const int MaxSessions = 12;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    readonly string directory;
    readonly Mutex mutex;
    readonly Dictionary<Guid, FileStream> leases = [];
    readonly List<string> warnings = [];
    internal string Root { get; }
    internal sealed record Envelope(int Version, DateTimeOffset WrittenUtc, DiagnosticSession Session);

    internal SessionStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("A fully qualified local storage directory is required.", nameof(root));
        Root = Path.GetFullPath(root);
        directory = Path.Combine(Root, "mcp-sessions");
        Safe(directory);
        Directory.CreateDirectory(directory);
        mutex = new Mutex(false, "Local\\JamesTools.Mcp.Sessions." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))));
    }

    static void Safe(string path)
    {
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Session storage cannot traverse a symbolic link or reparse point.");
    }

    T Locked<T>(Func<T> action)
    {
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Session storage is unavailable: another client holds the storage lock.");
            Safe(directory);
            return action();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    string PathFor(Guid id, string suffix = ".json") => Path.Combine(directory, id.ToString("N") + suffix);
    bool Owned(string path, out Guid id)
    {
        var name = Path.GetFileName(path);
        id = default;
        return name.Length >= 32 && Guid.TryParseExact(name[..32], "N", out id) &&
            name[32..] is ".json" or ".json.bak" or ".json.tmp" or ".lease";
    }
    void Warn(string value)
    {
        value = TelemetryData.Text(value);
        if (warnings.Count < 16 && !warnings.Contains(value)) warnings.Add(value);
    }
    internal string[] Warnings() => Locked(() => warnings.ToArray());
    bool IsLive(Guid id)
    {
        if (leases.ContainsKey(id)) return true;
        var path = PathFor(id, ".lease");
        if (!File.Exists(path)) return false;
        Safe(path);
        try { using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    Envelope Read(string path, Guid id)
    {
        Safe(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > TelemetryData.SessionBytes + 1024) throw new InvalidDataException("Persisted session exceeds 301 KiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Empty session envelope.");
        if (envelope.Version != 1 || !TelemetryData.Utc(envelope.WrittenUtc) || envelope.Session?.Id != id)
            throw new InvalidDataException("Invalid session envelope.");
        TelemetryData.Validate(envelope.Session);
        return envelope;
    }
    static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException;

    List<DiagnosticSession> Load()
    {
        var result = new List<DiagnosticSession>();
        // Bound directory enumeration as well as payload reads. This directory is service-owned.
        var files = Directory.EnumerateFiles(directory).Take(257).ToArray();
        if (files.Length > 256) throw new IOException("Session storage contains too many entries; inspect the private mcp-sessions directory.");
        var ids = files.Where(p => Owned(p, out _) && p.EndsWith(".json", StringComparison.Ordinal) || Owned(p, out _) && p.EndsWith(".json.bak", StringComparison.Ordinal))
            .Select(p => Guid.ParseExact(Path.GetFileName(p)[..32], "N")).Distinct().Take(64).ToArray();
        if (ids.Length > 32) throw new IOException("Session storage contains too many records.");
        foreach (var id in ids)
        {
            Envelope? record = null;
            try { record = Read(PathFor(id), id); }
            catch (Exception ex) when (StorageError(ex))
            {
                try { record = Read(PathFor(id, ".json.bak"), id); Warn($"Recovered session {id:N} from its backup."); }
                catch (Exception backupError) when (StorageError(backupError)) { Warn($"Session {id:N} is corrupt or unreadable and was omitted; its files were retained."); }
            }
            if (record is null) continue;
            var session = record.Session;
            if (session.Status == "recording" && !IsLive(id))
            {
                session = session with { Status = "interrupted", EndedUtc = record.WrittenUtc < session.StartedUtc ? session.StartedUtc : record.WrittenUtc,
                    Warnings = session.Warnings.Append("Recording owner exited. EndedUtc is the last durable observation; the exact interruption time is unavailable.").TakeLast(16).ToArray() };
                Write(session);
                Delete(PathFor(id, ".lease"));
            }
            result.Add(session);
        }
        foreach (var path in files.Where(p => Owned(p, out _) && p.EndsWith(".json.tmp", StringComparison.Ordinal))) Delete(path);
        Prune(result);
        return result;
    }

    void Delete(string path)
    {
        Safe(path);
        if (File.Exists(path)) File.Delete(path);
    }
    void Remove(Guid id)
    {
        foreach (var suffix in new[] { ".json", ".json.bak", ".json.tmp", ".lease" }) Delete(PathFor(id, suffix));
    }
    void Prune(List<DiagnosticSession> records, int reserve = 0)
    {
        foreach (var session in records.Where(s => s.Status != "recording" && s.StartedUtc < DateTimeOffset.UtcNow - Retention).ToArray())
        {
            if (IsLive(session.Id)) continue;
            Remove(session.Id); records.Remove(session);
        }
        while (records.Count + reserve > MaxSessions)
        {
            var oldest = records.Where(s => s.Status != "recording" && !IsLive(s.Id)).MinBy(s => s.EndedUtc ?? s.StartedUtc);
            if (oldest is null) throw new IOException("Session storage is full with active recordings.");
            Remove(oldest.Id); records.Remove(oldest);
        }
        var bytes = Directory.EnumerateFiles(directory).Sum(p => { Safe(p); return new FileInfo(p).Length; });
        // Leave room for one maximum-sized primary, backup and atomic staging file.
        while (bytes > 32L * 1024 * 1024 - 3L * (TelemetryData.SessionBytes + 1024))
        {
            var oldest = records.Where(s => s.Status != "recording" && !IsLive(s.Id)).MinBy(s => s.EndedUtc ?? s.StartedUtc);
            if (oldest is null) throw new IOException("Session storage byte quota is exhausted; corrupt or foreign files require inspection.");
            Remove(oldest.Id); records.Remove(oldest);
            bytes = Directory.EnumerateFiles(directory).Sum(p => new FileInfo(p).Length);
        }
    }

    void Write(DiagnosticSession session)
    {
        TelemetryData.Validate(session);
        if (JsonSerializer.SerializeToUtf8Bytes(session).Length > TelemetryData.SessionBytes)
            throw new InvalidDataException("Session response exceeds 300 KiB.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, DateTimeOffset.UtcNow, session));
        var path = PathFor(session.Id); var temp = PathFor(session.Id, ".json.tmp"); var backup = path + ".bak";
        Safe(path); Safe(temp); Safe(backup);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, backup); else File.Move(temp, path);
        }
        finally { Delete(temp); }
    }

    internal void Begin(DiagnosticSession session) => Locked(() =>
    {
        var records = Load();
        if (records.Count(s => s.Status == "recording") >= 4) throw new InvalidOperationException("At most four diagnostic sessions may record in this storage directory.");
        Prune(records, 1);
        var lease = new FileStream(PathFor(session.Id, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        leases.Add(session.Id, lease);
        try { Write(session); }
        catch { leases.Remove(session.Id); lease.Dispose(); Delete(PathFor(session.Id, ".lease")); throw; }
        return true;
    });
    internal void Save(DiagnosticSession session) => Locked(() => { Write(session); return true; });
    internal void Release(Guid id) => Locked(() => { if (leases.Remove(id, out var lease)) lease.Dispose(); Delete(PathFor(id, ".lease")); return true; });
    internal DiagnosticSession[] Snapshot() => Locked(() => Load().OrderByDescending(s => s.StartedUtc).ToArray());
    public void Dispose()
    {
        foreach (var lease in leases.Values) lease.Dispose();
        leases.Clear(); mutex.Dispose();
    }
}
