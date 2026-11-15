using System.Diagnostics;
using System.Text;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;

namespace JamesTools.Core.Mcp;

internal sealed record GameCatalog(IReadOnlyList<LibraryGame> Games, IReadOnlyList<string> Warnings);

internal static class GameInspectionReader
{
    internal static string? ComponentKind(string fileName) => fileName.ToLowerInvariant() switch
    {
        "nvngx_dlss.dll" => "dlss_super_resolution",
        "nvngx_dlssg.dll" => "dlss_frame_generation",
        "nvngx_dlssd.dll" => "dlss_ray_reconstruction",
        "libxess.dll" or "libxess_dx11.dll" => "xess_super_resolution",
        "libxess_fg.dll" => "xess_frame_generation",
        "dxgi.dll" or "d3d9.dll" or "d3d10.dll" or "d3d10_1.dll" or "d3d11.dll" or "d3d12.dll" or "d3d12core.dll" or "vulkan-1.dll" or "opengl32.dll" => "graphics_api_indicator",
        "reshade.dll" or "reshade64.dll" => "reshade_indicator",
        var name when name.EndsWith(".dll") && (name.StartsWith("ffx_fsr") || name.StartsWith("amd_fidelityfx")) => "fsr_indicator",
        var name when name.EndsWith(".exe") => "executable",
        _ => null
    };

