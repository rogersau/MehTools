using System.Text;
using System.Windows;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using Microsoft.Win32;

namespace JamesTools;

public partial class MainWindow
{
    private int _detectionRefreshVersion;

    private void InvalidateDetectionPreview()
    {
        _detectionRefreshVersion++;
        if (DetectionDiagnosticsText is not null)
            DetectionDiagnosticsText.Text = "Choose Check detection to refresh the selected game's process snapshot.";
    }

    private void DetectionSettingsSource_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => InvalidateDetectionPreview();
    private void AddExecutableAlias_Click(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is not GameRule game) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose an exact executable alias", Filter = "Applications (*.exe)|*.exe" };
        if (picker.ShowDialog(this) != true) return;
        var path = picker.FileName.Trim();
        if (!ExecutablePathRules.IsValidAbsoluteExecutable(path))
        {
            System.Windows.MessageBox.Show(this, "Choose a full .exe path.", "Invalid alias", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ExecutablePathRules.EqualsPath(path, game.ExecutablePath) ||
            game.ExecutableAliases.Any(alias => ExecutablePathRules.EqualsPath(alias, path)))
        {
            System.Windows.MessageBox.Show(this, "That executable is already the primary path or an alias.", "Duplicate alias", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        game.ExecutableAliases.Add(path);
    }

    private void RemoveExecutableAlias_Click(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is GameRule game && ExecutableAliasesList.SelectedItem is string alias)
            game.ExecutableAliases.Remove(alias);
    }

    private async void RefreshDetectionDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting) return;
        var version = ++_detectionRefreshVersion;
        if (GameGrid.SelectedItem is not GameRule selected)
        {
            DetectionDiagnosticsText.Text = "Select a game first.";
            return;
        }

