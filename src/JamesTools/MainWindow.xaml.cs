using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace JamesTools;

public partial class MainWindow : Window
{
    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamesTools");
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly AutomationService _automation;
    private readonly Forms.NotifyIcon _tray;
    private bool _exiting;
    private bool _saving;

    public MainWindow()
    {
        InitializeComponent();
        _store = new SettingsStore(_dataDirectory);
        string? initialMessage = null;
        try
        {
            _settings = _store.Load() ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            _settings = new AppSettings();
            initialMessage = "Couldn't load settings: " + ex.Message + " Saving will back up the old settings file.";
        }

        try
        {
            var plans = PowerPlans.List();
            DesktopPlanBox.ItemsSource = plans;
            GamingPlanBox.ItemsSource = plans;
        }
        catch (Exception ex) { initialMessage = "Couldn't read power plans: " + ex.Message; }
        DataContext = _settings;
        UpdateAutomationButton();

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "Meh Tools",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open Meh Tools", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _tray.ContextMenuStrip.Items.Add("Quit (stop recording)", null, (_, _) => Dispatcher.Invoke(() => _ = ExitAsync()));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        _automation = new AutomationService(_settings, _dataDirectory);
        _automation.StatusChanged += status => Dispatcher.BeginInvoke(() => ShowStatus(status));
        _automation.Activity += message => Dispatcher.BeginInvoke(() => AddActivity(message));
        _automation.Start();
        AddActivity(initialMessage ?? "Ready. Add your games and save.");
        if (initialMessage is not null) StatusNotice.Text = initialMessage;
    }

