using System.Text.Json;
namespace JamesTools.Core.Recording;

public sealed record RecordingBookmark(Guid Id, Guid CaptureId, Guid? SessionId, string Label,
    long OffsetMilliseconds, DateTime ObservedUtc, string? RecordingPath);

/// <summary>Independent capture generations. A connection gap deliberately breaks continuity.</summary>
public sealed class BookmarkCaptureTracker
{
    public Guid? CaptureId { get; private set; }
    public Guid? SessionId { get; private set; }
    public long Generation { get; private set; }
    private long _duration = -1, _bytes = -1;
    private Guid? _stoppingCapture;
    private bool _stopping, _split, _startObserved;
    public void BreakContinuity()
    {
        CaptureId = null; SessionId = null; _duration = _bytes = -1; Generation++;
        _stoppingCapture = null; _stopping = _split = _startObserved = false;
    }
    public Guid? ObserveState(ObsRecordStateChangedEventArgs state)
    {
        if (state.State == "OBS_WEBSOCKET_OUTPUT_STARTING")
        {
            BreakContinuity();
            _startObserved = true;
        }
        else if (state.State == "OBS_WEBSOCKET_OUTPUT_STARTED") _startObserved = true;
        else if (state.State == "OBS_WEBSOCKET_OUTPUT_STOPPING")
        {
            if (!_stopping) _stoppingCapture = _startObserved && !_split ? CaptureId : null;
            _stopping = true;
            CaptureId = null;
            Generation++;
        }
        else if (state.State == "OBS_WEBSOCKET_OUTPUT_STOPPED")
        {
            var completed = _stoppingCapture;
            BreakContinuity();
            return completed;
        }
        return null;
    }
    public void FileChanged()
    {
        BreakContinuity();
        _split = true; // Output duration spans files; no proven file-relative offset is available.
    }
    public void Observe(ObsRecordStatus status, Guid? owner)
    {
        if (_stopping || _split) return;
        if (!status.Active) { BreakContinuity(); return; }
        if (_duration > status.DurationMilliseconds || _bytes > status.Bytes) { BreakContinuity(); owner = null; }
        if (CaptureId is null) { CaptureId = Guid.NewGuid(); SessionId = owner; Generation++; }
        _duration = status.DurationMilliseconds; _bytes = status.Bytes;
    }
    public RecordingBookmark Mark(long generation, ObsRecordStatus fresh, string label, DateTime utc)
    {
        if (generation != Generation || CaptureId is null || !fresh.Active || fresh.DurationMilliseconds < 0)
            throw new InvalidOperationException("No confirmed continuous full recording. Refresh OBS status and try again.");
        if (fresh.DurationMilliseconds < _duration || fresh.Bytes < _bytes)
        { BreakContinuity(); throw new InvalidOperationException("OBS counters reset; previous moments remain unresolved. Try again after status refresh."); }
        _duration = fresh.DurationMilliseconds; _bytes = fresh.Bytes;
        return new(Guid.NewGuid(), CaptureId.Value, SessionId, string.IsNullOrWhiteSpace(label) ? "Moment" : label.Trim(), fresh.DurationMilliseconds, utc, null);
    }
}

public sealed class RecordingBookmarkStore
{
    private sealed record Document(int FormatVersion, RecordingBookmark[] Bookmarks);
    private readonly string _path;
    private RecordingBookmark[] _items = [];
    public RecordingBookmarkStore(string directory)
    {
        _path = Path.Combine(directory, "bookmarks.json");
        if (!File.Exists(_path)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (doc is null || doc.FormatVersion != 1 || doc.Bookmarks is null || doc.Bookmarks.Any(b => b is null || b.Id == Guid.Empty || b.CaptureId == Guid.Empty || b.Label is null || b.OffsetMilliseconds < 0 || b.OffsetMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond || b.ObservedUtc == default || (b.RecordingPath is not null && !Path.IsPathFullyQualified(b.RecordingPath))) || doc.Bookmarks.Select(b => b.Id).Distinct().Count() != doc.Bookmarks.Length)
                throw new InvalidDataException("Invalid bookmark metadata; file preserved.");
            _items = doc.Bookmarks;
        }
        catch (JsonException ex) { throw new InvalidDataException("Corrupt bookmark metadata; file preserved.", ex); }
    }
    public RecordingBookmark[] Snapshot() => _items.Select(b => b with { }).ToArray();
    public void Add(RecordingBookmark bookmark) => Save(_items.Append(bookmark).ToArray());
    public void Edit(Guid id, string label) => Save(_items.Select(b => b.Id == id ? b with { Label = label } : b).ToArray());
    public void Remove(Guid id) => Save(_items.Where(b => b.Id != id).ToArray());
    public void ConfirmPath(Guid capture, string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("OBS did not report an absolute recording path.");
        if (_items.Any(b => b.CaptureId == capture && b.RecordingPath is not null && !string.Equals(b.RecordingPath, path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Conflicting capture paths; bookmarks left unchanged.");
        Save(_items.Select(b => b.CaptureId == capture ? b with { RecordingPath = path } : b).ToArray());
    }
    private void Save(RecordingBookmark[] next)
    {
        if (next.Any(b => b.Id == Guid.Empty || b.CaptureId == Guid.Empty || b.Label is null ||
            b.OffsetMilliseconds < 0 || b.OffsetMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond || b.ObservedUtc == default) ||
            next.Select(b => b.Id).Distinct().Count() != next.Length)
            throw new InvalidDataException("Invalid or duplicate bookmark metadata.");
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document(1, next), new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak"); else File.Move(temp, _path);
            _items = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
