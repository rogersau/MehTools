using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JamesTools.Core.Diagnostics;

using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
namespace JamesTools;

public sealed class BenchmarkWindow : Window
{
    readonly BenchmarkStore store;
    readonly string gameKey;
    readonly Func<BenchmarkRecipe, BenchmarkRun, BenchmarkRunner, Action<BenchmarkRun>, CancellationToken, Task<BenchmarkRun>> capture;
    readonly Action<string> open;
    readonly ComboBox recipes = new(), history = new();
    readonly TextBox name = new(), scene = new() { AcceptsReturn = true, Height = 65 }, settings = new() { AcceptsReturn = true, Height = 65 };
    readonly TextBox warm = new() { Text = "10" }, duration = new() { Text = "60" }, count = new() { Text = "3" };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox results = new() { IsReadOnly = true, AcceptsReturn = true, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly BenchmarkRunner runner = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? cancel;
    BenchmarkSet? active;
    bool closing, closed;
    Task currentRun = Task.CompletedTask;
    public bool Running => cancel != null;
    public async Task CancelForExitAsync()
    {
        closing = true;
        cancel?.Cancel();
        await currentRun;
        if (!closed) Close();
    }
    public BenchmarkWindow(BenchmarkStore store, string gameKey, string gameName,
        Func<BenchmarkRecipe, BenchmarkRun, BenchmarkRunner, Action<BenchmarkRun>, CancellationToken, Task<BenchmarkRun>> capture, Action<string> open)
    {
        this.store = store; this.gameKey = gameKey; this.capture = capture; this.open = open;
        WindowTheme.Apply(this);
        Title = "Benchmark recipes — " + gameName; Width = 680; Height = 850; MinWidth = 520; MinHeight = 480;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = new ScrollViewer { Content = panel };
        void Field(string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); panel.Children.Add(control); }
        void Button(string label, Action action) { var b = new Button { Content = label, Margin = new Thickness(0, 4, 8, 4) }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }; var bar = panel.Children.Count > 0 ? panel.Children[panel.Children.Count - 1] as WrapPanel : null; if (bar is null) { bar = new WrapPanel(); panel.Children.Add(bar); } bar.Children.Add(b); }
        Field("Saved recipes (edits take effect only after Save)", recipes);
        recipes.SelectionChanged += (_, _) => { if (recipes.SelectedItem is BenchmarkRecipe r) { name.Text = r.Name; scene.Text = r.Instructions; settings.Text = r.Settings; warm.Text = r.WarmupSeconds.ToString(); duration.Text = r.CaptureSeconds.ToString(); count.Text = r.Runs.ToString(); } };
        recipes.DisplayMemberPath = "Name";
        Button("New recipe", () => { recipes.SelectedItem = null; name.Clear(); scene.Clear(); settings.Clear(); });
        Field("Name", name); Field("Scene / manual setup instructions", scene); Field("Settings context (declared, not verified)", settings);
        Field("Warm-up seconds (0–300)", warm); Field("Timed capture seconds (10–600)", duration); Field("Runs (1–10)", count);
        Button("Save recipe", () => { var r = new BenchmarkRecipe((recipes.SelectedItem as BenchmarkRecipe)?.Id ?? Guid.NewGuid(), gameKey, name.Text, scene.Text, settings.Text, int.Parse(warm.Text), int.Parse(duration.Text), int.Parse(count.Text)).Validate(); store.Save(store.Data with { Recipes = store.Data.Recipes.Where(x => x.Id != r.Id).Append(r).ToArray() }); Refresh(); recipes.SelectedItem = r; });
        Button("Delete recipe (keeps results)", () => { if (recipes.SelectedItem is BenchmarkRecipe r) { store.Save(store.Data with { Recipes = store.Data.Recipes.Where(x => x.Id != r.Id).ToArray() }); Refresh(); } });
        Button("Begin run set from saved recipe", () =>
        {
            if (Running) return;
            if (recipes.SelectedItem is not BenchmarkRecipe r) throw new InvalidOperationException("Save/select a recipe first.");
            var next = new BenchmarkSet(Guid.NewGuid(), r with { }, DateTime.UtcNow, Enumerable.Range(1, r.Runs).Select(i => new BenchmarkRun(Guid.NewGuid(), i)).ToArray());
            store.Put(next); active=next; Refresh(); status.Text = "Set up the scene manually: " + r.Instructions + "\nSettings: " + r.Settings + "\nThen choose Ready / Start run.";
        });
        Button("Ready / Start next run", () => { if (!Running) currentRun = Run(); });
        Button("Cancel active run", () => cancel?.Cancel());
        panel.Children.Add(status);
        Field("Saved run sets (including partial runs)", history); history.DisplayMemberPath = "StartedUtc";
        history.SelectionChanged += (_, _) => { if (history.SelectedItem is BenchmarkSet s) results.Text = s.Recipe.Name + "\n" + s.Recipe.Instructions + "\n" + s.Recipe.Settings + "\n" + BenchmarkResults.Describe(s); };
        panel.Children.Add(results);
        Button("Open reports in selected set", () => { if (history.SelectedItem is BenchmarkSet s) foreach (var r in s.Runs.Where(r => r.ReportPath != null)) open(r.ReportPath!); });
        timer.Tick += (_, _) => { if (Running) status.Text = $"Run {active?.Runs.FirstOrDefault(r => r.Status is "Warming" or "Capturing")?.Index}: {runner.Phase}; elapsed {runner.Elapsed:0.0}s; remaining {runner.Remaining:0.0}s. Cancel is available."; };
        timer.Start(); Refresh(); status.Text = store.Notice ?? "Select or save a recipe. Set up and reset scenes manually; no game input is automated.";
        Closing += (_, e) => { if (Running) { e.Cancel = true; closing = true; cancel?.Cancel(); } };
        Closed += (_, _) => { closed = true; timer.Stop(); };
    }
    void Refresh() { var data=store.Data; recipes.ItemsSource = data.Recipes.Where(r => r.GameKey == gameKey).ToArray(); history.ItemsSource = data.Sets.Where(s => s.Recipe.GameKey == gameKey).Reverse().ToArray(); if (active != null) history.SelectedItem = data.Sets.FirstOrDefault(s => s.Id == active.Id); }
    async Task Run()
    {
        if (Running || active == null) return;
        var r = active.Runs.FirstOrDefault(r => r.Status == "Ready"); if (r == null) return;
        cancel = new();
        try
        {
            active = active with { Runs = active.Runs.Select(x => x.Id == r.Id ? r with { Status = "Warming" } : x).ToArray() }; store.Put(active);
            var result = await capture(active.Recipe, r, runner, checkpoint => { active = active with { Runs = active.Runs.Select(x => x.Id == r.Id ? checkpoint : x).ToArray() }; store.Put(active); }, cancel.Token);
            active = active with { Runs = active.Runs.Select(x => x.Id == r.Id ? result : x).ToArray() }; store.Put(active);
            if (!closed) { Refresh(); status.Text = "Run finished. Reset the scene manually before Ready / Start. " + result.Reason; }
        }
        catch (Exception ex) { if (!closed) { status.Text = "Run could not finish/save: " + ex.Message; if (active != null) results.Text = BenchmarkResults.Describe(active); } }
        finally { cancel.Dispose(); cancel = null; if (closing && !closed) _ = Dispatcher.BeginInvoke(new Action(() => { if (!closed) Close(); })); }
    }
}
