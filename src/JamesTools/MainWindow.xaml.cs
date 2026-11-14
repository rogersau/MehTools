using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using JamesTools.Core.Sessions;
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
    private AppSettings _appliedSettings;
    private readonly AutomationService _automation;
    private SessionLibraryStore? _sessionStore;
    private SessionTracker? _sessionTracker;
    private readonly Forms.NotifyIcon _tray;
    private bool _exiting;
    private bool _saving;
    private bool _minimizeTipShown;
    private readonly HashSet<string> _activeGamePaths = new(StringComparer.OrdinalIgnoreCase);
    public System.Collections.ObjectModel.ObservableCollection<PowerPlanOption> GamePowerPlanOptions { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<GameProfileAssignmentOption> GameProfileAssignmentOptions { get; } = [];
    public IReadOnlyList<GameValueOption<CaptureMode>> CaptureModeOptions { get; } =
        [new(null, "Inherit (Recording default)"), new(CaptureMode.Recording, "Recording"), new(CaptureMode.Replay, "Replay"), new(CaptureMode.Both, "Both")];
    public IReadOnlyList<GameValueOption<OverlayPreset>> OverlayPresetOptions { get; } =
        [new(null, "Inherit"), new(OverlayPreset.Minimal, "Minimal (FPS)"), new(OverlayPreset.Fps, "FPS + frame delay"), new(OverlayPreset.Full, "Full")];
    public IReadOnlyList<GameValueOption<VibranceTargetMode>> VibranceTargetOptions { get; } =
        [new(VibranceTargetMode.AllDisplays, "All displays"), new(VibranceTargetMode.SelectedDisplay, "Selected display"), new(VibranceTargetMode.GameWindowDisplay, "Game window display")];
    public System.Collections.ObjectModel.ObservableCollection<VibranceDisplayOption> VibranceDisplayOptions { get; } = [];

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
            initialMessage = "Couldn't load your settings: " + ex.Message + " Saving will make a backup of the old file.";
        }

        try
        {
            _sessionStore = new SessionLibraryStore(_dataDirectory);
            _sessionTracker = new SessionTracker(_sessionStore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        {
            var message = "Couldn't load session history: " + ex.Message;
            initialMessage = initialMessage is null ? message : initialMessage + " " + message;
        }

        try
        {
            var plans = PowerPlans.List();
            DesktopPlanBox.ItemsSource = plans;
            GamingPlanBox.ItemsSource = plans;
            GamePowerPlanOptions.Clear();
            GamePowerPlanOptions.Add(new PowerPlanOption(null, "Default"));
            foreach (var p in plans) GamePowerPlanOptions.Add(new PowerPlanOption(p.Id, p.Name));
        }
        catch (Exception ex)
        {
            initialMessage = "Couldn't load power plans: " + ex.Message;
            GamePowerPlanOptions.Clear();
            GamePowerPlanOptions.Add(new PowerPlanOption(null, "Default"));
        }
        DataContext = _settings;
        RefreshVibranceDisplays();
        RefreshGameProfileOptions();
        InitializeRecordingPanel();
        InitializeSessionsPanel();
        if (MatchModeColumn is not null) MatchModeColumn.ItemsSource = new[] { "path", "name", "folder" };
        SyncAppsToCloseBox();
        UpdateAutomationButton();

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "Meh Tools",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open Meh Tools", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _tray.ContextMenuStrip.Items.Add(_settings.AutomationEnabled ? "Stop watching" : "Start watching", null, (_, _) => Dispatcher.Invoke(() => ToggleAutomation_Click(this, new RoutedEventArgs())));
        _tray.ContextMenuStrip.Items.Add("Show/Hide monitor", null, (_, _) => Dispatcher.Invoke(ToggleOverlay));
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(() => _ = ExitAsync()));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        _appliedSettings = _settings.Snapshot();
        ApplyRecordingHealthSettings();
        _automation = new AutomationService(_appliedSettings, _dataDirectory);
        _automation.RecordingLifecycleChanged += AutomationRecordingLifecycleChanged;
        _automation.StatusChanged += status => Dispatcher.BeginInvoke(() => ShowStatus(status));
        _automation.Activity += message => Dispatcher.BeginInvoke(() => AddActivity(message));
        _automation.Notify += notification => Dispatcher.BeginInvoke(() => ShowTrayNotification(notification));
        _automation.Start();
        RefreshSessionList();
        SourceInitialized += (_, _) => { ApplyOverlayHotkey(); ApplySaveClipHotkey(); ApplyStutterMarkerHotkey(); ApplyBookmarkHotkey(); ApplyRollingHotkey(); };
        AddActivity(initialMessage ?? "Ready. Add your games, then click Save.");
        if (initialMessage is not null) StatusNotice.Text = initialMessage;
    }

    public void ShowFromTray()
    {
        if (_exiting) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }


    private void GameGrid_LoadingRow(object? sender, System.Windows.Controls.DataGridRowEventArgs e) => ApplyGameRowState(e.Row);

    private void ApplyGameRowState(DataGridRow row)
    {
        if (row.Item is GameRule game && _activeGamePaths.Contains(game.ExecutablePath))
        {
            row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 84, 74));
            row.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
            row.FontWeight = System.Windows.FontWeights.SemiBold;
        }
        else
        {
            // Rows are recycled, so remove the highlight instead of leaving it on a different game.
            row.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            row.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            row.ClearValue(System.Windows.Controls.Control.FontWeightProperty);
        }
    }

    private void ShowStatus(AutomationStatus status)
    {
        if (_exiting) return;
        UpdateDetectedSessions(status);
        UpdateRolling(status);
        StatusGame.Text = status.Game;
        StatusRecording.Text = status.Recording;
        StatusVibrance.Text = status.Vibrance;
        StatusPower.Text = status.Power;
        OverviewHeadline.Text = status.Headline;
        SidebarStatus.Text = status.Headline;
        _activeGamePaths.Clear();
        if (status.ActiveGamePaths is not null) foreach (var p in status.ActiveGamePaths) _activeGamePaths.Add(p);
        // Update visible rows in place. Refreshing the collection interrupts cell editing.
        if (GameGrid is not null)
        {
            for (var i = 0; i < GameGrid.Items.Count; i++)
                if (GameGrid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row)
                    ApplyGameRowState(row);
        }
        UpdateTrayMenuText();
        if (!string.IsNullOrWhiteSpace(status.Notice)) StatusNotice.Text = status.Notice;
    }

    private void UpdateTrayMenuText()
    {
        try
        {
            if (_tray?.ContextMenuStrip?.Items.Count >= 2)
                _tray.ContextMenuStrip.Items[1].Text = _settings.AutomationEnabled ? "Stop watching" : "Start watching";
        }
        catch { }
    }

    private void AddActivity(string message)
    {
        if (_exiting) return;
        ActivityList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}   {message}");
        while (ActivityList.Items.Count > 20) ActivityList.Items.RemoveAt(ActivityList.Items.Count - 1);
    }

    private void ShowTrayNotification(AutomationNotification notification)
    {
        if (_exiting || !_settings.ShowNotifications) return;
        var icon = notification.Warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        _tray.ShowBalloonTip(3000, notification.Title, notification.Message, icon);
        if (notification.Warning) StatusNotice.Text = notification.Message;
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages is null || Navigation.SelectedIndex < 0) return;
        Pages.SelectedIndex = Navigation.SelectedIndex;
        var pages = new (string Title, string Subtitle)[]
        {
            ("Overview", "See what's happening and turn watching on or off."),
            ("Games", "Choose what should happen when you play."),
            ("Recording", "Set up OBS capture, replay clips, and saved videos."),
            ("Colours", "Choose colour strength for desktop and games."),
            ("Power", "Choose which power plans to use."),
            ("Performance", "Show live numbers or save a report."),
            ("Settings", "Change how the app works."),
            ("Sessions", "Search detected play sessions, recordings, clips, and performance reports.")
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
            if (!TryApplyPendingSettings(out var problem)) throw new InvalidOperationException(problem);

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
            _appliedSettings = _settings.Snapshot();
            ApplyRecordingHealthSettings();
            RefreshRecordingStorageTargets();
            _ = RefreshStorageVisibilityAsync();
            UpdateAutomationButton();
            StatusNotice.Text = "Saved.";
            ApplyOverlayHotkey();
            ApplySaveClipHotkey();
            ApplyStutterMarkerHotkey();
            ApplyBookmarkHotkey(); ApplyRollingHotkey();
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

    private void GameProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitGameGridEdits(out var problem))
        {
            MessageBox.Show(this, problem, "Check game settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var editor = new GameProfilesEditorWindow(_settings.Profiles, GamePowerPlanOptions, _settings.Games) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _settings.Profiles.Clear();
        foreach (var profile in editor.Profiles) _settings.Profiles.Add(profile.Copy());
        RefreshGameProfileOptions();
        var selected = GameGrid.SelectedItem as GameRule;
        DataContext = null; DataContext = _settings;
        if (selected is not null) GameGrid.SelectedItem = selected;
        RefreshEffectiveOptionsSummary();
    }

    private void RefreshGameProfileOptions()
    {
        GameProfileAssignmentOptions.Clear();
        GameProfileAssignmentOptions.Add(new(null, "Inherit global defaults"));
        foreach (var profile in _settings.Profiles)
            GameProfileAssignmentOptions.Add(new(profile.Id, profile.Name));
    }

    private void GameOverrideChanged(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(RefreshEffectiveOptionsSummary));

    private void GameGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshEffectiveOptionsSummary();
        InvalidateDetectionPreview();
    }

    private void RefreshEffectiveOptionsSummary()
    {
        if (EffectiveOptionsSummary is null || GameGrid?.SelectedItem is not GameRule game)
        {
            if (EffectiveOptionsSummary is not null) EffectiveOptionsSummary.Text = "Select a game to preview resolved values.";
            return;
        }
        var options = EffectiveGameOptionsResolver.Resolve(_settings, game);
        var powerName = GamePowerPlanOptions.FirstOrDefault(option => option.Id == options.PowerPlanId)?.Name ?? options.PowerPlanId.ToString();
        EffectiveOptionsSummary.Text = $"{options.CaptureMode}; folder: {options.RecordingDirectory}; replay: {options.ReplayBufferSeconds}s; stop delay: {options.RecordingStopDelaySeconds}s; vibrance: {options.VibrancePercent}%; power: {powerName}; overlay: CPU {OnOff(options.OverlayShowCpu)}, GPU {OnOff(options.OverlayShowGpu)}, FPS {OnOff(options.OverlayShowFps)}, frame delay {OnOff(options.OverlayShowLatency)}.\nRecord permission: {OnOff(options.Record)}; colour permission: {OnOff(options.UseVibrance)}.";
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private bool TryApplyPendingSettings(out string problem, bool validateSetup = true)
    {
        problem = "";
        if (!TryCommitGameGridEdits(out problem)) return false;
        if (FindInvalidGameEditor(this) is System.Windows.Controls.TextBox invalidEditor)
        {
            Navigation.SelectedIndex = 1;
            for (var parent = LogicalTreeHelper.GetParent(invalidEditor); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
                if (parent is Expander expander) expander.IsExpanded = true;
            invalidEditor.BringIntoView();
            invalidEditor.Focus();
            invalidEditor.SelectAll();
            problem = "Check the highlighted game value. Enter a whole number, or leave it blank to use the default.";
            return false;
        }
        if (!TryReadWholeNumber(StopDelayBox, "the wait time before stopping", 0, 120, out var stopDelay, out problem)) return false;
        if (!TryReadWholeNumber(ReplayBufferSecondsBox, "the replay buffer length", 15, 600, out var replayBufferSeconds, out problem)) return false;
        if (!TryReadWholeNumber(RecordingHealthStallSecondsBox, "the recording health stall threshold", 10, 300, out var healthStallSeconds, out problem)) return false;
        if (!TryReadWholeNumber(RollingBeforeSecondsBox, "the pre-trigger capture interval", 10, 300, out var rollingBefore, out problem)) return false;
        if (!TryReadWholeNumber(RollingAfterSecondsBox, "the post-trigger capture interval", 0, 60, out var rollingAfter, out problem)) return false;
        if (!TryReadWholeNumber(PollSecondsBox, "the check interval", 2, 30, out var poll, out problem)) return false;
        _settings.RecordingStopDelaySeconds = stopDelay;
        _settings.ReplayBufferSeconds = replayBufferSeconds;
        _settings.RecordingHealthStallSeconds = healthStallSeconds;
        _settings.RollingBeforeSeconds = rollingBefore;
        _settings.RollingAfterSeconds = rollingAfter;
        _settings.PollSeconds = poll;
        SyncAppsToCloseFromBox();
        try { JamesTools.Core.Diagnostics.NetworkProbeOptions.Create(_settings.NetworkDiagnosticsEnabled, _settings.NetworkTargets); }
        catch (ArgumentException ex) { problem = ex.Message; return false; }
        // A backup is useful even while machine-specific setup is incomplete.
        if (!validateSetup) return true;
        var errors = SettingsValidation.Validate(_settings);
        if (errors.Count > 0)
        {
            problem = string.Join(Environment.NewLine, errors);
            return false;
        }
        return true;
    }

    private static System.Windows.Controls.TextBox? FindInvalidGameEditor(DependencyObject parent)
    {
        if (parent is System.Windows.Controls.TextBox { DataContext: GameRule } editor && Validation.GetHasError(editor))
            return editor;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            if (FindInvalidGameEditor(child) is { } invalid) return invalid;
        return null;
    }

    private bool TryCommitGameGridEdits(out string problem)
    {
        problem = "";
        if (GameGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            GameGrid.CommitEdit(DataGridEditingUnit.Row, true))
            return true;
        FocusGameGridEditor();
        problem = "Check the highlighted game value. Enter a whole number, or leave the cell blank to use the default.";
        return false;
    }

    private bool TryReadWholeNumber(System.Windows.Controls.TextBox box, string label, int min, int max, out int value, out string problem)
    {
        problem = "";
        if (!int.TryParse(box.Text?.Trim(), out value) || value < min || value > max)
        {
            value = 0;
            Navigation.SelectedIndex = ReferenceEquals(box, StopDelayBox) ? 2 : 6;
            box.BringIntoView();
            box.Focus();
            box.SelectAll();
            problem = $"Enter a whole number from {min} to {max} for {label}.";
            return false;
        }
        return true;
    }

    private void FocusGameGridEditor()
    {
        if (!GameGrid.CurrentCell.IsValid)
        {
            GameGrid.Focus();
            return;
        }
        GameGrid.ScrollIntoView(GameGrid.CurrentCell.Item, GameGrid.CurrentCell.Column);
        GameGrid.UpdateLayout();
        if (GameGrid.ItemContainerGenerator.ContainerFromItem(GameGrid.CurrentCell.Item) is not DataGridRow row) return;
        var cell = FindCell(row, GameGrid.CurrentCell.Column);
        if (cell is null)
        {
            GameGrid.Focus();
            return;
        }
        cell.Focus();
        if (!cell.IsEditing) GameGrid.BeginEdit();
        GameGrid.UpdateLayout();
        if (FindVisualChild<System.Windows.Controls.TextBox>(cell) is System.Windows.Controls.TextBox editor)
        {
            editor.Focus();
            editor.SelectAll();
        }
    }

    private static System.Windows.Controls.DataGridCell? FindCell(DependencyObject parent, DataGridColumn column)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.DataGridCell cell && cell.Column == column) return cell;
            var nested = FindCell(child, column);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void SyncAppsToCloseBox()
    {
        if (AppsToCloseBox is null) return;
        AppsToCloseBox.Text = string.Join(Environment.NewLine, _settings.AppsToCloseOnGameLaunch.Where(a => !string.IsNullOrWhiteSpace(a)));
    }

    private void SyncAppsToCloseFromBox()
    {
        if (AppsToCloseBox is null) return;
        var items = AppsToCloseBox.Text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _settings.AppsToCloseOnGameLaunch.Clear();
        foreach (var item in items) _settings.AppsToCloseOnGameLaunch.Add(item);
    }

    private void ToggleAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (_appliedSettings.AutomationEnabled)
        {
            // Stopping must work even if an unrelated unsaved field is invalid.
            var paused = _appliedSettings.Snapshot();
            paused.AutomationEnabled = false;
            _automation.UpdateSettings(paused);
            _appliedSettings = paused;
            InvalidateRolling();
            _settings.AutomationEnabled = false;
            UpdateAutomationButton();
            StatusNotice.Text = "Watching stopped. Click Save to keep any other changes.";
            AddActivity("Watching stopped.");
            try { _store.Save(paused); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                StatusNotice.Text = "Watching stopped for this session. Couldn't save the watching state: " + ex.Message;
            }
            return;
        }
        var previous = _settings.AutomationEnabled;
        _settings.AutomationEnabled = !previous;
        if (!SaveSettings()) _settings.AutomationEnabled = previous;
        UpdateAutomationButton();
    }

    private void UpdateAutomationButton()
    {
        AutomationButton.Content = _settings.AutomationEnabled ? "Stop watching" : "Start watching";
        OverviewHeadline.Text = _settings.AutomationEnabled ? "Watching for games" : "Off";
        SidebarStatus.Text = _settings.AutomationEnabled ? "Watching for games" : "Off";
        UpdateTrayMenuText();
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Choose a game file" };
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
                var options = new AppDetectionOptions(true, _settings.DetectEpicGames, _settings.DetectGogGames, _settings.DetectEaGames, _settings.DetectUbisoftGames, _settings.DetectXboxGames);
                var catalog = LibraryGameDetector.Discover(options);
                var processes = ProcessSnapshot.Capture().Where(p => p.FullPath is not null).ToArray();
                var running = LibraryGameDetector.FindRunning(catalog, processes);
                // Prefer the actual running process path within matched roots.
                var list = new List<RunningGameChoice>();
                foreach (var game in running)
                {
                    var proc = processes.FirstOrDefault(p => p.FullPath is not null && p.FullPath.StartsWith(game.RootDirectory, StringComparison.OrdinalIgnoreCase));
                    if (proc?.FullPath is not null) list.Add(new RunningGameChoice(game.Name + " (" + game.Source + ")", proc.FullPath));
                }
                return list.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (choices.Length == 0)
            {
                MessageBox.Show(this, "No open detected game found. Open the game first, enable more launchers in Power settings, or use Browse for game.",
                    "No open game found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new Window
            {
                Owner = this, Title = "Choose an open game", Width = 760, Height = 400,
                MinWidth = 540, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };
            WindowTheme.Apply(dialog);
            var layout = new DockPanel { Margin = new Thickness(20) };
            var hint = new TextBlock { Text = "Pick the game you want to add.",
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
                BorderThickness = new Thickness(0), Padding = new Thickness(5) };
            layout.Children.Add(list);
            add.Click += (_, _) => { if (list.SelectedItem is RunningGameChoice) dialog.DialogResult = true; };
            dialog.Content = layout;
            if (dialog.ShowDialog() == true && list.SelectedItem is RunningGameChoice selected)
                AddGame(selected.Name, selected.Path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't find open games", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    private void AddGame(string name, string path)
    {
        if (_settings.Games.Any(g => string.Equals(g.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That game is already in the list.", "Already added", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "OBS program (obs64.exe)|obs64.exe|Applications (*.exe)|*.exe", Title = "Choose OBS" };
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
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to save videos" };
        if (picker.ShowDialog(this) == true) { _settings.RecordingDirectory = picker.FolderName; DataContext = null; DataContext = _settings; }
    }


    private void RefreshVibranceDisplays_Click(object sender, RoutedEventArgs e) => RefreshVibranceDisplays();

    private void RefreshVibranceDisplays()
    {
        VibranceDisplayOptions.Clear();
        try
        {
            using var nv = new NvidiaVibrance();
            var known = nv.Read().Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
                .Select(d => d.DeviceName!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var windows = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, s => s, StringComparer.OrdinalIgnoreCase);
            foreach (var identity in known)
            {
                var windowsName = windows.TryGetValue(identity, out var screen) ? screen.DeviceName : identity;
                var suffix = windowsName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) ? windowsName[@"\\.\DISPLAY".Length..] : "";
                var label = int.TryParse(suffix, out var number) ? $"Windows display {number} ({windowsName})" : $"Windows display ({windowsName})";
                VibranceDisplayOptions.Add(new(identity, $"{label} — NVIDIA"));
            }
        }
        catch { }
        var saved = _settings.VibranceSelectedDisplay;
        if (!string.IsNullOrWhiteSpace(saved) && !VibranceDisplayOptions.Any(d => string.Equals(d.DeviceName, saved, StringComparison.OrdinalIgnoreCase)))
            VibranceDisplayOptions.Add(new(saved, $"Saved display unavailable — {saved}"));
        if (VibranceDisplayOptions.Count == 0)
            VibranceDisplayOptions.Add(new("", "No mapped NVIDIA displays found"));
        if (VibranceDisplayBox is not null)
            VibranceDisplayBox.SelectedValue = _settings.VibranceSelectedDisplay;
    }

    private void RefreshHealth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = new List<string>();
            var obs = ObsValidation.Validate(_settings.ObsExecutablePath, _settings.ObsConfigRoot, RecordingFolderForHealth(), _settings.ObsPortable);
            lines.Add("OBS: " + (obs.Ok ? "OK" : "Needs attention") + (obs.Issues.Count > 0 ? " - " + obs.Issues[0] : ""));
            try { using var nv = new NvidiaVibrance(); var d = nv.Read(); lines.Add($"Colours (NVIDIA): OK ({d.Count} display(s))"); } catch (Exception ex) { lines.Add("Colours (NVIDIA): " + ex.Message); }
            try { var plans = PowerPlans.List(); lines.Add($"Power plans: OK ({plans.Count} plan(s))"); } catch (Exception ex) { lines.Add("Power plans: " + ex.Message); }
            var pmResolved = PresentMonLocator.Resolve(_settings.PresentMonPath);
            var pm = pmResolved is not null ? (string.IsNullOrWhiteSpace(_settings.PresentMonPath) ? "bundled/found" : "found") : "not found (optional; bundled copy missing)";
            lines.Add("PresentMon: " + pm + (pmResolved is not null ? $" ({Path.GetFileName(pmResolved)})" : ""));
            try { lines.Add("HDR active now: " + (HdrStatus.IsHdrActive() ? "yes" : "no/unknown")); } catch { lines.Add("HDR active now: unknown"); }
            HealthSummary.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { HealthSummary.Text = "Couldn't check health: " + ex.Message; }
    }
    private string RecordingFolderForHealth()
        => _settings.Games.Where(game => game.Record)
            .Select(game => EffectiveGameOptionsResolver.Resolve(_settings, game).RecordingDirectory)
            .FirstOrDefault(Directory.Exists) ?? _settings.RecordingDirectory;
    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_settings.RecordingDirectory)) throw new DirectoryNotFoundException("Choose a folder for videos first.");
            Process.Start(new ProcessStartInfo(_settings.RecordingDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open folder", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenObs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var running = Process.GetProcessesByName("obs64");
            try { if (running.Length > 0) throw new InvalidOperationException("OBS is already open. Close it so Meh Tools can start it when a game begins."); }
            finally { foreach (var process in running) process.Dispose(); }
            if (!File.Exists(_settings.ObsExecutablePath)) throw new FileNotFoundException("Choose the OBS program first.");
            var start = new ProcessStartInfo(_settings.ObsExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_settings.ObsExecutablePath)!
            };
            if (_settings.ObsPortable) start.ArgumentList.Add("--portable");
            Process.Start(start);
            StatusNotice.Text = "Close OBS when you're done so Meh Tools can open it for games.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open OBS", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (_settings.MinimizeToTray) Hide();
        else _ = Dispatcher.InvokeAsync(ExitAsync);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_exiting || !_settings.TrayOnMinimize || WindowState != WindowState.Minimized) return;
        WindowState = WindowState.Normal; // Reopening from the tray should show a normal window, not a restored taskbar one.
        Hide();
        if (_minimizeTipShown) return;
        _minimizeTipShown = true;
        _tray.ShowBalloonTip(2500, "Meh Tools", "Still running in the notification area. Double-click the tray icon to reopen it.", Forms.ToolTipIcon.Info);
    }


    private void TestObs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = ObsValidation.Validate(_settings.ObsExecutablePath, _settings.ObsConfigRoot, RecordingFolderForHealth(), _settings.ObsPortable);
            var msg = string.Join(Environment.NewLine, result.Info.Concat(result.Issues.Select(i => "Issue: " + i)));
            if (result.Ok)
                MessageBox.Show(this, (msg.Length == 0 ? "OBS configuration looks valid." : msg) + "\n\nClose OBS before managed capture so Meh Tools can update its configuration.", "OBS setup check", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, msg.Length == 0 ? "OBS validation failed." : msg, "OBS setup check", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "OBS setup check", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void TestVibrance_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var nvidia = new NvidiaVibrance();
            var displays = nvidia.Read();
            var current = displays.Count == 0 ? "none" : string.Join(", ", displays.Select(d => d.Percent + "%"));
            var msg = $"NVIDIA vibrance available on {displays.Count} display(s). Current: {current}.";
            if (_appliedSettings.AutomationEnabled)
            {
                msg += "\n\nStop watching before apply-and-revert tests to avoid conflicts with automation.";
                MessageBox.Show(this, msg, "Colour test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var targets = _settings.VibranceTargetMode == VibranceTargetMode.SelectedDisplay
                ? displays.Where(d => string.Equals(d.DeviceName, _settings.VibranceSelectedDisplay, StringComparison.OrdinalIgnoreCase)).ToArray()
                : displays.ToArray();
            if (_settings.VibranceTargetMode == VibranceTargetMode.GameWindowDisplay || targets.Length == 0 ||
                targets.Any(d => string.IsNullOrWhiteSpace(d.DeviceName)) ||
                targets.Select(d => d.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            {
                MessageBox.Show(this, msg + "\n\nApply-and-revert requires All displays with known identities, or an available Selected display. Game-window targeting is checked during watching.", "Colour test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, msg + $"\n\nBriefly apply the global game vibrance level to {targets.Length} targeted display(s), then restore each original value?", "Colour test", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var attempted = new List<VibranceDisplay>();
                var failures = new List<string>();
                try
                {
                    foreach (var display in targets)
                    {
                        attempted.Add(display);
                        nvidia.SetDisplay(display.DeviceName!, Math.Clamp(_settings.GameVibrance, 0, 100));
                    }
                    System.Threading.Thread.Sleep(400);
                }
                catch (Exception ex) { failures.Add(ex.Message); }
                finally
                {
                    foreach (var display in attempted)
                    {
                        try { nvidia.SetDisplay(display.DeviceName!, display.Percent); }
                        catch (Exception ex) { failures.Add($"Couldn't restore {display.DeviceName} to {display.Percent}%: {ex.Message}"); }
                    }
                }
                if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
                MessageBox.Show(this, "Applied and reverted vibrance successfully.", "Colour test", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else MessageBox.Show(this, msg, "Colour test", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Colour test", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void TestPower_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var active = PowerPlans.GetActive();
            var plans = PowerPlans.List();
            var name = plans.FirstOrDefault(p => p.Id == active)?.Name ?? active.ToString();
            var msg = $"Active power plan: {name}. {plans.Count} plan(s) available.";
            if (_settings.AutomationEnabled)
            {
                msg += "\n\nStop watching before apply-and-revert tests to avoid conflicts with automation.";
                MessageBox.Show(this, msg, "Power plan test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_settings.GamingPowerPlan != Guid.Empty && MessageBox.Show(this, msg + "\n\nBriefly switch to the gaming plan and revert to test control?", "Power plan test", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var original = active;
                try { PowerPlans.SetActive(_settings.GamingPowerPlan); System.Threading.Thread.Sleep(400); }
                finally { try { PowerPlans.SetActive(original); } catch { } }
                MessageBox.Show(this, "Switched and reverted power plan successfully.", "Power plan test", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else MessageBox.Show(this, msg, "Power plan test", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Power plan test", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }


    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export settings", Filter = "Settings JSON (*.json)|*.json", FileName = "MehTools-settings.json" };
            if (picker.ShowDialog(this) != true) return;
            if (!TryApplyPendingSettings(out var problem, validateSetup: false)) throw new InvalidOperationException(problem);
            File.WriteAllText(picker.FileName, _store.Serialize(_settings.Snapshot()));
            MessageBox.Show(this, "Settings exported.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't export settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Import settings", Filter = "Settings JSON (*.json)|*.json|All files (*.*)|*.*" };
            if (picker.ShowDialog(this) != true) return;
            var loaded = _store.Deserialize(File.ReadAllText(picker.FileName));
            var profileIssues = SettingsValidation.Validate(loaded).Where(error => error.Contains("profile", StringComparison.OrdinalIgnoreCase)).ToArray();
            ApplyImportedSettings(loaded);
            RefreshGameProfileOptions();
            DataContext = null; DataContext = _settings;
            if (MatchModeColumn is not null) MatchModeColumn.ItemsSource = new[] { "path", "name", "folder" };
            SyncAppsToCloseBox();
            RefreshRecordingStorageTargets();
            _ = RefreshStorageVisibilityAsync();
            UpdateAutomationButton();
            var message = "Settings imported. Review paths and click Save to keep them.";
            if (profileIssues.Length > 0) message += "\n\nProfile issues need attention before saving:\n" + string.Join("\n", profileIssues);
            MessageBox.Show(this, message, "Import", MessageBoxButton.OK,
                profileIssues.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't import settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ApplyImportedSettings(AppSettings imported)
    {
        // Keep the live collections so bindings stay attached, and copy their contents separately.
        foreach (var prop in typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.Name is nameof(AppSettings.Games) or nameof(AppSettings.Profiles) or nameof(AppSettings.AppsToCloseOnGameLaunch) or nameof(AppSettings.AutomationEnabled)) continue;
            prop.SetValue(_settings, prop.GetValue(imported));
        }
        _settings.Games.Clear();
        foreach (var game in imported.Games) _settings.Games.Add(game.Copy());
        _settings.Profiles.Clear();
        foreach (var profile in imported.Profiles) _settings.Profiles.Add(profile.Copy());
        _settings.AppsToCloseOnGameLaunch.Clear();
        foreach (var app in imported.AppsToCloseOnGameLaunch) _settings.AppsToCloseOnGameLaunch.Add(app);
    }

    // Overlay hotkey
    private const int WM_HOTKEY = 0x0312;
    private const int OverlayHotkeyId = 0x4A54;
    private const int SaveClipHotkeyId = 0x4A55;
    private const int StutterMarkerHotkeyId = 0x4A56;
    private const uint MOD_NOREPEAT = 0x4000;
    private System.Windows.Interop.HwndSource? _hwndSource;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void ApplyOverlayHotkey_Click(object sender, RoutedEventArgs e) => ApplyOverlayHotkey();

    private void ApplyOverlayHotkey()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, OverlayHotkeyId);
            _hwndSource ??= System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (_hwndSource is not null) { _hwndSource.RemoveHook(WndProc); _hwndSource.AddHook(WndProc); }
            if (!TryParseHotkey(_settings.OverlayHotkey ?? "", out var mod, out var key)) return;
            if (!RegisterHotKey(hwnd, OverlayHotkeyId, mod, key))
            {
                OverlayStatus.Text = "Couldn't register global hotkey (it may be in use).";
                StatusNotice.Text = "Hotkey not registered.";
            }
            else if (OverlayStatus is not null) OverlayStatus.Text = "Global hotkey applied. Reopen monitor to apply option changes.";
        }
        catch (Exception ex) { StatusNotice.Text = "Hotkey error: " + ex.Message; }
    }

    private void ApplySaveClipHotkey_Click(object sender, RoutedEventArgs e) => ApplySaveClipHotkey();

    private void ApplySaveClipHotkey()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, SaveClipHotkeyId);
            _hwndSource ??= System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (_hwndSource is not null) { _hwndSource.RemoveHook(WndProc); _hwndSource.AddHook(WndProc); }
            var text = _settings.SaveClipHotkey ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusNotice.Text = "Save clip hotkey disabled.";
                return;
            }
            if (!TryParseHotkey(text, out var mod, out var key) || mod == 0)
            {
                StatusNotice.Text = "Save clip hotkey is invalid. Use a key with a modifier, such as Ctrl+Alt+F10, or leave it blank.";
                return;
            }
            if (!RegisterHotKey(hwnd, SaveClipHotkeyId, mod | MOD_NOREPEAT, key))
            {
                StatusNotice.Text = "Save clip hotkey could not be registered; it may be invalid or already in use.";
            }
            else StatusNotice.Text = "Save clip hotkey applied.";
        }
        catch (Exception ex) { StatusNotice.Text = "Save clip hotkey error: " + ex.Message; }
    }

    private void ApplyStutterMarkerHotkey_Click(object sender, RoutedEventArgs e) => ApplyStutterMarkerHotkey();

    private void ApplyStutterMarkerHotkey()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, StutterMarkerHotkeyId);
            _hwndSource ??= System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (_hwndSource is not null) { _hwndSource.RemoveHook(WndProc); _hwndSource.AddHook(WndProc); }
            var text = _settings.StutterMarkerHotkey ?? "";
            if (string.IsNullOrWhiteSpace(text)) { StatusNotice.Text = "Stutter marker hotkey disabled."; return; }
            if (!TryParseHotkey(text, out var mod, out var key) || mod == 0)
            { StatusNotice.Text = "Stutter marker hotkey is invalid. Use a key with a modifier, such as Ctrl+Alt+F9, or leave it blank."; return; }
            if (!RegisterHotKey(hwnd, StutterMarkerHotkeyId, mod | MOD_NOREPEAT, key))
                StatusNotice.Text = "Stutter marker hotkey could not be registered; it conflicts with another hotkey or application.";
            else StatusNotice.Text = "Stutter marker hotkey applied.";
        }
        catch (Exception ex) { StatusNotice.Text = "Stutter marker hotkey error: " + ex.Message; }
    }

    private void ApplyBookmarkHotkey()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, BookmarkHotkeyId);
            _hwndSource ??= System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (_hwndSource is not null) { _hwndSource.RemoveHook(WndProc); _hwndSource.AddHook(WndProc); }
            var text = _appliedSettings.BookmarkHotkey ?? "";
            if (string.IsNullOrWhiteSpace(text)) { StatusNotice.Text = "Bookmark hotkey disabled."; return; }
            if (!TryParseHotkey(text, out var mod, out var key) || mod == 0)
            { StatusNotice.Text = "Bookmark hotkey is invalid. Use a key with a modifier, such as Ctrl+Alt+F8, or leave it blank."; return; }
            if (!RegisterHotKey(hwnd, BookmarkHotkeyId, mod | MOD_NOREPEAT, key))
                StatusNotice.Text = "Bookmark hotkey could not be registered; it conflicts with another hotkey or application.";
            else StatusNotice.Text = "Bookmark hotkey applied.";
        }
        catch (Exception ex) { StatusNotice.Text = "Bookmark hotkey error: " + ex.Message; }
    }

    private const int BookmarkHotkeyId = 0x4A57;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == OverlayHotkeyId)
        {
            Dispatcher.Invoke(ToggleOverlay);
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == SaveClipHotkeyId)
        {
            Dispatcher.BeginInvoke(() => _ = SaveClipAsync(true));
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == StutterMarkerHotkeyId)
        {
            Dispatcher.BeginInvoke(HandleStutterMarkerHotkey);
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == RollingHotkeyId)
        { TriggerRolling(); handled = true; }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == BookmarkHotkeyId)
        { Dispatcher.BeginInvoke(() => _ = MarkBookmarkAsync(true)); handled = true; }
        return IntPtr.Zero;
    }

    private void HandleStutterMarkerHotkey()
    {
        if (_performanceLogger is null)
        {
            const string message = "No performance log is active. Open Performance and click Start log before marking a stutter.";
            StatusNotice.Text = message;
            if (_settings.ShowNotifications && !_exiting) _tray.ShowBalloonTip(3000, "Stutter marker", message, Forms.ToolTipIcon.Info);
            return;
        }
        _performanceLogger.AddMarker("Stutter");
        const string recorded = "Stutter marker recorded at hotkey time (approximate; it may not be the exact stalled frame).";
        StatusNotice.Text = recorded;
        if (_settings.ShowNotifications && !_exiting) _tray.ShowBalloonTip(2500, "Stutter marker", "Marker added to the active performance log.", Forms.ToolTipIcon.Info);
    }

    private static bool TryParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? keyPart = null;
        foreach (var p in parts)
        {
            var u = p.ToUpperInvariant();
            if (u is "CTRL" or "CONTROL") modifiers |= 0x0002;
            else if (u is "ALT") modifiers |= 0x0001;
            else if (u is "SHIFT") modifiers |= 0x0004;
            else if (u is "WIN" or "WINDOWS") modifiers |= 0x0008;
            else if (keyPart is null) keyPart = p;
            else return false;
        }
        if (keyPart is null) return false;
        if (keyPart.Length == 1)
        {
            var c = char.ToUpperInvariant(keyPart[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
        }
        if (Enum.TryParse<System.Windows.Forms.Keys>(keyPart, true, out var k)) { vk = (uint)k; return vk != 0; }
        return false;
    }
    private async Task ExitAsync()
    {
        if (_exiting) return;
        if (!await PreparePerformanceForExitAsync()) return;
        if (!CloseConfigSnapshotsForExit()) return;
        foreach (var playback in OwnedWindows.OfType<RecordingPlaybackWindow>().ToArray()) { await playback.ShutdownAsync(); playback.Close(); }
        if (_liveWardogsWindow is { } live) { await live.ShutdownAsync(); live.Close(); }
        var wardogs = _wardogsWindow;
        if (wardogs is not null) { if (!await wardogs.ShutdownAsync()) { wardogs.Activate(); return; } wardogs.Close(); }
        _exiting = true;
        await StopRollingAsync();
        try { _sessionTracker?.Observe(DateTime.UtcNow, false, Array.Empty<SessionGameObservation>()); }
        catch (Exception ex) { AddActivity("Couldn't finish session history: " + ex.Message); }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, OverlayHotkeyId);
            UnregisterHotKey(hwnd, SaveClipHotkeyId);
            UnregisterHotKey(hwnd, StutterMarkerHotkeyId);
            UnregisterHotKey(hwnd, BookmarkHotkeyId);
            UnregisterHotKey(hwnd, RollingHotkeyId);
        }
        _overlay?.Close();
        Hide();
        await DisconnectObsAsync();
        try { await _automation.DisposeAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Meh Tools", MessageBoxButton.OK, MessageBoxImage.Warning); }
        _tray.Visible = false;
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}

public sealed record PowerPlanOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record GameProfileAssignmentOption(Guid? Id, string Name);
public sealed record GameValueOption<T>(T? Value, string Name) where T : struct, Enum;
public sealed record VibranceDisplayOption(string DeviceName, string Name);
