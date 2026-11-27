using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
    private readonly string _dataDirectory;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private AppSettings _appliedSettings;
    private readonly AutomationService _automation;
    private SessionLibraryStore? _sessionStore;
    private SessionTracker? _sessionTracker;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _trayWatchItem;
    private readonly Forms.ToolStripMenuItem _trayPerfItem;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _initializing = true;
    private bool _exiting;
    private bool _saving;
    private bool _minimizeTipShown;
    private readonly HashSet<string> _activeGamePaths = new(StringComparer.OrdinalIgnoreCase);
    public System.Collections.ObjectModel.ObservableCollection<PowerPlanOption> GamePowerPlanOptions { get; } = [];
    public IReadOnlyList<GameValueOption<CaptureMode>> CaptureModeOptions { get; } =
        [new(null, "Default (Recording)"), new(CaptureMode.Recording, "Recording"), new(CaptureMode.Replay, "Replay"), new(CaptureMode.Both, "Both")];
    public IReadOnlyList<GameValueOption<VibranceTargetMode>> VibranceTargetOptions { get; } =
        [new(VibranceTargetMode.AllDisplays, "All displays"), new(VibranceTargetMode.SelectedDisplay, "Selected display"), new(VibranceTargetMode.GameWindowDisplay, "Game window display")];
    public System.Collections.ObjectModel.ObservableCollection<VibranceDisplayOption> VibranceDisplayOptions { get; } = [];

    public MainWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamesTools")) { }

    internal MainWindow(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        InitializeComponent();
        InitializeDiagnosticConditions();
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
        InitializeRecordingPanel();
        UpdateObsSetupStatus();
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
        _trayWatchItem = new Forms.ToolStripMenuItem(_settings.AutomationEnabled ? "Stop watching" : "Start watching", null, (_, _) => Dispatcher.Invoke(() => ToggleAutomation_Click(this, new RoutedEventArgs())));
        _tray.ContextMenuStrip.Items.Add(_trayWatchItem);
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _trayPerfItem = new Forms.ToolStripMenuItem("Start performance log", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (_performanceLogger is null) StartPerformance_Click(this, new RoutedEventArgs());
            else StopPerformance_Click(this, new RoutedEventArgs());
        }));
        _tray.ContextMenuStrip.Items.Add(_trayPerfItem);
        _tray.ContextMenuStrip.Items.Add("Save OBS clip", null, (_, _) => Dispatcher.Invoke(() => _ = SaveClipAsync(true)));
        _tray.ContextMenuStrip.Items.Add("Steam highlights…", null, (_, _) => Dispatcher.Invoke(() => { ShowFromTray(); SteamWardogs_Click(this, new RoutedEventArgs()); }));
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(() => _ = ExitAsync()));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        _appliedSettings = _settings.Snapshot();
        ApplySteamHighlightSettings();
        _automation = new AutomationService(_appliedSettings, _dataDirectory);
        _automation.StatusChanged += status => Dispatcher.BeginInvoke(() => ShowStatus(status));
        _automation.Activity += message => Dispatcher.BeginInvoke(() => AddActivity(message));
        _automation.Notify += notification => Dispatcher.BeginInvoke(() => ShowTrayNotification(notification));
        _automation.Start();
        RefreshSessionList();
        SourceInitialized += (_, _) => { ApplySaveClipHotkey(); ApplyStutterMarkerHotkey(); };
        AddActivity(initialMessage ?? (_settings.Games.Count == 0 ? "Add a game to get started." : "Ready."));
        if (initialMessage is not null) StatusNotice.Text = initialMessage;

        // Debounced auto-save whenever any setting control changes.
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); SaveSettings(silent: true); };
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnSettingControlChanged));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSettingControlChanged));
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnSettingControlChanged));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSettingControlChanged));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(OnSettingControlChanged));
        _initializing = false;

        _ = Dispatcher.InvokeAsync(ResumeSavedObsConnectionAsync);
    }

    private void OnSettingControlChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _exiting || _saving) return;
        if (e.OriginalSource is DependencyObject src && IsNonSettingControl(src)) return;
        ScheduleAutoSave();
    }

    private bool IsNonSettingControl(DependencyObject source)
    {
        for (var cur = source; cur is not null; cur = LogicalTreeHelper.GetParent(cur))
        {
            if (cur == Navigation || cur == Pages || cur == SessionsList || cur == SessionsSearch ||
                cur == SessionNotes || cur == SessionFavourite || cur == SessionAttachments ||
                cur == PerformanceSetup || cur == PerformanceNotes || cur == MarkerNoteBox ||
                cur == RetestChangeBox || cur == PerformanceGame || cur == RecordingStorageTarget ||
                cur == ObsWebSocketPasswordBox || cur == ObsRememberPasswordBox ||
                cur == ActivityList)
                return true;
            if (cur is Expander) return true;
        }
        return false;
    }

    private void ScheduleAutoSave()
    {
        if (_initializing || _exiting || _saving) return;
        SetSaveBadge("Saving…", warning: false);
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void SetSaveBadge(string? text, bool warning)
    {
        if (SaveStateBadge is null || SaveStatePill is null) return;
        if (string.IsNullOrEmpty(text))
        {
            SaveStatePill.Visibility = Visibility.Collapsed;
            return;
        }
        SaveStateBadge.Text = text;
        SaveStateBadge.Foreground = new System.Windows.Media.SolidColorBrush(warning
            ? System.Windows.Media.Color.FromRgb(0xE8, 0xB0, 0x5C)
            : System.Windows.Media.Color.FromRgb(0x8A, 0xE1, 0xC3));
        SaveStatePill.Visibility = Visibility.Visible;
    }

    private void UpdateStatusDot(string headline)
    {
        if (SidebarStatusDot is null) return;
        var color = headline is "Needs attention" or "OBS still open"
            ? System.Windows.Media.Color.FromRgb(0xE8, 0xB0, 0x5C)
            : headline is "Off" or "Loading…"
                ? System.Windows.Media.Color.FromRgb(0x6F, 0x80, 0x8A)
                : System.Windows.Media.Color.FromRgb(0x8A, 0xE1, 0xC3);
        SidebarStatusDot.Fill = new System.Windows.Media.SolidColorBrush(color);
    }

    public void ShowFromTray()
    {
        if (_exiting) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void GameGrid_LoadingRow(object? sender, DataGridRowEventArgs e) => ApplyGameRowState(e.Row);

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
            row.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            row.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            row.ClearValue(System.Windows.Controls.Control.FontWeightProperty);
        }
    }

    private void ShowStatus(AutomationStatus status)
    {
        if (_exiting) return;
        UpdateDetectedSessions(status);
        StatusGame.Text = status.Game;
        StatusRecording.Text = status.Recording;
        StatusVibrance.Text = status.Vibrance;
        StatusPower.Text = status.Power;
        OverviewHeadline.Text = status.Headline;
        SidebarStatus.Text = status.Headline;
        UpdateStatusDot(status.Headline);
        _activeGamePaths.Clear();
        if (status.ActiveGamePaths is not null) foreach (var p in status.ActiveGamePaths) _activeGamePaths.Add(p);
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
            if (_trayWatchItem is not null)
                _trayWatchItem.Text = _settings.AutomationEnabled ? "Stop watching" : "Start watching";
            if (_trayPerfItem is not null)
                _trayPerfItem.Text = _performanceLogger is not null ? "Stop & save performance log" : "Start performance log";
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
        PageTitle.Text = (Pages.SelectedItem as TabItem)?.Header.ToString();
        if (SettingsNavButton is not null) SettingsNavButton.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        if (Pages is null) return;
        Navigation.SelectedIndex = -1;
        Pages.SelectedIndex = Pages.Items.Count - 1;
        PageTitle.Text = "Settings";
        SettingsNavButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x29, 0x3C, 0x43));
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings(silent: false);

    private bool SaveSettings(bool silent = false)
    {
        if (_saving) return false;
        _autoSaveTimer.Stop();
        _saving = true;
        try
        {
            if (!TryApplyPendingSettings(out var problem, revealInvalid: !silent))
            {
                if (silent)
                {
                    SetSaveBadge("Check fields", warning: true);
                    return false;
                }
                throw new InvalidOperationException(problem);
            }

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
            ApplySteamHighlightSettings();
            RefreshRecordingStorageTargets();
            _ = RefreshStorageVisibilityAsync();
            UpdateAutomationButton();
            UpdateObsSetupStatus();
            ApplySaveClipHotkey();
            ApplyStutterMarkerHotkey();
            SetSaveBadge("Saved", warning: false);
            if (!silent)
            {
                StatusNotice.Text = "Settings saved.";
                AddActivity("Settings saved.");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or ArgumentException)
        {
            SetSaveBadge("Not saved", warning: true);
            if (!silent) MessageBox.Show(this, ex.Message, "Couldn't save settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _saving = false; }
    }

    private void GameOverrideChanged(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(RefreshEffectiveOptionsSummary));

    private void GameGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshEffectiveOptionsSummary();
        RefreshGameSteamHighlights();
    }

    private void GameGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            RefreshGameSteamHighlights();
            ScheduleAutoSave();
        }));
    }

    private void GameObsCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { DataContext: GameRule game } box) return;
        var enabled = box.IsChecked == true;
        try
        {
            var saved = _appliedSettings.Snapshot();
            var rule = saved.Games.FirstOrDefault(candidate => ExecutablePathRules.EqualsPath(candidate.ExecutablePath, game.ExecutablePath));
            if (rule is not null && rule.Record != enabled)
            {
                rule.Record = enabled;
                if (enabled && saved.RecordingEnabled)
                {
                    var errors = SettingsValidation.Validate(saved);
                    if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                }
                _store.Save(saved);
                _appliedSettings = saved;
                _automation.UpdateSettings(saved);
            }
            box.GetBindingExpression(System.Windows.Controls.CheckBox.IsCheckedProperty)?.UpdateSource();
            RefreshEffectiveOptionsSummary();
            UpdateObsSetupStatus();
            StatusNotice.Text = $"Automatic OBS capture {(enabled ? "enabled" : "disabled")} for {game.Name}.";
            ScheduleAutoSave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            box.GetBindingExpression(System.Windows.Controls.CheckBox.IsCheckedProperty)?.UpdateTarget();
            MessageBox.Show(this, ex.Message, "Couldn't change automatic OBS capture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        EffectiveOptionsSummary.Text = $"Effective: {options.CaptureMode} · folder: {options.RecordingDirectory} · replay: {options.ReplayBufferSeconds}s · stop delay: {options.RecordingStopDelaySeconds}s · vibrance: {options.VibrancePercent}% · power: {powerName}";
    }

    private bool TryApplyPendingSettings(out string problem, bool validateSetup = true, bool revealInvalid = true)
    {
        problem = "";
        if (!TryCommitGameGridEdits(out problem, revealInvalid)) return false;
        if (FindInvalidGameEditor(this) is System.Windows.Controls.TextBox invalidEditor)
        {
            if (revealInvalid)
            {
                Navigation.SelectedIndex = 1;
                for (var parent = LogicalTreeHelper.GetParent(invalidEditor); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
                    if (parent is Expander expander) expander.IsExpanded = true;
                invalidEditor.BringIntoView();
                invalidEditor.Focus();
                invalidEditor.SelectAll();
            }
            problem = "Check the highlighted game value. Enter a whole number, or leave it blank to use the default.";
            return false;
        }
        if (!TryReadWholeNumber(StopDelayBox, "the wait time before stopping", 0, 120, out var stopDelay, out problem, revealInvalid)) return false;
        if (!TryReadWholeNumber(ReplayBufferSecondsBox, "the replay buffer length", 15, 600, out var replayBufferSeconds, out problem, revealInvalid)) return false;
        if (!TryReadWholeNumber(SteamBeforeBox, "the Steam seconds before each event", 0, SteamHighlightSettings.MaxPaddingSeconds, out var steamBefore, out problem, revealInvalid)) return false;
        if (!TryReadWholeNumber(SteamAfterBox, "the Steam seconds after each event", 0, SteamHighlightSettings.MaxPaddingSeconds, out var steamAfter, out problem, revealInvalid)) return false;
        if (!SteamHighlightSettings.ValidPadding(steamBefore, steamAfter))
        {
            if (revealInvalid) RevealEditor(SteamAfterBox);
            problem = "Choose at least one second before or after each Steam event.";
            return false;
        }
        _settings.SteamHighlights.BeforeSeconds = steamBefore;
        _settings.SteamHighlights.AfterSeconds = steamAfter;
        if (!TryReadWholeNumber(PollSecondsBox, "the check interval", 2, 30, out var poll, out problem, revealInvalid)) return false;
        _settings.RecordingStopDelaySeconds = stopDelay;
        _settings.ReplayBufferSeconds = replayBufferSeconds;
        _settings.PollSeconds = poll;
        SyncAppsToCloseFromBox();
        try { NetworkProbeOptions.Create(_settings.NetworkDiagnosticsEnabled, _settings.NetworkTargets); }
        catch (ArgumentException ex) { problem = ex.Message; return false; }
        if (!validateSetup) return true;
        var errors = SettingsValidation.Validate(_settings);
        if (errors.Count > 0)
        {
            if (revealInvalid && UpdateObsSetupStatus() && _settings.RecordingEnabled)
            {
                Navigation.SelectedIndex = 2;
                ObsSetupOptions.IsExpanded = true;
            }
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

    private bool TryCommitGameGridEdits(out string problem, bool focusOnFailure = true)
    {
        problem = "";
        if (GameGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            GameGrid.CommitEdit(DataGridEditingUnit.Row, true))
            return true;
        if (focusOnFailure) FocusGameGridEditor();
        problem = "Check the highlighted game value. Enter a whole number, or leave the cell blank to use the default.";
        return false;
    }

    private bool TryReadWholeNumber(System.Windows.Controls.TextBox box, string label, int min, int max, out int value, out string problem, bool reveal = true)
    {
        problem = "";
        if (!int.TryParse(box.Text?.Trim(), out value) || value < min || value > max)
        {
            value = 0;
            if (reveal) RevealEditor(box);
            problem = $"Enter a whole number from {min} to {max} for {label}.";
            return false;
        }
        return true;
    }

    private void RevealEditor(System.Windows.Controls.TextBox box)
    {
        for (var parent = LogicalTreeHelper.GetParent(box); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
        {
            if (parent is Expander expander) expander.IsExpanded = true;
            if (parent is TabItem page)
            {
                var idx = Pages.Items.IndexOf(page);
                if (idx == Pages.Items.Count - 1) SettingsNav_Click(this, new RoutedEventArgs());
                else Navigation.SelectedIndex = idx;
                break;
            }
        }
        UpdateLayout();
        box.BringIntoView();
        box.Focus();
        box.SelectAll();
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
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
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
            var paused = _appliedSettings.Snapshot();
            paused.AutomationEnabled = false;
            _automation.UpdateSettings(paused);
            _appliedSettings = paused;
            _settings.AutomationEnabled = false;
            UpdateAutomationButton();
            StatusNotice.Text = "Watching stopped.";
            AddActivity("Watching stopped.");
            try { _store.Save(paused); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                StatusNotice.Text = "Watching stopped for this session. Couldn't save state: " + ex.Message;
            }
            return;
        }
        var previous = _settings.AutomationEnabled;
        _settings.AutomationEnabled = !previous;
        if (!SaveSettings(silent: false)) _settings.AutomationEnabled = previous;
        UpdateAutomationButton();
    }

    private void UpdateAutomationButton()
    {
        AutomationButton.Content = _settings.AutomationEnabled ? "Stop watching" : "Start watching";
        var headline = _settings.AutomationEnabled ? "Watching for games" : "Off";
        OverviewHeadline.Text = headline;
        SidebarStatus.Text = headline;
        UpdateStatusDot(headline);
        UpdateTrayMenuText();
    }

    private void PowerEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { IsChecked: true } &&
            (DesktopPlanBox.SelectedItem is null || GamingPlanBox.SelectedItem is null))
            PowerPlanOptions.IsExpanded = true;
    }

    private void BrowseForGame()
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
            var knownGames = _settings.Games.Select(game => game.ExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var choices = await Task.Run(() =>
            {
                var options = new AppDetectionOptions(true, _settings.DetectEpicGames, _settings.DetectGogGames, _settings.DetectEaGames, _settings.DetectUbisoftGames, _settings.DetectXboxGames);
                var catalog = LibraryGameDetector.Discover(options);
                var processes = ProcessSnapshot.Capture().Where(p => p.FullPath is not null).ToArray();
                var running = LibraryGameDetector.FindRunning(catalog, processes);
                var list = new List<RunningGameChoice>();
                foreach (var game in running)
                {
                    var proc = processes.FirstOrDefault(p => p.FullPath is not null && p.FullPath.StartsWith(game.RootDirectory, StringComparison.OrdinalIgnoreCase));
                    if (proc?.FullPath is { } path && !knownGames.Contains(path)) list.Add(new RunningGameChoice(game.Name + " (" + game.Source + ")", path));
                }
                return list.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (choices.Length == 0)
            {
                BrowseForGame();
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
            var hint = new TextBlock { Text = "Choose an open game or browse for its executable.",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(hint, Dock.Top);
            layout.Children.Add(hint);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var add = new System.Windows.Controls.Button { Content = "Add game", IsDefault = true, Style = (Style)FindResource("AccentButton") };
            var browse = new System.Windows.Controls.Button { Content = "Browse…" };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) };
            buttons.Children.Add(browse); buttons.Children.Add(add); buttons.Children.Add(cancel); layout.Children.Add(buttons);
            var list = new System.Windows.Controls.ListBox { ItemsSource = choices, SelectedIndex = 0,
                BorderThickness = new Thickness(0), Padding = new Thickness(5) };
            layout.Children.Add(list);
            add.Click += (_, _) => { if (list.SelectedItem is RunningGameChoice) dialog.DialogResult = true; };
            bool browseRequested = false;
            browse.Click += (_, _) => { browseRequested = true; dialog.Close(); };
            dialog.Content = layout;
            if (dialog.ShowDialog() == true && list.SelectedItem is RunningGameChoice selected)
                AddGame(selected.Name, selected.Path);
            else if (browseRequested) BrowseForGame();
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
        ScheduleAutoSave();
    }

    private sealed record RunningGameChoice(string Name, string Path)
    {
        public override string ToString() => $"{Name}  —  {System.IO.Path.GetFileName(Path)}    {Path}";
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is GameRule game)
        {
            _settings.Games.Remove(game);
            ScheduleAutoSave();
        }
    }

    private void BrowseObs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "OBS program (obs64.exe)|obs64.exe|Applications (*.exe)|*.exe", Title = "Choose OBS" };
        if (picker.ShowDialog(this) == true)
        {
            SetObsExecutable(picker.FileName, ObsDiscovery.FromExecutable(picker.FileName));
            DataContext = null; DataContext = _settings;
            UpdateObsSetupStatus();
            ScheduleAutoSave();
        }
    }

    private void CaptureEnabled_Click(object sender, RoutedEventArgs e)
    {
        bool enabling = CaptureEnabledBox.IsChecked == true;
        if (enabling && TryDiscoverObsSetup())
        {
            DataContext = null;
            DataContext = _settings;
            _ = RefreshStorageVisibilityAsync();
        }
        if (UpdateObsSetupStatus() && enabling) ObsSetupOptions.IsExpanded = true;
    }

    private bool TryDiscoverObsSetup()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ObsExecutablePath) || !string.IsNullOrWhiteSpace(_settings.ObsConfigRoot)) return false;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        var executable = Path.Combine(programFiles, "obs-studio", "bin", "64bit", "obs64.exe");
        if (!File.Exists(executable) || ObsDiscovery.FromExecutable(executable) is not { } discovered) return false;
        SetObsExecutable(executable, discovered);
        return true;
    }

    private bool UpdateObsSetupStatus()
    {
        OpenVideosButton.IsEnabled = Directory.Exists(_settings.RecordingDirectory);
        bool hasProgram = File.Exists(_settings.ObsExecutablePath);
        bool hasConfig = Directory.Exists(_settings.ObsConfigRoot);
        if (_settings.RecordingEnabled && hasProgram && !hasConfig) ObsSetupOptions.IsExpanded = true;
        string? missing = !hasProgram ? "Choose the OBS program."
            : !hasConfig ? "Choose the OBS settings folder."
            : _settings.Games.Any(game => game.Record && !Directory.Exists(EffectiveGameOptionsResolver.Resolve(_settings, game).RecordingDirectory))
                ? "Choose a video folder for the games you want to record."
                : null;
        ObsSetupStatus.Text = !_settings.RecordingEnabled ? "Enable capture here and for the games you want to record."
            : missing ?? (_settings.Games.Any(game => game.Record)
                ? "Close OBS before playing so Meh Tools can start it automatically."
                : "Enable OBS Capture for a game on the Games page.");
        return missing is not null;
    }

    private void SetObsExecutable(string executable, DiscoveredObs? discovered)
    {
        _settings.ObsExecutablePath = executable;
        if (discovered is null) return;

        _settings.ObsConfigRoot = discovered.ConfigRoot;
        _settings.ObsPortable = discovered.Portable;
        if (string.IsNullOrWhiteSpace(_settings.RecordingDirectory) && discovered.RecordingDirectory is { Length: > 0 } folder &&
            Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
            _settings.RecordingDirectory = folder;
    }

    private void BrowseObsConfig_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the OBS settings folder" };
        if (picker.ShowDialog(this) == true)
        {
            _settings.ObsConfigRoot = picker.FolderName;
            DataContext = null; DataContext = _settings;
            UpdateObsSetupStatus();
            ScheduleAutoSave();
        }
    }

    private void BrowseRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to save videos" };
        if (picker.ShowDialog(this) == true)
        {
            _settings.RecordingDirectory = picker.FolderName;
            DataContext = null; DataContext = _settings;
            UpdateObsSetupStatus();
            ScheduleAutoSave();
        }
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
                var label = int.TryParse(suffix, out var number) ? $"Display {number} ({windowsName})" : $"Display ({windowsName})";
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
            var pm = pmResolved is not null ? (string.IsNullOrWhiteSpace(_settings.PresentMonPath) ? "bundled/found" : "found") : "not found";
            lines.Add("PresentMon: " + pm + (pmResolved is not null ? $" ({Path.GetFileName(pmResolved)})" : ""));
            try { lines.Add("HDR active now: " + (HdrStatus.IsHdrActive() ? "yes" : "no/unknown")); } catch { lines.Add("HDR active now: unknown"); }
            HealthSummary.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { HealthSummary.Text = "Couldn't check setup: " + ex.Message; }
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
            try { if (running.Length > 0) throw new InvalidOperationException("OBS is already open."); }
            finally { foreach (var process in running) process.Dispose(); }
            if (!File.Exists(_settings.ObsExecutablePath)) throw new FileNotFoundException("Choose the OBS program first.");
            var start = new ProcessStartInfo(_settings.ObsExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_settings.ObsExecutablePath)!
            };
            if (_settings.ObsPortable) start.ArgumentList.Add("--portable");
            Process.Start(start);
            StatusNotice.Text = "Close OBS when you're done so Meh Tools can start it automatically for games.";
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
        WindowState = WindowState.Normal;
        Hide();
        if (_minimizeTipShown) return;
        _minimizeTipShown = true;
        _tray.ShowBalloonTip(2500, "Meh Tools", "Running in the tray. Double-click the icon to reopen.", Forms.ToolTipIcon.Info);
    }

    private void TestObs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = ObsValidation.Validate(_settings.ObsExecutablePath, _settings.ObsConfigRoot, RecordingFolderForHealth(), _settings.ObsPortable);
            var msg = string.Join(Environment.NewLine, result.Info.Concat(result.Issues.Select(i => "Issue: " + i)));
            if (result.Ok)
                MessageBox.Show(this, msg.Length == 0 ? "OBS configuration looks valid." : msg, "OBS setup check", MessageBoxButton.OK, MessageBoxImage.Information);
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
                msg += "\n\nStop watching first to run an apply-and-revert test.";
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
                MessageBox.Show(this, msg, "Colour test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, msg + $"\n\nBriefly apply game vibrance to {targets.Length} display(s) and revert?", "Colour test", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
                    Thread.Sleep(400);
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
                msg += "\n\nStop watching first to run a switch-and-revert test.";
                MessageBox.Show(this, msg, "Power plan test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_settings.GamingPowerPlan != Guid.Empty && MessageBox.Show(this, msg + "\n\nBriefly switch to the gaming plan and revert?", "Power plan test", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var original = active;
                try { PowerPlans.SetActive(_settings.GamingPowerPlan); Thread.Sleep(400); }
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
            ApplyImportedSettings(loaded);
            DataContext = null; DataContext = _settings;
            if (MatchModeColumn is not null) MatchModeColumn.ItemsSource = new[] { "path", "name", "folder" };
            SyncAppsToCloseBox();
            RefreshRecordingStorageTargets();
            _ = RefreshStorageVisibilityAsync();
            UpdateAutomationButton();
            UpdateObsSetupStatus();
            ScheduleAutoSave();
            MessageBox.Show(this, "Settings imported. Review paths if this backup came from another PC.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't import settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ApplyImportedSettings(AppSettings imported)
    {
        foreach (var prop in typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.Name is nameof(AppSettings.Games) or nameof(AppSettings.AppsToCloseOnGameLaunch) or nameof(AppSettings.AutomationEnabled)) continue;
            prop.SetValue(_settings, prop.GetValue(imported));
        }
        _settings.Games.Clear();
        foreach (var game in imported.Games) _settings.Games.Add(game.Copy());
        _settings.AppsToCloseOnGameLaunch.Clear();
        foreach (var app in imported.AppsToCloseOnGameLaunch) _settings.AppsToCloseOnGameLaunch.Add(app);
    }

    // Global hotkeys
    private const int WM_HOTKEY = 0x0312;
    private const int SaveClipHotkeyId = 0x4A55;
    private const int StutterMarkerHotkeyId = 0x4A56;
    private const uint MOD_NOREPEAT = 0x4000;
    private System.Windows.Interop.HwndSource? _hwndSource;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!TryParseHotkey(text, out var mod, out var key) || mod == 0)
            {
                StatusNotice.Text = "Save clip hotkey is invalid. Use a modifier + key like Ctrl+Alt+F10, or leave blank.";
                return;
            }
            if (!RegisterHotKey(hwnd, SaveClipHotkeyId, mod | MOD_NOREPEAT, key))
                StatusNotice.Text = "Save clip hotkey could not be registered (already in use).";
        }
        catch (Exception ex) { StatusNotice.Text = "Save clip hotkey error: " + ex.Message; }
    }

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
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!TryParseHotkey(text, out var mod, out var key) || mod == 0)
            { StatusNotice.Text = "Stutter marker hotkey is invalid. Use a modifier + key like Ctrl+Alt+F9, or leave blank."; return; }
            if (!RegisterHotKey(hwnd, StutterMarkerHotkeyId, mod | MOD_NOREPEAT, key))
                StatusNotice.Text = "Stutter marker hotkey could not be registered (already in use).";
        }
        catch (Exception ex) { StatusNotice.Text = "Stutter marker hotkey error: " + ex.Message; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == SaveClipHotkeyId)
        {
            Dispatcher.BeginInvoke(() => _ = SaveClipAsync(true));
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == StutterMarkerHotkeyId)
        {
            Dispatcher.BeginInvoke(HandleStutterMarkerHotkey);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void HandleStutterMarkerHotkey()
    {
        if (_performanceLogger is null)
        {
            const string message = "No performance log is active. Open Performance and click Start log first.";
            StatusNotice.Text = message;
            if (_settings.ShowNotifications && !_exiting) _tray.ShowBalloonTip(3000, "Stutter marker", message, Forms.ToolTipIcon.Info);
            return;
        }
        _performanceLogger.AddMarker("Stutter");
        StatusNotice.Text = "Stutter marker added to performance log.";
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
        if (Enum.TryParse<Forms.Keys>(keyPart, true, out var k)) { vk = (uint)k; return vk != 0; }
        return false;
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _autoSaveTimer.Stop();
        if (!await PreparePerformanceForExitAsync()) return;
        if (steamWardogsWindow is { } steam) await steam.ShutdownAsync();
        _exiting = true;
        await StopSteamHighlightsAsync();
        try { _sessionTracker?.Observe(DateTime.UtcNow, false, Array.Empty<SessionGameObservation>()); }
        catch (Exception ex) { AddActivity("Couldn't finish session history: " + ex.Message); }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, SaveClipHotkeyId);
            UnregisterHotKey(hwnd, StutterMarkerHotkeyId);
        }
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

public sealed record GameValueOption<T>(T? Value, string Name) where T : struct, Enum;
public sealed record VibranceDisplayOption(string DeviceName, string Name);
