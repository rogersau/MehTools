using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Configuration;
using JamesTools.Core.ConfigurationSnapshots;
using JamesTools.Core.Platform;
using Microsoft.Win32;

namespace JamesTools;
public sealed class ConfigurationSnapshotsWindow : Window
{
    readonly ConfigSnapshotStore store;
    readonly GameRule game;
    readonly Action<string,string> openReport;
    readonly ListBox snapshots=new(){Height=120}, files=new(){Height=90}, reports=new(){Height=70};
    readonly ComboBox other = new() { MinWidth = 200, DisplayMemberPath = "Label" };
    readonly TextBox name = new() { Text = "Configuration snapshot" },
        output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 260 };
    readonly Button compare = new() { Content = "Compare" };
    readonly Button restore = new() { Content = "Restore reviewed file...", Visibility = Visibility.Collapsed };
    readonly StackPanel selectedDetails = new();
    ConfigSnapshot[] saved = [];
    RestorePlan? plan;
    bool busy, closed;
    public bool Busy => busy;
    ConfigSnapshot Selected => snapshots.SelectedItem as ConfigSnapshot ?? throw new IOException("Select a snapshot.");
    string FilePath => files.SelectedItem as string ?? throw new IOException("Select one snapshot file.");
    private sealed record CompareTarget(string Label, Guid? Snapshot);

    public ConfigurationSnapshotsWindow(ConfigSnapshotStore store, GameRule game, Action<string, string> openReport)
    {
        this.store = store; this.game = game.Copy(); this.openReport = openReport;
        WindowTheme.Apply(this);
        Title = "Configuration snapshots — " + game.Name; Width = 950; Height = 850; MinWidth = 600; MinHeight = 500;
        var top = new StackPanel { Margin = new Thickness(12) };
        Content = new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var selection = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var takeSnapshot = new Button { Content = "Create snapshot", HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        takeSnapshot.SetResourceReference(StyleProperty, "AccentButton");
        void RefreshSelection()
        {
            var paths = store.Selection(game.ExecutablePath);
            selection.Text = paths.Length > 0 ? string.Join(Environment.NewLine, paths) : "Choose the game configuration files to snapshot.";
            takeSnapshot.Visibility = paths.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        top.Children.Add(selection);
        var capture = new WrapPanel(); top.Children.Add(capture);
        capture.Children.Add(takeSnapshot);
        AddButton(capture, "Choose files...", async () =>
        {
            var picker = new OpenFileDialog { Multiselect = true, Filter = "Text configurations|*.ini;*.cfg;*.conf;*.json;*.xml;*.yaml;*.yml;*.toml;*.txt|All files|*.*" };
            if (picker.ShowDialog(this) != true) return;
            await Task.Run(() => store.Select(game.ExecutablePath, picker.FileNames));
            RefreshSelection();
        });
        takeSnapshot.Click += async (_, _) => await OperationAsync(async () =>
        {
            var label = name.Text;
            var created = await Task.Run(() => store.Create(game.ExecutablePath, label));
            Refresh(created.Id);
        });
        top.Children.Add(new Expander { Header = "Snapshot name", Content = name, Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 5, 0, 10) });

        var library = new StackPanel(); top.Children.Add(library);
        library.Children.Add(new TextBlock { Text = "Saved snapshots", FontWeight = FontWeights.SemiBold });
        snapshots.DisplayMemberPath = "Label"; library.Children.Add(snapshots); library.Children.Add(selectedDetails);
        selectedDetails.Children.Add(new TextBlock { Text = "Files in snapshot", Margin = new Thickness(0, 8, 0, 4) });
        selectedDetails.Children.Add(files);
        var comparison = new WrapPanel(); selectedDetails.Children.Add(comparison);
        comparison.Children.Add(other); comparison.Children.Add(compare);
        compare.Click += async (_, _) => await OperationAsync(async () =>
        {
            var snapshot = Selected;
            var path = FilePath;
            if ((other.SelectedItem as CompareTarget)?.Snapshot is { } otherId)
            {
                output.Text = await Task.Run(() => store.Compare(game.ExecutablePath, snapshot.Id, path, otherId));
            }
            else
            {
                var preview = await Task.Run(() => store.Preview(game.ExecutablePath, snapshot.Id, path));
                output.Text = $"Target {preview.Path}\nSnapshot {snapshot.Label}\nCurrent SHA256 {preview.CurrentHash}\nSnapshot SHA256 {preview.SnapshotHash}\n{preview.Difference}";
                plan = preview;
            }
            output.BringIntoView();
        });
        output.Visibility = Visibility.Collapsed;
        output.TextChanged += (_, _) => output.Visibility = string.IsNullOrWhiteSpace(output.Text) ? Visibility.Collapsed : Visibility.Visible;
        top.Children.Add(output); top.Children.Add(restore);
        restore.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        restore.Click += async (_, _) => await OperationAsync(async () =>
        {
            var preview = plan ?? throw new IOException("Compare with the current file first.");
            if (MessageBox.Show(this, $"Restore one file?\n{preview.Path}\nSnapshot {preview.Snapshot}\nHash {preview.SnapshotHash}\nClose the game first. A verified backup will be retained.",
                "Confirm configuration restore", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            plan = null;
            output.Text = await Task.Run(() => store.Restore(preview, () => ConfigSnapshotStore.ClosedGuard(this.game,
                Task.Run(ProcessSnapshot.Capture).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult())));
            output.BringIntoView();
        }, false);

        var links = new StackPanel();
        selectedDetails.Children.Add(new Expander { Header = "Linked performance reports", Content = links, Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 7, 0, 8) });
        links.Children.Add(reports);
        var reportActions = new WrapPanel(); links.Children.Add(reportActions);
        AddButton(reportActions, "Link report...", async () =>
        {
            var snapshot = Selected;
            var picker = new OpenFileDialog { Filter = "Saved performance report|*.md;*.txt" };
            if (picker.ShowDialog(this) != true) return;
            await Task.Run(() => store.Link(game.ExecutablePath, snapshot.Id, picker.FileName));
            Refresh();
        });
        string Report() => Selected.Reports.ElementAtOrDefault(reports.SelectedIndex) ?? throw new IOException("Select a linked report.");
        var open = AddButton(reportActions, "Open report", async () =>
        {
            var path = Report();
            var text = await Task.Run(() => ConfigSnapshotStore.ReadReport(path));
            openReport(text, Path.GetFileName(path));
        });
        var unlink = AddButton(reportActions, "Unlink", async () =>
        {
            var id = Selected.Id; var path = Report();
            await Task.Run(() => store.Link(game.ExecutablePath, id, path, true));
            Refresh();
        });
        open.Visibility = unlink.Visibility = Visibility.Collapsed;
        reports.SelectionChanged += (_, _) =>
        {
            unlink.Visibility = reports.SelectedIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
            open.Visibility = reports.SelectedIndex >= 0 && File.Exists(Report()) ? Visibility.Visible : Visibility.Collapsed;
        };
        snapshots.SelectionChanged += (_, _) =>
        {
            var snapshot = snapshots.SelectedItem as ConfigSnapshot;
            selectedDetails.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
            files.ItemsSource = snapshot?.Files.Select(file => file.Path).ToArray() ?? [];
            files.SelectedIndex = files.Items.Count == 1 ? 0 : -1;
            reports.ItemsSource = snapshot?.Reports.Select(path => path + (File.Exists(path) ? "" : " [missing]")).ToArray() ?? [];
            reports.Visibility = reports.HasItems ? Visibility.Visible : Visibility.Collapsed;
            UpdateComparisonTargets();
        };
        files.SelectionChanged += (_, _) => UpdateComparisonTargets();
        other.SelectionChanged += (_, _) => ClearComparison();
        snapshots.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(snapshots.HasItems)) { Source = snapshots, Converter = new BooleanToVisibilityConverter() });
        library.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(snapshots.HasItems)) { Source = snapshots, Converter = new BooleanToVisibilityConverter() });
        selectedDetails.Visibility = Visibility.Collapsed;
        Refresh(); RefreshSelection();
        Closing += (_, e) => { if (busy) { e.Cancel = true; output.Text += "\nWait for the current file operation to finish before closing."; } };
        Closed += (_, _) => closed = true;
    }

    Button AddButton(System.Windows.Controls.Panel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        button.Click += async (_, _) => await OperationAsync(action);
        panel.Children.Add(button);
        return button;
    }

    void UpdateComparisonTargets()
    {
        ClearComparison();
        var path = files.SelectedItem as string;
        var current = snapshots.SelectedItem as ConfigSnapshot;
        var targets = saved.Where(s => s.Id != current?.Id && s.Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new CompareTarget(s.Label, s.Id)).Prepend(new CompareTarget("Current file", null)).ToArray();
        other.ItemsSource = targets; other.SelectedIndex = 0;
        other.Visibility = path is not null && targets.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        compare.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        compare.Content = targets.Length > 1 ? "Compare" : "Compare with current";
    }

    void ClearComparison()
    {
        plan = null;
        restore.Visibility = Visibility.Collapsed;
        output.Clear();
    }

    async Task OperationAsync(Func<Task> action, bool clearPlan = true)
    {
        if (busy || closed) return;
        busy = true;
        ((UIElement)Content).IsEnabled = false;
        if (clearPlan) ClearComparison();
        try { await action(); }
        catch (Exception ex) { if (!closed) { output.Text = ex.Message; output.BringIntoView(); } }
        finally
        {
            busy = false;
            if (!closed)
            {
                ((UIElement)Content).IsEnabled = true;
                restore.Visibility = plan is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    void Refresh(Guid? preferred = null)
    {
        var id = preferred ?? (snapshots.SelectedItem as ConfigSnapshot)?.Id;
        saved = store.Snapshots(game.ExecutablePath).ToArray();
        snapshots.ItemsSource = saved;
        snapshots.SelectedItem = saved.FirstOrDefault(s => s.Id == id) ?? saved.FirstOrDefault();
    }
}

public partial class MainWindow
{
    private bool _openingConfigSnapshots;
    private readonly List<ConfigurationSnapshotsWindow> _configSnapshotWindows=[];
    private bool CloseConfigSnapshotsForExit()
    {
        if(_configSnapshotWindows.Any(w=>w.Busy)){StatusNotice.Text="Wait for the configuration file operation to finish before quitting.";return false;}
        foreach(var window in _configSnapshotWindows.ToArray())window.Close();
        return true;
    }
    private async void ConfigurationSnapshots_Click(object sender,RoutedEventArgs e)
    {
        if (SelectedGame(PerformanceGame) is not { } game) { PerformanceStatus.Text = "Select a configured game first."; return; }
        if(_openingConfigSnapshots)return;
        _openingConfigSnapshots=true;var captured=game.Copy();
        try{var store=await Task.Run(()=>new ConfigSnapshotStore(_dataDirectory));if(!_exiting){var window=new ConfigurationSnapshotsWindow(store,captured,(text,title)=>ShowReportTimeline(text,title)){Owner=this};_configSnapshotWindows.Add(window);window.Closed+=(_,_)=>_configSnapshotWindows.Remove(window);window.Show();}}
        catch(Exception ex){PerformanceStatus.Text="Snapshot store unavailable; existing data retained. "+ex.Message;}
        finally{_openingConfigSnapshots=false;}
    }
}
