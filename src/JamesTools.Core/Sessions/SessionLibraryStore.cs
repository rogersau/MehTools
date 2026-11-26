using System.Text.Json;

namespace JamesTools.Core.Sessions;

/// <summary>Local JSON store for game-session history and references to files.</summary>
public sealed class SessionLibraryStore
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly List<GameSession> _sessions = [];
    private GameSession[] _savedSnapshot = [];

    public SessionLibraryStore(string directory)
    {
        DirectoryPath = directory;
        FilePath = Path.Combine(directory, "sessions.json");
        if (File.Exists(FilePath)) Load();
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }

    public IReadOnlyList<GameSession> Snapshot()
    {
        lock (_sync) return _sessions.Select(Clone).ToArray();
    }

    public GameSession? Get(Guid id)
    {
        lock (_sync) return _sessions.FirstOrDefault(s => s.Id == id) is { } session ? Clone(session) : null;
    }

    public Guid? FindVideoSession(string fullPath)
    {
        lock (_sync)
        {
            var matches = _sessions.Where(session => session.Attachments.Any(attachment =>
                attachment.Kind is SessionAttachmentKind.Recording or SessionAttachmentKind.Clip &&
                string.Equals(attachment.Path, fullPath, StringComparison.OrdinalIgnoreCase))).ToArray();
            var gameSessions = matches.Where(session => !string.IsNullOrWhiteSpace(session.ExecutableKey)).ToArray();
            if (gameSessions.Length == 1) return gameSessions[0].Id;
            return matches.Length == 1 ? matches[0].Id : null;
        }
    }

    public GameSession Create(string name, string executableKey, DateTime observedUtc, SessionStatus status = SessionStatus.Active)
    {
        lock (_sync)
        {
            var session = new GameSession
            {
                Id = Guid.NewGuid(), GameName = name, ExecutableKey = executableKey,
                StartedUtc = observedUtc, LastObservedUtc = observedUtc,
                EndedUtc = status == SessionStatus.Active ? null : observedUtc, Status = status
            };
            _sessions.Add(session);
            SaveLocked();
            return Clone(session);
        }
    }

    public bool End(Guid id, DateTime observedUtc, SessionStatus status)
    {
        lock (_sync)
        {
            var index = _sessions.FindIndex(s => s.Id == id);
            if (index < 0 || _sessions[index].Status != SessionStatus.Active) return false;
            observedUtc = observedUtc < _sessions[index].LastObservedUtc ? _sessions[index].LastObservedUtc : observedUtc;
            _sessions[index] = _sessions[index] with
            {
                EndedUtc = observedUtc, LastObservedUtc = observedUtc, Status = status
            };
            SaveLocked();
            return true;
        }
    }

    /// <summary>Updates the crash-recovery checkpoint without writing more often than the caller chooses.</summary>
    public bool Checkpoint(Guid id, DateTime observedUtc, bool persist)
    {
        lock (_sync)
        {
            var index = _sessions.FindIndex(s => s.Id == id && s.Status == SessionStatus.Active);
            if (index < 0 || observedUtc <= _sessions[index].LastObservedUtc) return false;
            _sessions[index] = _sessions[index] with { LastObservedUtc = observedUtc };
            if (persist) SaveLocked();
            return true;
        }
    }

    public bool UpdateDetails(Guid id, string? notes = null, bool? favourite = null)
    {
        lock (_sync)
        {
            var index = _sessions.FindIndex(s => s.Id == id);
            if (index < 0) return false;
            _sessions[index] = _sessions[index] with
            {
                Notes = notes ?? _sessions[index].Notes,
                Favourite = favourite ?? _sessions[index].Favourite
            };
            SaveLocked();
            return true;
        }
    }

    public Guid AddAttachment(Guid? sessionId, string? unassignedName, string executableKey,
        SessionAttachmentKind kind, string path, DateTime observedUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An attachment path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        lock (_sync)
        {
            var duplicate = _sessions.FirstOrDefault(s => s.Attachments.Any(a =>
                string.Equals(a.Path, fullPath, StringComparison.OrdinalIgnoreCase)));
            if (duplicate is not null) return duplicate.Id;
            var index = sessionId is Guid id ? _sessions.FindIndex(s => s.Id == id) : -1;
            if (index < 0)
            {
                var session = new GameSession
                {
                    Id = Guid.NewGuid(), GameName = string.IsNullOrWhiteSpace(unassignedName) ? "Unassigned capture" : unassignedName,
                    ExecutableKey = executableKey ?? "", StartedUtc = observedUtc, LastObservedUtc = observedUtc,
                    EndedUtc = observedUtc, Status = SessionStatus.Completed
                };
                _sessions.Add(session);
                index = _sessions.Count - 1;
            }
            var target = _sessions[index];
            _sessions[index] = target with
            {
                Attachments = target.Attachments.Select(Clone).Append(new SessionAttachment
                {
                    Kind = kind, Path = fullPath, ObservedUtc = observedUtc
                }).ToList()
            };
            SaveLocked();
            return _sessions[index].Id;
        }
    }

    /// <summary>Adds a user-selected existing file to the explicitly selected session.</summary>
    public bool AttachExplicitly(Guid sessionId, SessionAttachmentKind kind, string path, DateTime observedUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An attachment path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        lock (_sync)
        {
            var index = _sessions.FindIndex(s => s.Id == sessionId);
            if (index < 0) return false;
            var target = _sessions[index];
            if (target.Attachments.Any(a => string.Equals(a.Path, fullPath, StringComparison.OrdinalIgnoreCase))) return true;
            _sessions[index] = target with
            {
                Attachments = target.Attachments.Append(new SessionAttachment
                {
                    Kind = kind, Path = fullPath, ObservedUtc = observedUtc
                }).ToList()
            };
            SaveLocked();
            return true;
        }
    }

    public GameSession? FindActive(string executablePath)
    {
        var key = NormalizeExecutable(executablePath);
        lock (_sync) return _sessions.LastOrDefault(s => s.Status == SessionStatus.Active &&
            string.Equals(s.ExecutableKey, key, StringComparison.OrdinalIgnoreCase)) is { } session ? Clone(session) : null;
    }

    public GameSession? FindPreviousReport(GameSession current)
    {
        lock (_sync) return _sessions.Where(s => s.Id != current.Id && s.Status != SessionStatus.Active &&
                !string.IsNullOrWhiteSpace(current.ExecutableKey) &&
                string.Equals(s.ExecutableKey, current.ExecutableKey, StringComparison.OrdinalIgnoreCase) &&
                s.StartedUtc < current.StartedUtc &&
                s.Attachments.Any(a => a.Kind == SessionAttachmentKind.PerformanceReport && File.Exists(a.Path)))
            .OrderByDescending(s => s.StartedUtc).Select(Clone).FirstOrDefault();
    }

    public static string NormalizeExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (ArgumentException) { return path.Trim(); }
    }

    private void Load()
    {
        SessionFile? file;
        try { file = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(FilePath), JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("The session history file is not valid JSON. It was left untouched.", ex); }
        if (file is null) throw new InvalidDataException("The session history file is empty. It was left untouched.");
        if (file.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Session history format {file.FormatVersion} is not supported. The file was left untouched.");
        if (file.Sessions is null || file.Sessions.Any(s => s is null || s.Id == Guid.Empty || s.StartedUtc == default ||
            s.LastObservedUtc == default || s.StartedUtc > s.LastObservedUtc ||
            (s.EndedUtc is DateTime ended && ended < s.StartedUtc) ||
            string.IsNullOrWhiteSpace(s.GameName) || s.ExecutableKey is null || s.Notes is null || !Enum.IsDefined(s.Status) ||
            s.Attachments is null || s.Attachments.Any(a => a is null || string.IsNullOrWhiteSpace(a.Path) ||
                a.ObservedUtc == default || !Enum.IsDefined(a.Kind))))
            throw new InvalidDataException("The session history file contains invalid entries. It was left untouched.");
        if (file.Sessions.Select(s => s.Id).Distinct().Count() != file.Sessions.Count)
            throw new InvalidDataException("The session history file contains duplicate session IDs. It was left untouched.");
        lock (_sync)
        {
            _sessions.Clear();
            _sessions.AddRange(file.Sessions.Select(Clone));
            _savedSnapshot = _sessions.Select(Clone).ToArray();
            var recovered = false;
            for (var i = 0; i < _sessions.Count; i++)
            {
                var session = _sessions[i];
                if (session.Status != SessionStatus.Active) continue;
                _sessions[i] = session with { Status = SessionStatus.Interrupted, EndedUtc = session.LastObservedUtc };
                recovered = true;
            }
            if (recovered) SaveLocked();
        }
    }

    private void SaveLocked()
    {
        var temporary = Path.Combine(DirectoryPath, $"sessions.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SessionFile
            {
                FormatVersion = CurrentFormatVersion, Sessions = _sessions.Select(Clone).ToList()
            }, JsonOptions));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
            _savedSnapshot = _sessions.Select(Clone).ToArray();
        }
        catch
        {
            // A failed write must not publish a mutation that the tracker believes
            // failed, or its next observation could create duplicate sessions.
            _sessions.Clear();
            _sessions.AddRange(_savedSnapshot.Select(Clone));
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static GameSession Clone(GameSession s) => s with { Attachments = s.Attachments.Select(Clone).ToList() };
    private static SessionAttachment Clone(SessionAttachment a) => a with { };
    private sealed class SessionFile { public int FormatVersion { get; set; } public List<GameSession>? Sessions { get; set; } }
}
