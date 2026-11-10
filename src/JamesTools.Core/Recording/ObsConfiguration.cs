using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JamesTools.Core.Recording;

internal static class ObsConfiguration
{
    internal sealed record Preparation(ObsFileTransaction? Transaction, bool WaitingForWindow, string Message);

    internal static Preparation Prepare(string root, string recordingDirectory, int gamePid, string gameExecutable,
        Func<int, string, GameWindow?> findWindow)
    {
        var userIniPath = Path.Combine(root, "user.ini");
        if (!File.Exists(userIniPath)) throw new InvalidDataException("OBS user.ini was not found in the selected configuration folder.");
        var userBytes = File.ReadAllBytes(userIniPath);
        var user = IniDocument.Parse(userBytes);
        var profile = SafeComponent(user.GetValue("Basic", "ProfileDir"), "profile directory");
        var scene = SafeComponent(user.GetValue("Basic", "SceneCollectionFile"), "scene collection");
        if (!scene.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) scene += ".json";

        var profilePath = Path.Combine(root, "basic", "profiles", profile, "basic.ini");
        var scenePath = Path.Combine(root, "basic", "scenes", scene);
        if (!File.Exists(profilePath)) throw new InvalidDataException("The selected OBS profile basic.ini could not be found.");
        if (!File.Exists(scenePath)) throw new InvalidDataException("The selected OBS scene collection could not be found.");

        // Parse both files and validate the source before creating backups or modifying anything.
        var profileBytes = File.ReadAllBytes(profilePath);
        var profileIni = IniDocument.Parse(profileBytes);
        if (string.Equals(profileIni.GetValue("Output", "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase))
        {
            var recordingType = profileIni.GetValue("AdvOut", "RecType");
            if (!string.IsNullOrEmpty(recordingType) && !string.Equals(recordingType, "Standard", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This OBS profile uses a custom recording output. Choose standard recording in OBS before using this folder setting.");
        }
        var sceneBytes = File.ReadAllBytes(scenePath);
        var sceneText = DecodeUtf8(sceneBytes);
        var rootNode = JsonNode.Parse(sceneText) as JsonObject
            ?? throw new InvalidDataException("The OBS scene collection is not a JSON object.");
        if (rootNode["sources"] is not JsonArray sources)
            throw new InvalidDataException("The OBS scene collection has no sources list.");

        var captures = VisibleGameCaptures(rootNode, sources,
            string.Equals(user.GetValue("BasicWindow", "PreviewProgramMode"), "true", StringComparison.OrdinalIgnoreCase));
        if (captures.Count == 0) throw new InvalidDataException("The active OBS program scene has no visible Game Capture source.");
        var exeName = Path.GetFileName(gameExecutable);
        var matching = captures.Where(source => source["settings"] is JsonObject sourceSettings &&
            string.Equals(ObsWindows.GetConfiguredExecutable(StringValue(sourceSettings["window"])), exeName,
                StringComparison.OrdinalIgnoreCase)).ToList();
        JsonObject capture;
        if (captures.Count == 1) capture = captures[0];
        else if (matching.Count == 1) capture = matching[0];
        else throw new InvalidDataException("The scene collection has multiple Game Capture sources; select a collection with one unambiguous source.");

        if (capture["settings"] is not JsonObject captureSettings)
            throw new InvalidDataException("The OBS Game Capture source has no settings object.");

        bool alreadyTargeted = string.Equals(
            ObsWindows.GetConfiguredExecutable(StringValue(captureSettings["window"])), exeName,
            StringComparison.OrdinalIgnoreCase);
        byte[]? newSceneBytes = null;
        bool sceneChanged = false;
        if (!alreadyTargeted)
        {
            var window = findWindow(gamePid, exeName);
            if (window is null)
                return new Preparation(null, true, "Waiting for a visible window belonging to the selected game.");
            captureSettings["window"] = ObsWindows.EncodeWindow(window);
            captureSettings["capture_mode"] = "window";
            captureSettings["priority"] = 2; // OBS WINDOW_PRIORITY_EXE
            sceneChanged = true;
        }
        else if (!string.Equals(StringValue(captureSettings["capture_mode"]), "window", StringComparison.Ordinal))
        {
            // Reuse the valid target without waiting for a window, but make sure
            // OBS is actually configured to capture that target.
            captureSettings["capture_mode"] = "window";
            sceneChanged = true;
        }
        if (sceneChanged)
        {
            var updatedScene = rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            newSceneBytes = EncodeUtf8(updatedScene, HasUtf8Bom(sceneBytes));
        }

        var escapedDirectory = recordingDirectory.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        profileIni.SetValue("SimpleOutput", "FilePath", escapedDirectory);
        profileIni.SetValue("AdvOut", "RecFilePath", escapedDirectory);
        var updates = new List<ObsFileChange>();
        var newProfileBytes = profileIni.ToBytes();
        if (!profileBytes.SequenceEqual(newProfileBytes)) updates.Add(new ObsFileChange(profilePath, profileBytes, newProfileBytes));
        if (newSceneBytes is not null && !sceneBytes.SequenceEqual(newSceneBytes))
            updates.Add(new ObsFileChange(scenePath, sceneBytes, newSceneBytes));
        var watched = new[]
        {
            new ObsFileSnapshot(userIniPath, userBytes),
            new ObsFileSnapshot(profilePath, profileBytes),
            new ObsFileSnapshot(scenePath, sceneBytes)
        };
        return new Preparation(new ObsFileTransaction(updates, watched), false, "OBS configuration is ready.");
    }

    private static string? StringValue(JsonNode? value) => value is JsonValue j && j.TryGetValue<string>(out var s) ? s : null;

    private static List<JsonObject> VisibleGameCaptures(JsonObject collection, JsonArray sources, bool studioMode)
    {
        string sceneKey = studioMode ? "current_program_scene" : "current_scene";
        var activeName = StringValue(collection[sceneKey]);
        if (string.IsNullOrWhiteSpace(activeName))
            throw new InvalidDataException("The OBS scene collection does not identify its active program scene.");

        var allSources = sources.OfType<JsonObject>().ToList();
        if (collection["groups"] is JsonArray groups) allSources.AddRange(groups.OfType<JsonObject>());
        var activeScenes = allSources.Where(source => StringValue(source["id"]) == "scene" &&
            string.Equals(StringValue(source["name"]), activeName, StringComparison.Ordinal)).ToList();
        if (activeScenes.Count != 1)
            throw new InvalidDataException("The active OBS program scene could not be found unambiguously.");

        var captures = new List<JsonObject>();
        var visited = new HashSet<JsonObject>();
        void Visit(JsonObject source)
        {
            if (!visited.Add(source) || IsExplicitlyFalse(source["enabled"])) return;
            var id = StringValue(source["id"]);
            if (id == "game_capture") { captures.Add(source); return; }
            if (id is not ("scene" or "group") || source["settings"] is not JsonObject settings ||
                settings["items"] is not JsonArray items) return;
            foreach (var item in items.OfType<JsonObject>())
            {
                // OBS ignores flattened backup copies of group items. Missing
                // visibility also defaults to false in OBS scene_load_item.
                if (!IsExplicitlyTrue(item["visible"]) || IsExplicitlyTrue(item["group_item_backup"])) continue;
                var uuid = StringValue(item["source_uuid"]);
                var name = StringValue(item["name"]);
                var matches = uuid?.Length != 36 ? new List<JsonObject>() : allSources.Where(candidate =>
                    string.Equals(StringValue(candidate["uuid"]), uuid, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0 && !string.IsNullOrEmpty(name))
                    matches = allSources.Where(candidate => string.Equals(StringValue(candidate["name"]), name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 1) Visit(matches[0]);
            }
        }
        Visit(activeScenes[0]);
        return captures;
    }

    private static bool IsExplicitlyFalse(JsonNode? value) =>
        value is JsonValue j && j.TryGetValue<bool>(out var present) && !present;

    private static bool IsExplicitlyTrue(JsonNode? value) =>
        value is JsonValue j && j.TryGetValue<bool>(out var present) && present;

    private static string SafeComponent(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value != Path.GetFileName(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException($"OBS has no valid selected {description} in user.ini.");
        return value;
    }

    internal static bool HasUtf8Bom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
    internal static string DecodeUtf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes, HasUtf8Bom(bytes) ? 3 : 0,
        bytes.Length - (HasUtf8Bom(bytes) ? 3 : 0));
    internal static byte[] EncodeUtf8(string text, bool bom)
    {
        var content = new UTF8Encoding(false, true).GetBytes(text);
        if (!bom) return content;
        return new byte[] { 0xef, 0xbb, 0xbf }.Concat(content).ToArray();
    }
}

// OBS's config format escapes backslashes, CR and LF in string values. Keep
// every unrelated line and its line ending intact instead of rewriting the INI.
internal sealed class IniDocument
{
    private sealed record Line(string Text, string Ending);
    private readonly List<Line> _lines;
    private readonly bool _bom;
    private readonly string _newline;

    private IniDocument(List<Line> lines, bool bom, string newline) => (_lines, _bom, _newline) = (lines, bom, newline);
    internal static IniDocument Read(string path) => Parse(File.ReadAllBytes(path));

    internal static IniDocument Parse(byte[] bytes)
    {
        var text = ObsConfiguration.DecodeUtf8(bytes);
        var lines = new List<Line>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            bool cr = i > start && text[i - 1] == '\r';
            lines.Add(new Line(text.Substring(start, i - start - (cr ? 1 : 0)), cr ? "\r\n" : "\n"));
            start = i + 1;
        }
        if (start < text.Length) lines.Add(new Line(text[start..], ""));
        return new IniDocument(lines, ObsConfiguration.HasUtf8Bom(bytes), text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
    }

    internal string? GetValue(string section, string key)
    {
        var (_, _, matches) = Find(section, key);
        if (matches.Count == 0) return null;
        if (matches.Count != 1) throw new InvalidDataException($"OBS configuration has duplicate {section}/{key} entries.");
        var line = _lines[matches[0]].Text;
        return Unescape(line[(line.IndexOf('=') + 1)..].Trim());
    }

    internal void SetValue(string section, string key, string escapedValue)
    {
        var (sectionIndex, nextSection, matches) = Find(section, key);
        if (matches.Count > 1) throw new InvalidDataException($"OBS configuration has duplicate {section}/{key} entries.");
        if (matches.Count == 1)
        {
            int index = matches[0];
            var line = _lines[index];
            _lines[index] = line with { Text = line.Text[..(line.Text.IndexOf('=') + 1)] + escapedValue };
            return;
        }
        if (sectionIndex < 0)
        {
            EnsureEndingBefore(_lines.Count);
            _lines.Add(new Line($"[{section}]", _newline));
            _lines.Add(new Line($"{key}={escapedValue}", _newline));
        }
        else
        {
            EnsureEndingBefore(nextSection);
            _lines.Insert(nextSection, new Line($"{key}={escapedValue}", _newline));
        }
    }

    internal byte[] ToBytes() => ObsConfiguration.EncodeUtf8(string.Concat(_lines.Select(line => line.Text + line.Ending)), _bom);

    private void EnsureEndingBefore(int index)
    {
        if (index > 0 && _lines[index - 1].Ending.Length == 0)
            _lines[index - 1] = _lines[index - 1] with { Ending = _newline };
    }

    private (int Section, int NextSection, List<int> Matches) Find(string section, string key)
    {
        var sectionIndex = -1;
        var nextSection = _lines.Count;
        var inside = false;
        var matches = new List<int>();
        for (int i = 0; i < _lines.Count; i++)
        {
            string line = _lines[i].Text.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                if (inside) { nextSection = i; inside = false; }
                if (string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
                {
                    if (sectionIndex >= 0) throw new InvalidDataException($"OBS configuration has duplicate [{section}] sections.");
                    sectionIndex = i;
                    inside = true;
                }
                continue;
            }
            var equals = line.IndexOf('=');
            if (inside && equals >= 0 && string.Equals(line[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }
        return (sectionIndex, nextSection, matches);
    }

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length) { result.Append(value[i]); continue; }
            char next = value[i + 1];
            if (next is '\\' or 'r' or 'n')
            {
                result.Append(next == 'r' ? '\r' : next == 'n' ? '\n' : '\\');
                i++;
            }
            else result.Append(value[i]);
        }
        return result.ToString();
    }
}

internal sealed record ObsFileChange(string Path, byte[] Original, byte[] Replacement);
internal sealed record ObsFileSnapshot(string Path, byte[] Original);

internal sealed class ObsFileTransaction
{
    private readonly IReadOnlyList<ObsFileChange> _changes;
    private readonly IReadOnlyList<ObsFileSnapshot> _watched;
    private readonly List<(ObsFileChange Change, string Backup)> _applied = new();

    internal ObsFileTransaction(IReadOnlyList<ObsFileChange> changes, IReadOnlyList<ObsFileSnapshot> watched) =>
        (_changes, _watched) = (changes, watched);

    internal void VerifyStable()
    {
        foreach (var snapshot in _watched)
        {
            var applied = _applied.Find(entry => string.Equals(entry.Change.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase));
            var expected = applied.Change is null ? snapshot.Original : applied.Change.Replacement;
            if (!File.ReadAllBytes(snapshot.Path).SequenceEqual(expected))
                throw new IOException("An OBS configuration file changed while recording was being prepared.");
        }
    }

    internal void Apply(Func<bool> isObsRunning)
    {
        var staged = new List<(ObsFileChange Change, string Backup, string Temp)>();
        try
        {
            VerifyStable();
            foreach (var change in _changes)
            {
                var suffix = ".jamestools-" + Guid.NewGuid().ToString("N");
                var backup = change.Path + suffix + ".bak";
                var temp = change.Path + suffix + ".tmp";
                if (!File.ReadAllBytes(change.Path).SequenceEqual(change.Original))
                    throw new IOException("An OBS configuration file changed while it was being prepared; no changes were made.");
                File.Copy(change.Path, backup, false);
                staged.Add((change, backup, temp));
                if (!File.ReadAllBytes(backup).SequenceEqual(change.Original))
                    throw new IOException("An OBS configuration file changed while its backup was being created.");
                WriteNewFile(temp, change.Replacement);
            }
            foreach (var entry in staged)
            {
                if (isObsRunning()) throw new IOException("OBS started while configuration was being updated.");
                VerifyStable();
                File.Replace(entry.Temp, entry.Change.Path, null);
                _applied.Add((entry.Change, entry.Backup));
            }
        }
        catch (Exception error)
        {
            if (!TryRollback(isObsRunning, out var rollbackError))
                throw new IOException($"{error.Message} Automatic rollback could not be completed: {rollbackError}", error);
            throw;
        }
        finally
        {
            foreach (var entry in staged)
                try { if (File.Exists(entry.Temp)) File.Delete(entry.Temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal bool TryRollback(Func<bool> isObsRunning, out string? error)
    {
        var problems = new List<string>();
        for (int i = _applied.Count - 1; i >= 0; i--)
        {
            var (change, backup) = _applied[i];
            string temp = change.Path + ".jamestools-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (isObsRunning()) throw new IOException("OBS is running");
                if (!File.ReadAllBytes(change.Path).SequenceEqual(change.Replacement))
                    throw new IOException("the file has since changed");
                var original = File.ReadAllBytes(backup);
                if (!original.SequenceEqual(change.Original)) throw new IOException("the backup has since changed");
                WriteNewFile(temp, original);
                File.Replace(temp, change.Path, null);
                _applied.RemoveAt(i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{ex.Message}; original backup: {backup}");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        error = problems.Count == 0 ? null : string.Join("; ", problems);
        return problems.Count == 0;
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }
}