    internal static string? NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path.AsSpan(2).Contains(':') || path.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))) return null;
        if (path.Split(['\\', '/']).Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return full.Length <= 3 ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    internal static void CheckAncestors(string path)
    {
        var normalized = NormalizeRoot(path) ?? throw new UnauthorizedAccessException("Only local exact paths are allowed.");
        if (new DriveInfo(Path.GetPathRoot(normalized)!).DriveType == DriveType.Network)
            throw new UnauthorizedAccessException("Mapped network drives are not allowed.");
        for (string? current = normalized; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Reparse points are not allowed.");
    }

    internal static GameCatalog Discover(string directory)
    {
        var warnings = new List<string>();
        var settings = new AppSettings();
        var path = Path.Combine(directory, "settings.json");
        try
        {
            if (File.Exists(path)) settings = new SettingsStore(directory).Deserialize(Encoding.UTF8.GetString(WindowsBackendSupport.ReadBounded(path, 2 * 1024 * 1024)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Security.SecurityException)
        { warnings.Add("Settings catalog: " + WindowsBackendSupport.Status(ex)); }
        var games = new List<LibraryGame>();
        // Configured executable parents are trusted roots only for exact path rules.
        foreach (var rule in settings.Games.Take(1000).Where(g => g.MatchMode == "path"))
            foreach (var executable in new[] { rule.ExecutablePath }.Concat(rule.ExecutableAliases.Take(32)))
                if (ExecutablePathRules.Normalize(executable) is { } exe && NormalizeRoot(Path.GetDirectoryName(exe)) is { } root)
                    games.Add(new(WindowsBackendSupport.Text(rule.Name), root, "settings"));
        try
        {
            games.AddRange(LibraryGameDetector.Discover(new(true, settings.DetectEpicGames, settings.DetectGogGames,
                settings.DetectEaGames, settings.DetectUbisoftGames, settings.DetectXboxGames)).Take(2000));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { warnings.Add("Library discovery: " + WindowsBackendSupport.Status(ex)); }
        return new(games.Where(g => NormalizeRoot(g.RootDirectory) is not null)
            .Select(g => g with { RootDirectory = NormalizeRoot(g.RootDirectory)!, Name = WindowsBackendSupport.Text(g.Name) })
            .DistinctBy(g => g.RootDirectory, StringComparer.OrdinalIgnoreCase).Take(2000).ToArray(), warnings);
    }

    internal static GameInspection Inspect(string request, GameCatalog catalog, CancellationToken cancellationToken)
    {
        GameInspection Result(string status, string warning, LibraryGame? game = null) => new(1, DateTimeOffset.UtcNow, status,
            game?.Name, game?.RootDirectory, [], catalog.Warnings.Append(warning).ToArray());
        if (string.IsNullOrWhiteSpace(request) || request.Length > 1024 || request.Any(char.IsControl))
            return Result("unsupported", "Supply a discovered game name or exact installation root.");
        var root = NormalizeRoot(request);
        var matches = catalog.Games.Where(g => root is not null
            ? string.Equals(g.RootDirectory, root, StringComparison.OrdinalIgnoreCase)
            : string.Equals(g.Name, request, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Colons and slashes can be part of a catalog title. A matching title never authorizes its text as a path.
        if (matches.Length == 0 && root is null && request.IndexOfAny(['\\', '/', ':']) >= 0)
            return Result("permission_denied", "Invalid game root; traversal, alternate streams, device and network paths are forbidden.");
        if (matches.Length != 1) return Result(matches.Length == 0 ? "permission_denied" : "unsupported",
            matches.Length == 0 ? "Game is not in the discovered/configured catalog." : "Ambiguous name; supply the exact catalog root.");
        var game = matches[0];
        var components = new List<GameComponent>();
        var warnings = catalog.Warnings.ToList();
        var timer = Stopwatch.StartNew();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((game.RootDirectory, 0));
        var visited = 0;
        var directories = 0;
        var truncated = false;
        try
        {
            CheckAncestors(game.RootDirectory);
            while (pending.TryDequeue(out var next))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++directories > 128 || timer.Elapsed > TimeSpan.FromSeconds(2)) { truncated = true; break; }
                CheckAncestors(next.Path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(next.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > 4096 || timer.Elapsed > TimeSpan.FromSeconds(2))
                    { truncated = true; break; }
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add("A reparse entry was skipped."); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (next.Depth < 3 && pending.Count < 128) pending.Enqueue((entry, next.Depth + 1));
                        else truncated = true;
                        continue;
                    }
                    var extension = Path.GetExtension(entry).ToLowerInvariant();
                    var kind = ComponentKind(Path.GetFileName(entry));
                    if (kind is null && extension is not (".ini" or ".cfg" or ".json" or ".toml" or ".log")) continue;
                    CheckAncestors(entry);
                    var relative = Path.GetRelativePath(game.RootDirectory, entry);
                    if (kind is not null)
                    {
                        if (components.Count(c => c.Kind == kind) >= (kind == "executable" ? 16 : 32)) { truncated = true; continue; }
                        if (new FileInfo(entry).Length > 256 * 1024 * 1024) { warnings.Add("Binary metadata skipped above 256 MiB."); continue; }
                        string? version = null;
                        try { version = WindowsBackendSupport.Text(FileVersionInfo.GetVersionInfo(entry).FileVersion); }
                        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                        { warnings.Add("Binary version metadata: " + WindowsBackendSupport.Status(ex)); }
                        components.Add(new(relative, kind, string.IsNullOrEmpty(version) ? null : version,
                            "Installed file identified by filename; version resource only. Not loaded or executed. API indicators may be proxy DLLs.", "unknown"));
                    }
                    else if (components.Count(c => c.Kind is "log" or "config") < 32)
                        components.Add(new(relative, extension == ".log" ? "log" : "config", null,
                            "File exists; contents not read. Existence does not establish active use.", "unknown"));
                    else truncated = true;
                }
                if (visited > 4096 || timer.Elapsed > TimeSpan.FromSeconds(2)) break;
            }
            if (components.Count > 128) truncated = true;
            if (truncated) warnings.Add("Inspection bounded to 3 directory levels, 128 directories/components, 4096 entries and 2 seconds; results may be incomplete.");
            warnings.Add("Installed does not mean active. Runtime injection, configuration contents and log contents were not inspected.");
            return new(1, DateTimeOffset.UtcNow, truncated || warnings.Count > 1 ? "partial" : "available", game.Name, game.RootDirectory,
                components.OrderBy(c => c.Kind is "config" or "log" ? 2 : c.Kind == "executable" ? 1 : 0).Take(128).ToArray(), warnings.Distinct().Take(32).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { return new(1, DateTimeOffset.UtcNow, WindowsBackendSupport.Status(ex), game.Name, game.RootDirectory, components.Take(128).ToArray(), warnings.Append("Game inspection could not complete; " + WindowsBackendSupport.Status(ex)).Distinct().Take(32).ToArray()); }
    }
}
