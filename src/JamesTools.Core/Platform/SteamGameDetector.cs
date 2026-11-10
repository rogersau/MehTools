using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JamesTools.Core.Platform;

public sealed record SteamGame(string Name, string RootDirectory);

public static class SteamGameDetector
{
    private static readonly HashSet<string> HelperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "crashreportclient.exe", "crashreportclienteditor.exe", "crashpad_handler.exe",
        "unitycrashhandler32.exe", "unitycrashhandler64.exe", "epicwebhelper.exe",
        "steam.exe", "steamservice.exe", "steamwebhelper.exe", "steamerrorreporter.exe",
        "gameoverlayui.exe", "unins000.exe", "uninstall.exe", "setup.exe", "redist.exe",
        "easyanticheat_eos_setup.exe", "easyanticheat_setup.exe", "battleye_setup.exe",
        "srcds.exe", "hlds.exe", "palserver.exe", "dedicatedserver.exe"
    };

    private static readonly Regex NonGameName = new(
        @"\b(?:dedicated\s+server|server|tools?|sdk|redistributables?|steamworks|proton)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServerProcess = new(
        @"(?:^|[_.-])(?:dedicated)?server(?:[_.-]|$)|dedicatedserver",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<SteamGame> Discover()
    {
        var steamRoots = RegistrySteamRoots();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles))
            steamRoots.Add(Path.Combine(programFiles, "Steam"));

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            if (!TryNormalizePath(root, out var normalized))
                continue;
            libraries.Add(normalized);
            AddLibraries(normalized, libraries);
        }

        var games = new Dictionary<string, SteamGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    var game = ReadManifest(manifest, steamApps);
                    if (game is not null)
                        games.TryAdd(game.RootDirectory, game);
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // Libraries can be disconnected while Steam is running.
            }
        }

        return games.Values.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<SteamGame> FindRunning(IReadOnlyList<SteamGame> catalog, IReadOnlyList<RunningProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(processes);
        var roots = new List<(SteamGame Game, string Root)>();
        foreach (var game in catalog)
        {
            if (!NonGameName.IsMatch(game.Name) && TryNormalizePath(game.RootDirectory, out var root))
                roots.Add((game, root));
        }
        // Nested installs should match the most specific game first.
        roots.Sort((left, right) => right.Root.Length.CompareTo(left.Root.Length));

        var matches = new Dictionary<string, SteamGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            if (string.IsNullOrWhiteSpace(process.FullPath) || !TryNormalizePath(process.FullPath, out var path))
                continue;
            var fileName = Path.GetFileName(path);
            if (HelperNames.Contains(fileName) || ServerProcess.IsMatch(Path.GetFileNameWithoutExtension(fileName)))
                continue;

            foreach (var (game, root) in roots)
            {
                if (!IsChildPath(path, root))
                    continue;
                matches.TryAdd(root, game);
                break;
            }
        }
        return matches.Values.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HashSet<string> RegistrySteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            AddRegistryPath(roots, RegistryHive.CurrentUser, view, @"Software\Valve\Steam", "SteamPath");
            AddRegistryPath(roots, RegistryHive.LocalMachine, view, @"Software\Valve\Steam", "InstallPath");
        }
        return roots;
    }

    private static void AddRegistryPath(HashSet<string> roots, RegistryHive hive, RegistryView view, string subKey, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key?.GetValue(value) is string path && !string.IsNullOrWhiteSpace(path))
                roots.Add(path);
        }
        catch (Exception ex) when (IsFileError(ex) || ex is SecurityException)
        {
            // Other registry views or the default installation directory may still work.
        }
    }

    private static void AddLibraries(string steamRoot, HashSet<string> libraries)
    {
        try
        {
            var entries = Vdf.Parse(File.ReadAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf")));
            var folders = entries.FirstOrDefault(entry => entry.Key.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase))?.Children;
            if (folders is null)
                return;
            foreach (var entry in folders)
            {
                if (!uint.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    continue;
                // Older libraryfolders files used a direct string instead of an object.
                var path = entry.Value ?? GetValue(entry.Children, "path");
                if (path is not null && TryNormalizePath(path, out var normalized))
                    libraries.Add(normalized);
            }
        }
        catch (Exception ex) when (IsFileError(ex) || ex is FormatException)
        {
            // Keep the main Steam library if the optional library list cannot be read.
        }
    }

    private static SteamGame? ReadManifest(string manifest, string steamApps)
    {
        try
        {
            var entries = Vdf.Parse(File.ReadAllText(manifest));
            var state = entries.FirstOrDefault(entry => entry.Key.Equals("AppState", StringComparison.OrdinalIgnoreCase))?.Children;
            var name = GetValue(state, "name");
            var installDir = GetValue(state, "installdir");
            var flagsText = GetValue(state, "StateFlags");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir) || NonGameName.IsMatch(name))
                return null;
            // Steam marks installed apps with bit 4. Older manifests can omit the field.
            if (flagsText is not null && uint.TryParse(flagsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) && (flags & 4) == 0)
                return null;
            if (Path.IsPathRooted(installDir))
                return null;
            var common = Path.GetFullPath(Path.Combine(steamApps, "common"));
            var path = Path.GetFullPath(Path.Combine(common, installDir));
            if (!IsChildPath(path, common) || !Directory.Exists(path))
                return null;
            return new SteamGame(name, path);
        }
        catch (Exception ex) when (IsFileError(ex) || ex is FormatException)
        {
            // A partially written or inaccessible manifest should not hide other games.
            return null;
        }
    }

    private static string? GetValue(List<Vdf.Entry>? entries, string key) =>
        entries?.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool TryNormalizePath(string path, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return false;
        }
    }

    private static bool IsChildPath(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        (Path.EndsInDirectorySeparator(root) || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;

    // Steam's KeyValues files are nested quoted key/value pairs with escaped paths.
    private sealed class Vdf
    {
        internal sealed record Entry(string Key, string? Value, List<Entry>? Children);
        private enum Kind { Text, Open, Close }
        private readonly record struct Token(Kind Kind, string Text);

        private readonly string text;
        private int position;

        private Vdf(string text) => this.text = text.TrimStart('\uFEFF');

        public static List<Entry> Parse(string text) => new Vdf(text).ReadEntries(false);

        private List<Entry> ReadEntries(bool insideObject)
        {
            var entries = new List<Entry>();
            while (Next() is { } key)
            {
                if (key.Kind == Kind.Close && insideObject)
                    return entries;
                if (key.Kind != Kind.Text || Next() is not { } value)
                    throw new FormatException("Invalid Steam KeyValues file.");
                if (value.Kind == Kind.Open)
                    entries.Add(new Entry(key.Text, null, ReadEntries(true)));
                else if (value.Kind == Kind.Text)
                    entries.Add(new Entry(key.Text, value.Text, null));
                else
                    throw new FormatException("Invalid Steam KeyValues file.");
            }
            if (insideObject)
                throw new FormatException("Unclosed Steam KeyValues object.");
            return entries;
        }

        private Token? Next()
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position]))
                {
                    position++;
                    continue;
                }
                if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n') position++;
                    continue;
                }
                break;
            }
            if (position >= text.Length) return null;
            var first = text[position++];
            if (first == '{') return new Token(Kind.Open, "{");
            if (first == '}') return new Token(Kind.Close, "}");
            if (first == '"')
            {
                var value = new StringBuilder();
                while (position < text.Length)
                {
                    var c = text[position++];
                    if (c == '"') return new Token(Kind.Text, value.ToString());
                    if (c == '\\' && position < text.Length && (text[position] == '\\' || text[position] == '"'))
                        c = text[position++];
                    value.Append(c);
                }
                throw new FormatException("Unclosed Steam KeyValues string.");
            }
            var start = position - 1;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '{' && text[position] != '}')
                position++;
            return new Token(Kind.Text, text[start..position]);
        }
    }
}
