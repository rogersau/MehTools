namespace JamesTools.Core.Sessions;

/// <summary>Turns explicit observations into sessions; callers provide clock and observations for deterministic use.</summary>
public sealed class SessionTracker(SessionLibraryStore store)
{
    private readonly Dictionary<string, Guid> _active = store.Snapshot()
        .Where(s => s.Status == SessionStatus.Active)
        .ToDictionary(s => s.ExecutableKey, s => s.Id, StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCheckpointUtc;

    public IReadOnlyDictionary<string, Guid> ActiveSessions => new Dictionary<string, Guid>(_active, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GameSession> Observe(DateTime utcNow, bool watching, IReadOnlyCollection<SessionGameObservation> observations)
    {
        utcNow = utcNow.ToUniversalTime();
        var seen = watching
            ? observations.Where(o => !string.IsNullOrWhiteSpace(o.ExecutablePath))
                .GroupBy(o => SessionLibraryStore.NormalizeExecutable(o.ExecutablePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SessionGameObservation>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<GameSession>();
        foreach (var key in _active.Keys.Where(key => !seen.ContainsKey(key)).ToArray())
        {
            var id = _active[key];
            store.End(id, utcNow, watching ? SessionStatus.Completed : SessionStatus.WatchingStopped);
            _active.Remove(key);
            if (store.Get(id) is { } ended) changed.Add(ended);
        }
        foreach (var (key, observation) in seen)
        {
            if (_active.TryGetValue(key, out var existing))
            {
                var persist = utcNow - _lastCheckpointUtc >= TimeSpan.FromMinutes(5);
                store.Checkpoint(existing, utcNow, persist);
                continue;
            }
            var session = store.Create(observation.Name, key, utcNow);
            _active[key] = session.Id;
            changed.Add(session);
        }
        if (_active.Count > 0 && utcNow - _lastCheckpointUtc >= TimeSpan.FromMinutes(5)) _lastCheckpointUtc = utcNow;
        return changed;
    }

    public GameSession? FindActive(string executablePath) => store.FindActive(executablePath);
}
