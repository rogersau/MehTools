using System.Windows;
using JamesTools.Core.Configuration;
using JamesTools.Core.Recording;

namespace JamesTools;

public partial class MainWindow
{
    private void ShowGettingStarted_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedIndex = 0;
        GettingStartedCard.Visibility = Visibility.Visible;
        GettingStartedCard.BringIntoView();
    }

    private void DismissGettingStarted_Click(object sender, RoutedEventArgs e)
    {
        if (_appliedSettings.ShowGettingStarted)
        {
            var saved = _appliedSettings.Snapshot();
            saved.ShowGettingStarted = false;
            try { _store.Save(saved); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                StatusNotice.Text = "Couldn't dismiss the welcome screen: " + ex.Message;
                return;
            }
            _appliedSettings = saved;
        }
        _settings.ShowGettingStarted = false;
        GettingStartedCard.Visibility = Visibility.Collapsed;
        AutomationButton.Focus();
    }

    private void GettingStartedRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.RecordingEnabled || _appliedSettings.AutomationEnabled)
        {
            Navigation.SelectedIndex = 2;
            return;
        }

        var game = _settings.Games.Count == 1 ? _settings.Games[0] : null;
        var choice = game is not null && File.Exists(game.ExecutablePath) ? new GameChoice(game.Name, game.ExecutablePath) : null;
        var setup = new RecordingSetupWindow(choice, _settings.ObsExecutablePath, _settings.RecordingDirectory,
            SaveRecordingSetup) { Owner = this };
        if (setup.ShowDialog() == true)
        {
            GettingStartedCard.Visibility = Visibility.Collapsed;
            StatusNotice.Text = "Recording setup saved. Watching is on. Close OBS before playing and verify a short recording.";
        }
        else if (setup.OpenAdvanced) Navigation.SelectedIndex = 2;
    }

    private bool SaveRecordingSetup(RecordingSetupSelection selection, Window owner)
    {
        var game = _settings.Games.FirstOrDefault(game => ExecutablePathRules.EqualsPath(game.ExecutablePath, selection.Game.Path) ||
            game.ExecutableAliases.Any(alias => ExecutablePathRules.EqualsPath(alias, selection.Game.Path)));
        var added = game is null;
        game ??= new GameRule { Name = selection.Game.Name, ExecutablePath = selection.Game.Path };

        var current = _settings.Snapshot();
        var previousRecord = game.Record;
        current.RecordingDirectory = selection.RecordingDirectory;
        var options = EffectiveGameOptionsResolver.Resolve(current, game);
        ObsRecorder.CheckSetupForGame(selection.ObsExecutable, selection.Obs, options.RecordingDirectory,
            selection.Game.Path, options.CaptureMode, options.ReplayBufferSeconds);

        var previous = (_settings.ObsExecutablePath, _settings.ObsConfigRoot, _settings.ObsPortable, _settings.RecordingDirectory,
            _settings.RecordingEnabled, _settings.AutomationEnabled, _settings.ShowGettingStarted);
        _settings.ObsExecutablePath = selection.ObsExecutable;
        _settings.ObsConfigRoot = selection.Obs.ConfigRoot;
        _settings.ObsPortable = selection.Obs.Portable;
        _settings.RecordingDirectory = selection.RecordingDirectory;
        _settings.RecordingEnabled = true;
        _settings.AutomationEnabled = true;
        _settings.ShowGettingStarted = false;
        game.Record = true;
        if (added) _settings.Games.Add(game);

        if (SaveSettings(owner))
        {
            DataContext = null;
            DataContext = _settings;
            GameGrid.SelectedItem = game;
            return true;
        }

        (_settings.ObsExecutablePath, _settings.ObsConfigRoot, _settings.ObsPortable, _settings.RecordingDirectory,
            _settings.RecordingEnabled, _settings.AutomationEnabled, _settings.ShowGettingStarted) = previous;
        if (added) _settings.Games.Remove(game);
        else game.Record = previousRecord;
        return false;
    }

    private void GettingStartedPerformance_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedIndex = 5;
        OverlayButton.BringIntoView();
        OverlayButton.Focus();
    }

    private void GettingStartedColours_Click(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 3;
    private void GettingStartedPower_Click(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 4;
    private void GettingStartedImport_Click(object sender, RoutedEventArgs e) => ImportSettings_Click(sender, e);
}
