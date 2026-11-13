using System.Text.Json.Nodes;

namespace JamesTools.Core.Recording;

public sealed record ObsValidationResult(bool Ok, IReadOnlyList<string> Issues, IReadOnlyList<string> Info);

public static class ObsValidation
{
    public static ObsValidationResult Validate(string executablePath, string configRoot, string recordingDirectory, bool portable)
    {
        var issues = new List<string>();
        var info = new List<string>();
        try
        {
            if (!File.Exists(executablePath)) issues.Add("OBS program file not found.");
            if (!Directory.Exists(configRoot)) issues.Add("OBS settings folder not found.");
            if (!string.IsNullOrWhiteSpace(recordingDirectory) && !Directory.Exists(recordingDirectory)) issues.Add("Recording folder not found.");
            if (issues.Count > 0) return new(false, issues, info);

            var discovered = ObsDiscovery.FromExecutable(executablePath);
            if (discovered is null) issues.Add("Could not detect OBS configuration from the selected executable.");
            else
            {
                info.Add($"Detected config: {discovered.ConfigRoot} ({(discovered.Portable ? "portable" : "standard")}).");
                if (!string.Equals(Path.GetFullPath(discovered.ConfigRoot), Path.GetFullPath(configRoot), StringComparison.OrdinalIgnoreCase))
                    issues.Add("Selected OBS settings folder does not match the folder detected for this OBS executable.");
                if (discovered.Portable != portable) issues.Add($"Portable mode setting does not match detected OBS installation ({(discovered.Portable ? "portable" : "standard")}).");
            }

            if (!Directory.Exists(configRoot)) return new(false, issues, info);
            var userIniPath = Path.Combine(configRoot, "user.ini");
            if (!File.Exists(userIniPath)) { issues.Add("OBS user.ini was not found in the selected configuration folder."); return new(false, issues, info); }
            var user = IniDocument.Read(userIniPath);
            var profile = user.GetValue("Basic", "ProfileDir");
            var sceneFile = user.GetValue("Basic", "SceneCollectionFile");
            if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(sceneFile)) issues.Add("OBS user.ini does not identify a selected profile/scene collection.");
            if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(sceneFile)) return new(false, issues, info);
            if (!sceneFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) sceneFile += ".json";
            var profilePath = Path.Combine(configRoot, "basic", "profiles", profile, "basic.ini");
            var scenePath = Path.Combine(configRoot, "basic", "scenes", sceneFile);
            if (!File.Exists(profilePath)) issues.Add("Selected OBS profile basic.ini could not be found.");
            if (!File.Exists(scenePath)) issues.Add("Selected OBS scene collection could not be found.");
            if (!File.Exists(profilePath) || !File.Exists(scenePath)) return new(false, issues, info);

            var profileIni = IniDocument.Read(profilePath);
            var mode = profileIni.GetValue("Output", "Mode");
            if (string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase))
            {
                var recType = profileIni.GetValue("AdvOut", "RecType");
                if (!string.IsNullOrEmpty(recType) && !string.Equals(recType, "Standard", StringComparison.OrdinalIgnoreCase))
                    issues.Add("OBS profile uses a custom/non-standard recording output (e.g. FFmpeg/custom). Use standard recording.");
                else info.Add("OBS output mode: Advanced (standard recording).");
            }
            else info.Add("OBS output mode: Simple/standard.");

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(scenePath));
                var sceneName = node?["current_program_scene"]?.ToString() ?? node?["current_scene"]?.ToString();
                info.Add("Active scene: " + (sceneName ?? "(unknown)"));
                var hasGameCapture = scenePath.Length > 0 && File.ReadAllText(scenePath).Contains("game_capture", StringComparison.OrdinalIgnoreCase);
                if (!hasGameCapture) issues.Add("No Game Capture source found in the scene collection (a visible Game Capture source is required in the active scene).");
                else info.Add("Scene collection contains a Game Capture source (visibility/active-scene targeting is verified when starting).");
            }
            catch (Exception ex) { issues.Add("Could not read scene collection: " + ex.Message); }
        }
        catch (Exception ex) { issues.Add("Validation error: " + ex.Message); }
        return new(issues.Count == 0, issues, info);
    }
}
