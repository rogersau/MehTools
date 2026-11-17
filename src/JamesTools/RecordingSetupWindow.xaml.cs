using System.Diagnostics;
using System.Windows;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;

namespace JamesTools;

internal sealed record RecordingSetupSelection(GameChoice Game, string ObsExecutable, DiscoveredObs Obs, string RecordingDirectory);

internal partial class RecordingSetupWindow : Window
{
    private readonly Func<RecordingSetupSelection, Window, bool> _save;
    private GameChoice? _game;
    private string? _obsExecutable;
    private string _folder;
    private bool _folderChosen;
    public bool OpenAdvanced { get; private set; }

    public RecordingSetupWindow(GameChoice? game, string? obsExecutable, string? folder, Func<RecordingSetupSelection, Window, bool> save)
    {
        InitializeComponent();
        _save = save;
        _folderChosen = IsExistingDirectory(folder);
        _folder = _folderChosen ? folder! : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (game is not null) SetGame(game);
        if (File.Exists(obsExecutable)) SetObs(obsExecutable);
        else ShowFolder();
    }

    private async void DetectObs_Loaded(object sender, RoutedEventArgs e)
    {
        if (_obsExecutable is null) await DetectObsAsync();
    }

    private async void FindObs_Click(object sender, RoutedEventArgs e) => await DetectObsAsync();

    private async Task DetectObsAsync()
    {
        FindObsButton.IsEnabled = false;
        var previous = _obsExecutable;
        SelectedObsText.Text = "Looking for OBS Studio…";
        string? detected;
        try { detected = await Task.Run(ObsDiscovery.FindExecutable); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            detected = null;
        }
        finally { FindObsButton.IsEnabled = true; }
        if (!IsVisible || _obsExecutable != previous) return;
        if (detected is not null) SetObs(detected);
        else if (previous is not null) SelectedObsText.Text = previous;
        else SelectedObsText.Text = "OBS Studio wasn't found.";
    }

    private async void FindGame_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            if (await GamePicker.PickRunningAsync(this, new AppDetectionOptions(true, true, true, true, true, true)) is { } game)
                SetGame(game);
        }
        finally { button.IsEnabled = true; }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (GamePicker.Browse(this) is { } game) SetGame(game);
    }

    private void SetGame(GameChoice game)
    {
        _game = game;
        SelectedGameText.Text = $"{game.Name} ({Path.GetFileName(game.Path)})";
        SelectedGameText.ToolTip = game.Path;
        SetupError.Visibility = Visibility.Collapsed;
    }

    private void BrowseObs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose the OBS Studio executable", Filter = "OBS Studio (obs64.exe;obs32.exe)|obs64.exe;obs32.exe" };
        if (picker.ShowDialog(this) == true) SetObs(picker.FileName);
    }

    private void SetObs(string executable)
    {
        _obsExecutable = executable;
        SelectedObsText.Text = executable;
        SelectedObsText.ToolTip = executable;
        SetupError.Visibility = Visibility.Collapsed;
        if (!_folderChosen)
        {
            var suggested = ObsDiscovery.FromExecutable(executable)?.RecordingDirectory;
            _folder = IsExistingDirectory(suggested) ? suggested! : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
        ShowFolder();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose where to save recordings", InitialDirectory = _folder };
        if (picker.ShowDialog(this) != true) return;
        _folder = picker.FolderName;
        _folderChosen = true;
        ShowFolder();
        SetupError.Visibility = Visibility.Collapsed;
    }

    private static bool IsExistingDirectory(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && Directory.Exists(folder);

    private void ShowFolder()
    {
        var exists = IsExistingDirectory(_folder);
        SelectedFolderText.Text = exists ? _folder : "Choose a folder for your recordings.";
        SelectedFolderText.ToolTip = exists ? _folder : null;
    }

    private void GetObs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://obsproject.com/download") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { ShowError("Open obsproject.com/download in your browser to download OBS Studio."); }
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        OpenAdvanced = true;
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_game is null || !File.Exists(_game.Path))
                throw new InvalidDataException("Choose an existing game executable first.");
            if (!File.Exists(_obsExecutable))
                throw new InvalidDataException("Choose your OBS Studio executable first.");
            var obs = ObsDiscovery.FromExecutable(_obsExecutable)
                ?? throw new InvalidDataException("OBS settings weren't found. Open OBS, finish its initial setup, then close it and try again.");
            if (!IsExistingDirectory(_folder))
                throw new InvalidDataException("Choose an existing folder for your recordings.");
            if (_save(new RecordingSetupSelection(_game, _obsExecutable, obs, _folder), this)) DialogResult = true;
            else ShowError("Setup couldn't be saved. Review the settings and try again.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.Text.Json.JsonException or System.Security.SecurityException)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        SetupError.Text = message;
        SetupError.Visibility = Visibility.Visible;
        SetupError.BringIntoView();
    }
}
