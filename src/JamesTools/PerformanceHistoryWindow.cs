using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using JamesTools.Core.Diagnostics;

namespace JamesTools;
public sealed class PerformanceHistoryWindow : Window
{
    readonly PerformanceHistoryStore store;
    readonly string game;
    readonly Action<string, string> open;
    readonly ComboBox groups = new() { MinWidth = 180, DisplayMemberPath = "Name" };
    readonly ListBox reports = new() { MinHeight = 140, DisplayMemberPath = "Label" };
    readonly TextBox name = new() { MinWidth = 140, ToolTip = "Group name" };
    readonly TextBox declaration = new() { MinWidth = 240, ToolTip = "Common scene, settings and purpose" };
    readonly TextBox notes = new() { AcceptsReturn = true, Height = 55, ToolTip = "User annotation, separate from captured facts" };
    readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 210 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly ComboBox metric = new() { ItemsSource = new[] { "Mean ms", "P99 ms", "Stutters", "Stutters/1000 frames", "CPU %", "GPU %", "RAM MiB" }, SelectedIndex = 0 };
    sealed record Entry(HistoryReport Report, string Label);
    HistoryReport? Selected => (reports.SelectedItem as Entry)?.Report;
    readonly Canvas chart = new() { Height = 100, Background = Brushes.WhiteSmoke };
    readonly Dictionary<Guid, string> verification = [];
    HistoryReport? baseline;
    bool busy, closed, reloading;
    DateTime? verifiedUtc;
    public PerformanceHistoryWindow(PerformanceHistoryStore store, string game, string title, (string Path, string Reference, DateTime Observed)[] sources, Action<string, string> open)
    {
        this.store = store; this.game = game; this.open = open;
        Title = title + " — Performance history"; Width = 1050; Height = 850;
        var panel = new StackPanel { Margin = new Thickness(14) }; Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = "Select an explicit comparison group. Declarations describe workload; differences do not establish cause.", TextWrapping = TextWrapping.Wrap });
        var bar = new WrapPanel(); panel.Children.Add(bar); bar.Children.Add(groups); bar.Children.Add(name); bar.Children.Add(declaration);
        Button(bar, "New group", () => { groups.SelectedItem = null; name.Text = ""; declaration.Text = ""; return Task.CompletedTask; });
        Button(bar, "Save/new group", async () => { var g = groups.SelectedItem as HistoryGroup; var next=new HistoryGroup(g?.Id ?? Guid.NewGuid(),game,name.Text,declaration.Text,g?.Members ?? []); await Change(d => d with { Groups = d.Groups.Where(x => x.Id != next.Id).Append(next).ToArray() }); await Reload(next.Id); });
        Button(bar, "Delete group", async () => { if (groups.SelectedItem is HistoryGroup g) await Change(d => d with { Groups = d.Groups.Where(x => x.Id != g.Id).ToArray() }); await Reload(); });
        panel.Children.Add(reports);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        Button(actions, "Import saved report", async () => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Saved reports|*.md;*.txt|All files|*.*" }; if (picker.ShowDialog(this) == true) { var r = await PerformanceHistoryStore.IndexAsync(game, picker.FileName, "Explicit import", DateTime.UtcNow); if(closed)return; await Change(d => d with { Reports = d.Reports.Append(r).ToArray() }); await Reload(); } });
        Button(actions, "Add/remove membership", async () => { if (groups.SelectedItem is not HistoryGroup g || Selected is not HistoryReport r) throw new InvalidOperationException("Select a group and a report first."); await Change(d => d with { Groups = d.Groups.Select(x => x.Id != g.Id ? x : x with { Members = x.Members.Contains(r.Id) ? x.Members.Where(id => id != r.Id).ToArray() : x.Members.Append(r.Id).ToArray() }).ToArray() }); await Reload(); });
        Button(actions, "Set baseline", () => { if(groups.SelectedItem is not HistoryGroup g || Selected is not HistoryReport r || !g.Members.Contains(r.Id) || verification.GetValueOrDefault(r.Id)!="Verified" || !PerformanceHistoryStore.FrameValid(r.Summary)) throw new InvalidOperationException("Choose a verified member of this group with supported frame data."); baseline = r; Render(); return Task.CompletedTask; });
        Button(actions, "Clear baseline",()=>{baseline=null;Render();return Task.CompletedTask;});
        Button(actions, "Verify sources", ()=>Reload());
        Button(actions, "Reindex new version", async () => { if (Selected is HistoryReport r) { var next = await PerformanceHistoryStore.IndexAsync(game, r.Path, r.Reference, DateTime.UtcNow, r.Id); if(closed)return; await Change(d => d with { Reports = d.Reports.Append(next).ToArray() }); await Reload(); } });
        Button(actions, "Open timeline", async () => { if (Selected is HistoryReport r) { var text = await PerformanceHistoryStore.ReadVerifiedAsync(r); if (!closed) open(text, System.IO.Path.GetFileName(r.Path)); } });
        panel.Children.Add(new TextBlock { Text = "User annotation (driver update, graphics changes, OBS on/off):" }); panel.Children.Add(notes);
        Button(panel, "Save annotation", async () => { var annotation=notes.Text; if (Selected is HistoryReport r) await Change(d => d with { Reports = d.Reports.Select(x => x.Id == r.Id ? x with { Notes = annotation } : x).ToArray() }); await Reload(); });
        panel.Children.Add(metric); metric.SelectionChanged += (_, _) => Render();
        panel.Children.Add(chart); panel.Children.Add(details); panel.Children.Add(status);
        reports.SelectionChanged += (_, _) => { if(reloading)return; notes.Text = (Selected)?.Notes ?? ""; Render(); };
        groups.SelectionChanged += (_, _) => { if(reloading)return; baseline=null; if (groups.SelectedItem is HistoryGroup g) { name.Text = g.Name; declaration.Text = g.Declaration; } Render(); };
        chart.SizeChanged+=(_,_)=>Render();
        Closed += (_, _) => closed = true;
        Loaded += async (_, _) => await Run(async () =>
        {
            var known=store.Snapshot().Reports.Where(r=>r.Game==game).Select(r=>r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions=new List<HistoryReport>();
            foreach (var source in sources)
            {
                if(closed)return;
                if (!known.Add(source.Path)) continue;
                var r = await PerformanceHistoryStore.IndexAsync(game, source.Path, source.Reference, source.Observed);
                additions.Add(r);
            }
            if(closed)return;
            if(additions.Count>0)await Change(d=>d with {Reports=[..d.Reports,..additions]});
            await Reload();
        });
    }
    void Button(Panel panel, string label, Func<Task> action) { var b = new Button { Content = label, Margin = new Thickness(3) }; b.Click += async (_, _) => await Run(action); panel.Children.Add(b); }
    async Task Run(Func<Task> action)
    {
        if (busy || closed) return; busy = true; IsEnabled = false;
        try { await action(); } catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        finally { busy = false; if (!closed) IsEnabled = true; }
    }
    Task Change(Func<HistoryData,HistoryData> change)=>Task.Run(()=>store.Change(change));
    async Task Reload(Guid? preferredGroup=null)
    {
        if(closed)return;
        var snapshot = store.Snapshot(); var rows = snapshot.Reports.Where(r => r.Game == game).OrderBy(r => r.SortTime).ThenBy(r=>r.Id).ToArray();
        var checks = new List<(Guid Id, string Status)>();
        foreach (var row in rows) { if (closed) return; checks.Add((row.Id, await PerformanceHistoryStore.VerifyAsync(row))); }
        if (closed) return;
        verifiedUtc=DateTime.UtcNow;
        foreach (var check in checks) verification[check.Id] = check.Status;
        var groupId = preferredGroup ?? (groups.SelectedItem as HistoryGroup)?.Id; var reportId = Selected?.Id;
        reloading=true;
        groups.ItemsSource = snapshot.Groups.Where(g => g.Game == game).ToArray(); groups.SelectedItem = snapshot.Groups.FirstOrDefault(g => g.Id == groupId);
        var entries = rows.Select(r => new Entry(r, r.Date + " | " + System.IO.Path.GetFileName(r.Path) + " | " + verification.GetValueOrDefault(r.Id) + " | " + (r.Notes.Length > 0 ? "annotated" : ""))).ToArray();
        reports.ItemsSource = entries; reports.SelectedItem = entries.FirstOrDefault(r => r.Report.Id == reportId);
        reloading=false;notes.Text=Selected?.Notes ?? "";
        if(groups.SelectedItem is HistoryGroup selectedGroup){name.Text=selectedGroup.Name;declaration.Text=selectedGroup.Declaration;}else baseline=null;
        Render();
    }
    static string F(double? n) => n is double v && double.IsFinite(v) ? v.ToString("0.###") : "unavailable";
    void Render()
    {
        if (closed || reloading) return;
        var group = groups.SelectedItem as HistoryGroup;
        var all = store.Snapshot().Reports.Where(r => r.Game == game).ToArray();
        var text = new System.Text.StringBuilder(); chart.Children.Clear();
        chart.Children.Add(new TextBlock { Text = $"{metric.SelectedItem}; report order →", Foreground = Brushes.Black });
        text.AppendLine("Group table: date | mean ms | P99 ms | stutters | per 1000 frames | CPU % | GPU % | RAM MiB | status");
        var allMembers = all.Where(r => group?.Members.Contains(r.Id) == true).OrderBy(r=>r.SortTime).ThenBy(r=>r.Id).ToArray();
        var rows=allMembers.TakeLast(200).ToArray();
        if(allMembers.Length>rows.Length)text.AppendLine($"Showing the latest {rows.Length} of {allMembers.Length} group reports. All reports remain in the membership list.");
        var eligible = rows.Where(r => verification.GetValueOrDefault(r.Id) == "Verified" && PerformanceHistoryStore.FrameValid(r.Summary)).ToArray();
        var anchor = baseline is null ? eligible.FirstOrDefault() : allMembers.FirstOrDefault(r => r.Id == baseline.Id && verification.GetValueOrDefault(r.Id)=="Verified" && PerformanceHistoryStore.FrameValid(r.Summary));
        double? Value(ReportMachineSummary s) => metric.SelectedIndex switch { 1 => s.FrameP99Ms, 2 => PerformanceHistoryStore.StutterRate(s) is not null ? s.FramesOver50Ms : null, 3 => PerformanceHistoryStore.StutterRate(s), 4 => s.CpuAverage, 5 => s.GpuAverage, 6 => s.MemoryAverageMiB, _ => s.FrameMsAverage };
        var max = Math.Max(1, eligible.Where(r=>anchor is not null && PerformanceHistoryStore.Compatible(anchor.Summary,r.Summary)).Select(r => Value(r.Summary)).Where(v => v is >= 0 && double.IsFinite(v.Value)).Select(v => v!.Value).DefaultIfEmpty(1).Max());
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; var s = r.Summary;
            var valid = anchor != null && eligible.Any(e => e.Id == r.Id) && PerformanceHistoryStore.Compatible(anchor.Summary, s);
            text.AppendLine($"{r.Date} | {F(s.FrameMsAverage)} | {F(s.FrameP99Ms)} | {s.FramesOver50Ms?.ToString() ?? "unavailable"} | {F(PerformanceHistoryStore.StutterRate(s))} | {F(s.CpuAverage)} | {F(s.GpuAverage)} | {F(s.MemoryAverageMiB)} | {verification.GetValueOrDefault(r.Id)}; {(valid ? "compatible" : "excluded from frame trend/deltas")}");
            if (valid && Value(s) is double point && double.IsFinite(point) && point >= 0)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.SteelBlue,ToolTip=r.Date+"\n"+System.IO.Path.GetFileName(r.Path)+"\n"+metric.SelectedItem+": "+F(point) }; Canvas.SetLeft(dot, 58 + i * Math.Max(1,chart.ActualWidth-78) / Math.Max(1, rows.Length-1)); Canvas.SetTop(dot, 85 - point / max * 70); chart.Children.Add(dot);
            }
            if(valid)
            {
                if (baseline != null) text.AppendLine($"  Δ vs baseline: mean {F(PerformanceHistoryStore.Difference(s.FrameMsAverage, anchor!.Summary.FrameMsAverage))} ms; P99 {F(PerformanceHistoryStore.Difference(s.FrameP99Ms, anchor.Summary.FrameP99Ms))} ms; stutters {F(PerformanceHistoryStore.Difference(s.FramesOver50Ms, anchor.Summary.FramesOver50Ms))}; /1000 frames {F(PerformanceHistoryStore.Difference(PerformanceHistoryStore.StutterRate(s), PerformanceHistoryStore.StutterRate(anchor.Summary)))}; CPU {F(PerformanceHistoryStore.Difference(s.CpuAverage, anchor.Summary.CpuAverage))}; GPU {F(PerformanceHistoryStore.Difference(s.GpuAverage, anchor.Summary.GpuAverage))}; RAM {F(PerformanceHistoryStore.Difference(s.MemoryAverageMiB, anchor.Summary.MemoryAverageMiB))} MiB");
            }
        }
        text.AppendLine($"Chart scale: 0–{F(max)} {metric.SelectedItem}; baseline: {baseline?.Id.ToString() ?? "none"}");
        if (Selected is HistoryReport selected)
        {
            var s=selected.Summary;
            text.AppendLine($"\nSelected: {selected.Date}\n{selected.Path}\nSource: {selected.Reference}\nStatus: {verification.GetValueOrDefault(selected.Id)}\nDuration: {F(s.DurationSeconds)}s; hardware samples: {s.SystemSampleCount?.ToString() ?? "unavailable"}; frames: {s.FrameCount?.ToString() ?? "unavailable"}\nFPS: {F(s.FpsAverage)}; method: {s.FpsMethod ?? "unavailable"}; streams: {s.FrameStreamCount?.ToString() ?? "unknown"}; selected stream: {s.FrameStream ?? "unknown"}\nDriver: {s.DriverVersion ?? "Unknown"}; recording: {selected.RecordingContext}\nDeclared scene/settings: {s.Context ?? "not provided"}\nUser notes: {selected.Notes}\nVersion: {selected.Id}; previous: {selected.PreviousVersion?.ToString() ?? "none"}");
        }
        details.Text = text.ToString();
        status.Text = $"Sources last verified: {verifiedUtc:u}. Dots: selected whole-report metric, ordered by validated capture date (observed/imported time if unavailable); no interpolation. All game reports remain listed for membership management. Only verified single-stream supported frame methods with matching network configuration enter trends. Stream IDs can change between sessions; grouping is a user declaration, not causal evidence. Recording context: Unknown. Old versions and notes survive reindex/group deletion; new versions require explicit membership.";
    }
}