        var useSaved = DetectionSettingsSource.SelectedIndex == 1;
        var settings = (useSaved ? _appliedSettings : _settings).Snapshot();
        var game = settings.Games.FirstOrDefault(candidate =>
            string.Equals(candidate.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            DetectionDiagnosticsText.Text = "This game isn't present in the selected settings snapshot.";
            return;
        }

        DetectionDiagnosticsText.Text = "Capturing processes…";
        try
        {
            // Process enumeration can touch many processes and is deliberately kept off the UI thread.
            var processes = await Task.Run(ProcessSnapshot.Capture);
            if (_exiting || version != _detectionRefreshVersion || !ReferenceEquals(GameGrid.SelectedItem, selected) ||
                (DetectionSettingsSource.SelectedIndex == 1) != useSaved) return;
            var capturedAt = DateTime.Now;
            var evaluation = AutomationPolicy.EvaluateRule(game, processes, settings.Games);
            var effective = EffectiveGameOptionsResolver.Resolve(settings, game);
            var output = new StringBuilder();
            output.AppendLine(useSaved ? "Viewing saved active settings." : "Viewing current draft settings (recommended); changes take effect after Save.");
            output.AppendLine($"Process snapshot: {capturedAt:yyyy-MM-dd HH:mm:ss zzz}. Read-only; no actions were run.");
            output.AppendLine($"Rule: {game.Name} · match mode: {game.MatchMode} · primary: {game.ExecutablePath}");
            output.AppendLine("Primary executable file: " + (File.Exists(game.ExecutablePath) ? "available on disk." : "missing or inaccessible on disk."));
            foreach (var alias in game.ExecutableAliases)
                output.AppendLine($"Alias file {alias}: {(File.Exists(alias) ? "available on disk." : "missing or inaccessible on disk.")}");
            output.AppendLine(evaluation.Summary);
            if (evaluation.Match is not null)
                output.AppendLine($"Selected process: {evaluation.Match.Process.Name} (PID {evaluation.Match.Process.Id}) — {evaluation.Match.Process.FullPath ?? "path unknown / inaccessible"}");
            output.AppendLine($"Captured {processes.Count} processes; relevant matching or likely candidate processes:");
            if (evaluation.Processes.Count == 0) output.AppendLine("  No relevant process was observed in this snapshot.");
            foreach (var item in evaluation.Processes)
            {
                var process = item.Process;
                output.AppendLine($"  {process.Name} (PID {process.Id}) — {process.FullPath ?? "path unknown / inaccessible"}");
                output.AppendLine($"    {item.Reason}");
            }
            if (evaluation.CompetingGames.Count > 0)
                output.AppendLine("Competing configured rules: " + string.Join(", ", evaluation.CompetingGames) + ". Rule order follows the games list; process selection prefers primary path, alias path, then mode match, with lowest PID as a stable tie-break.");

            var matched = evaluation.Match is not null;
            var watching = settings.AutomationEnabled;
            var recording = watching && settings.RecordingEnabled && effective.Record && matched;
            var captureMode = effective.CaptureMode;
            var recordingMode = recording && captureMode is CaptureMode.Recording or CaptureMode.Both;
            var replayMode = recording && captureMode is CaptureMode.Replay or CaptureMode.Both;
            var vibrance = watching && settings.VibranceEnabled && effective.UseVibrance && matched;
            var power = watching && settings.PowerEnabled && matched;
            var closeApps = watching && settings.CloseAppsOnGameLaunch && settings.AppsToCloseOnGameLaunch.Count > 0 && matched;
            output.AppendLine("Action eligibility from these settings (eligibility only; nothing is executed):");
            output.AppendLine("  Other running games can take priority in settings order; an existing managed capture keeps its matching process. This view does not inspect current automation ownership.");
            output.AppendLine($"  Watching: {(watching ? "enabled in this snapshot" : "disabled")}.");
            output.AppendLine($"  Recording: {(recordingMode ? "eligible" : "not eligible")}; capture mode {captureMode}; effective Record permission {Bool(effective.Record)}; Recording switch {Bool(settings.RecordingEnabled)}.");
            output.AppendLine($"  Replay buffer: {(replayMode ? "eligible" : "not eligible")}; effective Record permission {Bool(effective.Record)}; Recording switch {Bool(settings.RecordingEnabled)}.");
            output.AppendLine($"  Colours / vibrance: {(vibrance ? "eligible" : "not eligible")}; Vibrance switch {Bool(settings.VibranceEnabled)}; game permission {Bool(effective.UseVibrance)}.");
            output.AppendLine($"    Focus requirement: {(settings.VibranceOnlyWhenFocused ? "required at runtime; foreground state is not checked" : "not required by current settings")}.");
            if (vibrance && settings.VibranceOnlyWhenFocused) output.AppendLine("    Focus is a runtime condition; this preview does not inspect the foreground window.");
            if (vibrance && settings.DisableVibranceWhenHdr) output.AppendLine("    HDR is a runtime condition; this preview does not inspect HDR state.");
            output.AppendLine($"  Power plan: {(power ? "eligible" : "not eligible")}; Power switch {Bool(settings.PowerEnabled)}.");
            output.AppendLine($"  Optional close-app behavior: {(closeApps ? "configured; only runs on a newly observed game-launch transition" : "not eligible")}; this preview cannot determine that transition.");
            if (recording) output.AppendLine("  OBS process/window state and actual recording output are runtime conditions; neither is checked here.");
            if (recording && ExecutablePathRules.Normalize(evaluation.Match?.Process.FullPath) is null)
                output.AppendLine("  Capture is blocked for the selected process because its executable path is inaccessible.");
            DetectionDiagnosticsText.Text = output.ToString();
        }
        catch (Exception ex)
        {
            if (!_exiting && version == _detectionRefreshVersion && ReferenceEquals(GameGrid.SelectedItem, selected))
                DetectionDiagnosticsText.Text = "Couldn't check detection: " + ex.Message;
        }
    }

    private static string Bool(bool value) => value ? "enabled" : "disabled";
}