    public void ShowFromTray()
    {
        if (_exiting) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowStatus(AutomationStatus status)
    {
        if (_exiting) return;
        StatusGame.Text = status.Game;
        StatusRecording.Text = status.Recording;
        StatusVibrance.Text = status.Vibrance;
        StatusPower.Text = status.Power;
        SidebarStatus.Text = _settings.AutomationEnabled ? "Watching for games" : "Paused";
        if (!string.IsNullOrWhiteSpace(status.Notice)) StatusNotice.Text = status.Notice;
    }

    private void AddActivity(string message)
    {
        if (_exiting) return;
        ActivityList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}   {message}");
        while (ActivityList.Items.Count > 20) ActivityList.Items.RemoveAt(ActivityList.Items.Count - 1);
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages is null || Navigation.SelectedIndex < 0) return;
        Pages.SelectedIndex = Navigation.SelectedIndex;
        var pages = new (string Title, string Subtitle)[]
        {
            ("Overview", "Your game settings, all in one place."),
            ("Games", "Choose what happens when you play."),
            ("Recording", "Choose OBS and where to save videos."),
            ("Vibrance", "Set your game colours."),
            ("Power", "Choose your power plans."),
            ("Performance", "Make a report about how your PC handles games."),
            ("Settings", "Make the app yours.")
        };
        var selected = pages[Navigation.SelectedIndex];
        PageTitle.Text = selected.Title;
        PageSubtitle.Text = selected.Subtitle;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private bool SaveSettings()
    {
        if (_saving) return false;
        _saving = true;
        try
        {
            GameGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            GameGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!int.TryParse(StopDelayBox.Text, out var stopDelay) || !int.TryParse(PollSecondsBox.Text, out var poll))
                throw new InvalidOperationException("Check interval and recording stop delay must be whole numbers.");
            _settings.RecordingStopDelaySeconds = stopDelay;
            _settings.PollSeconds = poll;
            var errors = SettingsValidation.Validate(_settings);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            var oldStartup = StartupManager.ReadCommand();
            var newStartup = _settings.StartWithWindows ? StartupManager.CurrentCommand() : null;
            var changedStartup = !string.Equals(oldStartup, newStartup, StringComparison.OrdinalIgnoreCase);
            if (changedStartup) StartupManager.WriteCommand(newStartup);
            try { _store.Save(_settings); }
            catch
            {
                if (changedStartup) { try { StartupManager.WriteCommand(oldStartup); } catch { } }
                throw;
            }
            _automation.UpdateSettings(_settings);
            UpdateAutomationButton();
            StatusNotice.Text = "Saved.";
            AddActivity("Settings saved.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _saving = false; }
    }

    private void ToggleAutomation_Click(object sender, RoutedEventArgs e)
    {
        var previous = _settings.AutomationEnabled;
        _settings.AutomationEnabled = !previous;
        if (!SaveSettings()) _settings.AutomationEnabled = previous;
        UpdateAutomationButton();
    }

    private void UpdateAutomationButton()
    {
        AutomationButton.Content = _settings.AutomationEnabled ? "Pause" : "Start watching";
        OverviewHeadline.Text = _settings.AutomationEnabled ? "Watching for games" : "Paused";
        SidebarStatus.Text = _settings.AutomationEnabled ? "Watching for games" : "Paused";
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Choose the game file" };
        if (picker.ShowDialog(this) != true) return;
        AddGame(Path.GetFileNameWithoutExtension(picker.FileName), picker.FileName);
    }

    private async void AddRunningGame_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var choices = await Task.Run(() =>
            {
                var catalog = SteamGameDetector.Discover();
                return ProcessSnapshot.Capture().Where(p => p.FullPath is not null)
                    .Select(p => new { Process = p, Games = SteamGameDetector.FindRunning(catalog, [p]) })
                    .Where(item => item.Games.Count > 0)
                    .Select(item => new RunningGameChoice(item.Games[0].Name, item.Process.FullPath!))
                    .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (choices.Length == 0)
            {
                MessageBox.Show(this, "No running Steam game found. Start it first, or find its game file.",
                    "No running game", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new Window
            {
                Owner = this, Title = "Choose a running game", Width = 760, Height = 400,
                MinWidth = 540, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };
            var layout = new DockPanel { Margin = new Thickness(20) };
            var hint = new TextBlock { Text = "Choose your game from the list.",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(hint, Dock.Top);
            layout.Children.Add(hint);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var add = new System.Windows.Controls.Button { Content = "Add game", IsDefault = true, Style = (Style)FindResource("AccentButton") };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) };
            buttons.Children.Add(add); buttons.Children.Add(cancel); layout.Children.Add(buttons);
            var list = new System.Windows.Controls.ListBox { ItemsSource = choices, SelectedIndex = 0,
                Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black,
                BorderThickness = new Thickness(0), Padding = new Thickness(5) };
            layout.Children.Add(list);
            add.Click += (_, _) => { if (list.SelectedItem is RunningGameChoice) dialog.DialogResult = true; };
            dialog.Content = layout;
            if (dialog.ShowDialog() == true && list.SelectedItem is RunningGameChoice selected)
                AddGame(selected.Name, selected.Path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't find running games", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    private void AddGame(string name, string path)
    {
        if (_settings.Games.Any(g => string.Equals(g.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That game file is already in the list.", "Game already added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _settings.Games.Add(new GameRule { Name = name, ExecutablePath = path });
    }

    private sealed record RunningGameChoice(string Name, string Path)
    {
        public override string ToString() => $"{Name}  —  {System.IO.Path.GetFileName(Path)}    {Path}";
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is GameRule game) _settings.Games.Remove(game);
    }

    private void BrowseObs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "OBS executable (obs64.exe)|obs64.exe|Applications (*.exe)|*.exe", Title = "Choose OBS" };
        if (picker.ShowDialog(this) == true)
        {
            _settings.ObsExecutablePath = picker.FileName;
            var discovered = ObsDiscovery.FromExecutable(picker.FileName);
            if (discovered is not null)
            {
                _settings.ObsConfigRoot = discovered.ConfigRoot;
                _settings.ObsPortable = discovered.Portable;
                if (string.IsNullOrWhiteSpace(_settings.RecordingDirectory) &&
                    !string.IsNullOrWhiteSpace(discovered.RecordingDirectory))
                    _settings.RecordingDirectory = discovered.RecordingDirectory;
            }
            DataContext = null; DataContext = _settings;
        }
    }

    private void BrowseObsConfig_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the OBS settings folder" };
        if (picker.ShowDialog(this) == true) { _settings.ObsConfigRoot = picker.FolderName; DataContext = null; DataContext = _settings; }
    }

    private void BrowseRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a recording folder" };
        if (picker.ShowDialog(this) == true) { _settings.RecordingDirectory = picker.FolderName; DataContext = null; DataContext = _settings; }
    }

    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_settings.RecordingDirectory)) throw new DirectoryNotFoundException("Choose an existing recording folder first.");
            Process.Start(new ProcessStartInfo(_settings.RecordingDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open recordings", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenObs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var running = Process.GetProcessesByName("obs64");
            try { if (running.Length > 0) throw new InvalidOperationException("OBS is already running. Close it when finished so automatic recording can start it later."); }
            finally { foreach (var process in running) process.Dispose(); }
            if (!File.Exists(_settings.ObsExecutablePath)) throw new FileNotFoundException("Choose the OBS app first.");
            var start = new ProcessStartInfo(_settings.ObsExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_settings.ObsExecutablePath)!
            };
            if (_settings.ObsPortable) start.ArgumentList.Add("--portable");
            Process.Start(start);
            StatusNotice.Text = "Close OBS when finished so automatic recording can start it later.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open OBS", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (_settings.MinimizeToTray) Hide();
        else _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        if (!await PreparePerformanceForExitAsync()) return;
        _exiting = true;
        Hide();
        try { await _automation.DisposeAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Meh Tools", MessageBoxButton.OK, MessageBoxImage.Warning); }
        _tray.Visible = false;
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
