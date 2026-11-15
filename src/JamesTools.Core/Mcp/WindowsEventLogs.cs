using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Xml.Linq;

namespace JamesTools.Core.Mcp;

internal sealed record WindowsEventQuery(string[] Logs, string[] XPaths, int Limit)
{
    internal string XPath => XPaths[0];
    internal string Structured(string log) => new XElement("QueryList", new XElement("Query", new XAttribute("Id", 0),
        new XAttribute("Path", log), XPaths.Select(xpath => new XElement("Select", new XAttribute("Path", log), xpath)))).ToString(SaveOptions.DisableFormatting);
}

internal static class WindowsEventLogs
{
    private static readonly IReadOnlyDictionary<string, string[]> Sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = ["Microsoft-Windows-WHEA-Logger", "Microsoft-Windows-Kernel-Processor-Power"],
        ["gpu"] = ["Display", "nvlddmkm", "amdkmdag", "amdwddmg"],
        ["memory"] = ["Microsoft-Windows-Resource-Exhaustion-Detector", "Microsoft-Windows-WHEA-Logger"],
        ["storage"] = ["disk", "Ntfs", "storahci", "stornvme", "Microsoft-Windows-StorPort"],
        ["network"] = ["Microsoft-Windows-NDIS", "Microsoft-Windows-TCPIP", "Microsoft-Windows-DNS-Client"],
        ["drivers"] = ["Microsoft-Windows-Kernel-PnP", "Microsoft-Windows-DriverFrameworks-UserMode"],
        ["windows"] = ["Microsoft-Windows-Kernel-Power", "EventLog", "Microsoft-Windows-WER-SystemErrorReporting", "Application Error", "Windows Error Reporting", ".NET Runtime"]
    };

    internal static WindowsEventQuery Validate(EventLogFilter filter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Limit is < 1 or > 200) throw new ArgumentException("Event limit must be 1–200.");
        var to = filter.ToUtc?.ToUniversalTime() ?? now;
        var from = filter.FromUtc?.ToUniversalTime() ?? to.AddHours(-24);
        if (from > to || to - from > TimeSpan.FromDays(7) || to > now.AddMinutes(1))
            throw new ArgumentException("Event window must be ordered, at most seven days, and not in the future.");
        var category = filter.Category?.ToLowerInvariant() ?? "windows";
        string[] logs = category switch { "system" => ["System"], "application" => ["Application"], _ => ["System", "Application"] };
        if (category is not ("system" or "application") && !Sources.ContainsKey(category)) throw new ArgumentException("Unsupported event category.");
        var providers = category is "system" or "application" || filter.Category is null
            ? Sources.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : Sources[category];
        if (filter.Source is not null)
        {
            var source = providers.FirstOrDefault(s => string.Equals(s, filter.Source, StringComparison.OrdinalIgnoreCase));
            if (source is null) throw new ArgumentException("Event source is not in the selected category allowlist.");
            providers = [source];
        }
        var level = filter.Severity?.ToLowerInvariant() switch
        {
            null => "(Level=1 or Level=2 or Level=3)",
            "critical" => "Level=1", "error" => "Level=2", "warning" => "Level=3", "information" => "Level=4", "verbose" => "Level=5",
            _ => throw new ArgumentException("Unsupported event severity.")
        };
        if (filter.EventIds is { } ids && (ids.Length is < 1 or > 32 || ids.Any(id => id is < 0 or > 65535)))
            throw new ArgumentException("Supply 1–32 event IDs in the range 0–65535.");
        // Split provider and ID clauses to remain below Windows Event Log's 20-expression XPath limit.
        var idGroups = filter.EventIds is { } eventIds ? eventIds.Distinct().Chunk(4).Select(ids => " and (" +
            string.Join(" or ", ids.Select(id => "EventID=" + id.ToString(CultureInfo.InvariantCulture))) + ")").ToArray() : [""];
        static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        var xpaths = providers.Chunk(4).SelectMany(group => idGroups.Select(idClause =>
        {
            // All provider strings are constants, never caller-supplied XPath.
            var providerClause = string.Join(" or ", group.Select(p => "Provider[@Name='" + p + "']"));
            return $"*[System[({providerClause}) and ({level}){idClause} and TimeCreated[@SystemTime>='{Utc(from)}' and @SystemTime<='{Utc(to)}']]]";
        })).ToArray();
        return new(logs, xpaths, filter.Limit);
    }

    internal static EventLogResult Read(EventLogFilter filter, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        WindowsEventQuery query;
        try { query = Validate(filter, now); }
        catch (ArgumentException ex) { return new(1, now, "unsupported", [], false, [ex.Message]); }
        var events = new List<DiagnosticEvent>();
        var warnings = new List<string>();
        var failures = new List<string>();
        var truncated = false;
        var clock = Stopwatch.StartNew();
        foreach (var log in query.Logs)
        {
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query.Structured(log)) { ReverseDirection = true }) { BatchSize = 1 };
                var count = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.Elapsed > TimeSpan.FromSeconds(2)) { truncated = true; break; }
                    using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(200));
                    if (record is null) break;
                    if (++count > query.Limit) { truncated = true; break; }
                    if (record.TimeCreated is not { } time) { warnings.Add("An event without a timestamp was omitted."); continue; }
                    var severity = record.Level switch { 1 => "critical", 2 => "error", 3 => "warning", 4 => "information", 5 => "verbose", _ => "unknown" };
                    var source = record.ProviderName ?? "Unknown";
                    events.Add(new(new DateTimeOffset(time.ToUniversalTime()), source, record.Id, severity, log,
                        Summary(source, record.Id, severity)));
                    // Do not call FormatDescription(), ToXml(), or read event Properties/UserId/MachineName.
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or SecurityException or TimeoutException)
            {
                var status = ex is EventLogException eventError && (eventError.HResult & 0xffff) == 5 ? "permission_denied" : WindowsBackendSupport.Status(ex);
                failures.Add(status);
                warnings.Add(log + ": " + status + "; event payload omitted.");
            }
        }
        if (events.Count > query.Limit) truncated = true;
        if (truncated) warnings.Add("Event results truncated by count/time budget.");
        return new(1, now, failures.Count == 0 ? "available" : events.Count > 0 ? "partial" : failures[0],
            events.OrderByDescending(e => e.TimestampUtc).Take(query.Limit).ToArray(), truncated, warnings.Distinct().ToArray());
    }

    internal static string Summary(string source, int id, string severity) => (source, id) switch
    {
        ("Microsoft-Windows-Kernel-Power", 41) => "Windows reported an unexpected restart; this event alone does not identify its cause.",
        ("Display", 4101) => "Windows reported a display driver recovery; event payload omitted.",
        ("Microsoft-Windows-Resource-Exhaustion-Detector", 2004) => "Windows reported resource exhaustion; process and user details omitted.",
        _ => $"Windows recorded a {severity} event; message, identifiers and payload omitted."
    };
}
