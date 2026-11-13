using System.Text.Json;
using Microsoft.Win32;

namespace JamesTools.Core.Platform;

public sealed record LibraryGame(string Name, string RootDirectory, string Source);

public static class LibraryGameDetector
{
    public static IReadOnlyList<LibraryGame> Discover(AppDetectionOptions options)
    {
        var games = new Dictionary<string, LibraryGame>(StringComparer.OrdinalIgnoreCase);
        if (options.Steam) Add(games, SteamGameDetector.Discover().Select(g => new LibraryGame(g.Name, g.RootDirectory, "Steam")));
        if (options.Epic) Add(games, DiscoverEpic());
        if (options.Gog) Add(games, DiscoverGog());
        if (options.Ea) Add(games, DiscoverEa());
        if (options.Ubisoft) Add(games, DiscoverUbisoft());
        if (options.Xbox) Add(games, DiscoverXbox());
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<LibraryGame> FindRunning(IReadOnlyList<LibraryGame> catalog, IReadOnlyList<RunningProcess> processes)
    {
        // Reuse Steam-style matching: process under game root, exclude obvious helpers by name heuristics minimal.
        var roots = catalog.Where(g => TryNormalize(g.RootDirectory, out _))
            .Select(g => (Game: g, Root: Normalize(g.RootDirectory)))
            .OrderByDescending(x => x.Root.Length).ToArray();
        var matches = new Dictionary<string, LibraryGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            if (string.IsNullOrWhiteSpace(process.FullPath)) continue;
            string path;
            try { path = Normalize(process.FullPath); } catch { continue; }
            var file = Path.GetFileName(path);
            if (IsHelper(file)) continue;
            foreach (var (game, root) in roots)
            {
                if (IsChild(path, root)) { matches.TryAdd(root, game); break; }
            }
        }
        return matches.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Add(Dictionary<string, LibraryGame> games, IEnumerable<LibraryGame> items)
    {
        foreach (var g in items)
        {
            try { if (!string.IsNullOrWhiteSpace(g.RootDirectory) && Directory.Exists(g.RootDirectory)) games.TryAdd(Normalize(g.RootDirectory), g); }
            catch { }
        }
    }

    private static IEnumerable<LibraryGame> DiscoverEpic()
    {
        // Epic manifests: %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            LibraryGame? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("InstallLocation", out var loc) && root.TryGetProperty("DisplayName", out var name))
                {
                    var p = loc.GetString(); var n = name.GetString();
                    if (!string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(n)) game = new LibraryGame(n, p, "Epic");
                }
            }
            catch { }
            if (game is not null) yield return game;
        }
    }

    private static IEnumerable<LibraryGame> DiscoverGog()
    {
        var results = new List<LibraryGame>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            RegistryKey? baseKey = null; RegistryKey? gamesKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (gamesKey is null) continue;
                foreach (var id in gamesKey.GetSubKeyNames())
                {
                    using var key = gamesKey.OpenSubKey(id);
                    var path = key?.GetValue("path") as string ?? key?.GetValue("InstallPath") as string;
                    var name = key?.GetValue("gameName") as string ?? key?.GetValue("name") as string;
                    if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(name))
                        results.Add(new LibraryGame(name, path, "GOG"));
                }
            }
            catch { }
            finally { gamesKey?.Dispose(); baseKey?.Dispose(); }
        }
        return results;
    }

    private static IEnumerable<LibraryGame> DiscoverEa()
    {
        var results = new List<LibraryGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var root in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var gamesKey = baseKey.OpenSubKey(root);
                if (gamesKey is null) continue;
                foreach (var sub in gamesKey.GetSubKeyNames())
                {
                    using var key = gamesKey.OpenSubKey(sub);
                    var path = key?.GetValue("Install Dir") as string ?? key?.GetValue("InstallDir") as string;
                    var name = key?.GetValue("DisplayName") as string ?? sub;
                    if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(name) && seen.Add(NormalizeSafe(path)))
                        results.Add(new LibraryGame(name, path, "EA"));
                }
            }
            catch { }
        }
        return results;
    }

    private static IEnumerable<LibraryGame> DiscoverUbisoft()
    {
        var results = new List<LibraryGame>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var hive in new[] { RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var installs = baseKey.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
                if (installs is null) continue;
                foreach (var id in installs.GetSubKeyNames())
                {
                    using var key = installs.OpenSubKey(id);
                    var path = key?.GetValue("InstallDir") as string;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    if (string.IsNullOrWhiteSpace(name)) name = "Ubisoft " + id;
                    results.Add(new LibraryGame(name, path, "Ubisoft"));
                }
            }
            catch { }
        }
        return results;
    }

    private static IEnumerable<LibraryGame> DiscoverXbox()
    {
        // Microsoft Store/Xbox PC: best-effort via GamingServices/GameConfig or install roots is complex/restricted.
        // Use common XboxGames library folders as roots if present.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            string root;
            try { root = Path.Combine(drive.RootDirectory.FullName, "XboxGames"); } catch { continue; }
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name)) continue;
                yield return new LibraryGame(name, dir, "Xbox");
            }
        }
    }

    private static bool IsHelper(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        return n.Contains("crash") || n.Contains("report") || n.Contains("helper") || n.Contains("launcher") && n.Contains("web") ||
               n.Contains("unins") || n.Contains("setup") || n.Contains("redist") || n.Contains("easyanticheat") || n.Contains("battleye");
    }
    private static bool TryNormalize(string path, out string n) { try { n = Normalize(path); return true; } catch { n = ""; return false; } }
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string NormalizeSafe(string path) { try { return Normalize(path); } catch { return path; } }
    private static bool IsChild(string path, string root) => path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
}

public sealed record AppDetectionOptions(bool Steam, bool Epic, bool Gog, bool Ea, bool Ubisoft, bool Xbox);
