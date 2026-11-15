using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JamesTools.Mcp;

public sealed record RunningHost(int ProcessId, DateTimeOffset ProcessStartedUtc, DateTimeOffset StartedUtc, string Transport, string? Endpoint);
public sealed record McpStatus(int SchemaVersion, string ConfigPath, bool ConfigurationExists, bool Enabled, bool RestartRequiredForChanges, IReadOnlyList<RunningHost> Hosts, IReadOnlyList<string> Warnings);
internal sealed record HostArtifact(int SchemaVersion, string ConfigPath, RunningHost Host);

/// <summary>A live-process fingerprint plus held file lease; stale JSON never proves a running host.</summary>
internal sealed class HostStatusLease : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _path;
    internal const int MaximumArtifacts = 256;
    private static string RuntimeDirectory => Path.Combine(ConfigurationStore.AppDataDirectory, "Mcp", "runtime");

    public HostStatusLease(string configPath, string transport, string? endpoint)
    {
        PrivateFiles.RestrictDirectory(RuntimeDirectory);
        using var coordinator = AcquireCoordinator(RuntimeDirectory);
        PruneStaleArtifacts(RuntimeDirectory);
        _path = Path.Combine(RuntimeDirectory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        _lease = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            PrivateFiles.RestrictFile(_path);
            using var process = Process.GetCurrentProcess();
            var host = new RunningHost(process.Id, new(process.StartTime.ToUniversalTime()), DateTimeOffset.UtcNow, transport, endpoint);
            JsonSerializer.Serialize(_lease, new HostArtifact(1, Path.GetFullPath(configPath), host), ConfigurationStore.Json);
            _lease.Flush(true);
        }
        catch { Dispose(); throw; }
    }

    private static FileStream AcquireCoordinator(string directory)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(directory, "host-status.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && deadline.Elapsed < TimeSpan.FromSeconds(2))
            { Thread.Sleep(25); }
        }
    }

    internal static void PruneStaleArtifacts(string directory)
    {
        // Inspection, allocation and deletion are bounded. Unknown/malformed files are never
        // deleted. A cluttered or fully occupied directory fails closed rather than growing.
        var paths = Directory.EnumerateFiles(directory, "*.json").Take(MaximumArtifacts + 1).ToArray();
        foreach (var path in paths.Take(MaximumArtifacts))
        {
            try
            {
                var match = Regex.Match(Path.GetFileName(path), @"^([1-9][0-9]{0,9})-[a-f0-9]{32}\.json$", RegexOptions.CultureInvariant);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var pid) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                // Opening for write succeeds only when no creator lease is held. Keep this
                // exclusive probe open through deletion to prevent a concurrent lease race.
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                if (probe.Length is < 1 or > 8192) continue;
                var artifact = JsonSerializer.Deserialize<HostArtifact>(probe, ConfigurationStore.Json);
                if (artifact?.SchemaVersion != 1 || artifact.Host is null || artifact.Host.ProcessId != pid ||
                    artifact.Host.ProcessStartedUtc.Offset != TimeSpan.Zero || artifact.Host.StartedUtc.Offset != TimeSpan.Zero ||
                    artifact.Host.ProcessStartedUtc == default || artifact.Host.StartedUtc == default ||
                    artifact.Host.Transport is not ("stdio" or "http") ||
                    string.IsNullOrWhiteSpace(artifact.ConfigPath) || !Path.IsPathFullyQualified(artifact.ConfigPath)) continue;
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { /* Held/live, malformed, inaccessible, or concurrently removed: leave it alone. */ }
        }
        if (Directory.EnumerateFiles(directory, "*.json").Take(MaximumArtifacts).Count() >= MaximumArtifacts)
            throw new IOException("Runtime status directory has reached its bounded artifact capacity.");
    }

    public static McpStatus Read(string configPath, McpConfiguration configuration)
    {
        var hosts = new List<RunningHost>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var canonicalPath = Path.GetFullPath(configPath);
        if (Directory.Exists(RuntimeDirectory))
        {
            try
            {
                var examined = 0;
                foreach (var path in Directory.EnumerateFiles(RuntimeDirectory, "*.json"))
                {
                    if (++examined > MaximumArtifacts) { warnings.Add("Runtime artifact scan limited to 256 entries; host list may be incomplete."); break; }
                    try
                    {
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (input.Length is < 1 or > 8192) continue;
                        var artifact = JsonSerializer.Deserialize<HostArtifact>(input, ConfigurationStore.Json);
                        if (artifact?.SchemaVersion != 1 || artifact.Host is null ||
                            !string.Equals(artifact.ConfigPath, canonicalPath, StringComparison.OrdinalIgnoreCase) ||
                            artifact.Host.ProcessId <= 0 || artifact.Host.Transport is not ("stdio" or "http")) continue;
                        using var process = Process.GetProcessById(artifact.Host.ProcessId);
                        if (process.HasExited || process.StartTime.ToUniversalTime() != artifact.Host.ProcessStartedUtc.UtcDateTime) continue;
                        // A successfully opened writer means the creator's live lease is absent.
                        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); continue; }
                        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { }
                        hosts.Add(artifact.Host);
                    }
                    catch (ArgumentException) { /* Process no longer exists. */ }
                    catch (FileNotFoundException) { /* Host exited during inspection. */ }
                    catch (DirectoryNotFoundException) { }
                    catch (JsonException) { warnings.Add("An invalid runtime artifact was ignored."); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    { warnings.Add("A runtime host could not be verified; it was omitted."); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Runtime status directory is unavailable; host list may be incomplete."); }
        }
        return new(1, canonicalPath, File.Exists(canonicalPath), configuration.Enabled, true, hosts.OrderBy(h => h.ProcessId).ToArray(), warnings.ToArray());
    }

    public void Dispose()
    {
        _lease.Dispose();
        try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
