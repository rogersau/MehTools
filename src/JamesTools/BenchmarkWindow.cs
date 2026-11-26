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
    readonly Button runButton = new(), openReports = new() { Content = "Open reports" };
    readonly ActionMenu runActions = new() { Content = "Run actions", Visibility = Visibility.Collapsed };
    readonly TextBlock recipeLabel = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 2) };
    readonly TextBlock instructions = new() { TextWrapping = TextWrapping.Wrap };
    readonly Expander recipeEditor = new() { Header = "Recipe settings", Padding = new Thickness(0, 7, 0, 0) };
    readonly Expander savedRuns = new() { Header = "Saved runs", Padding = new Thickness(0, 7, 0, 0), Margin = new Thickness(0, 10, 0, 0) };
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
        void Field(System.Windows.Controls.Panel parent, string label, UIElement control) { parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); parent.Children.Add(control); }
        void Perform(Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
        panel.Children.Add(recipeLabel); panel.Children.Add(recipes);
        recipes.DisplayMemberPath = "Name";
        panel.Children.Add(instructions);
        runButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var runControls = new WrapPanel(); panel.Children.Add(runControls);
        runButton.SetResourceReference(StyleProperty, "AccentButton"); runControls.Children.Add(runButton);
        runControls.Children.Add(runActions);
        runActions.Add("End this benchmark", (_, _) => { if (!Running) { active = null; Refresh(); status.Text = "Completed runs are in Saved runs."; } });
        runButton.Click += (_, _) => Perform(() =>
        {
            if (Running) { cancel?.Cancel(); runButton.IsEnabled = false; return; }
            if (active?.Runs.Any(r => r.Status == "Ready") != true)
            {
                if (recipes.SelectedItem is not BenchmarkRecipe recipe) return;
                var next = new BenchmarkSet(Guid.NewGuid(), recipe with { }, DateTime.UtcNow,
                    Enumerable.Range(1, recipe.Runs).Select(i => new BenchmarkRun(Guid.NewGuid(), i)).ToArray());
                store.Put(next); active = next;
                Refresh();
            }
            currentRun = Run();
        });
        panel.Children.Add(status);
        var recipeFields = new StackPanel(); recipeEditor.Content = recipeFields; panel.Children.Add(recipeEditor);
        Field(recipeFields, "Recipe name", name);
        var tuning = new StackPanel();
        recipeFields.Children.Add(new Expander { Header = "Setup instructions and timing", Content = tuning, Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 8, 0, 4) });
        Field(tuning, "Setup instructions", scene); Field(tuning, "Settings used", settings);
        Field(tuning, "Warm-up (0–300 seconds)", warm); Field(tuning, "Capture (10–600 seconds)", duration); Field(tuning, "Runs (1–10)", count);
        var recipeActions = new WrapPanel(); recipeFields.Children.Add(recipeActions);
        var saveRecipe = new Button { Content = "Save recipe" }; recipeActions.Children.Add(saveRecipe);
        saveRecipe.Click += (_, _) => Perform(() =>
        {
            var r = new BenchmarkRecipe((recipes.SelectedItem as BenchmarkRecipe)?.Id ?? Guid.NewGuid(), gameKey, name.Text, scene.Text, settings.Text,
                int.Parse(warm.Text), int.Parse(duration.Text), int.Parse(count.Text)).Validate();
            store.Save(store.Data with { Recipes = store.Data.Recipes.Where(x => x.Id != r.Id).Append(r).ToArray() });
            Refresh(r.Id); recipeEditor.IsExpanded = false;
        });
        var moreRecipes = new ActionMenu { Content = "Recipe actions" }; recipeActions.Children.Add(moreRecipes);
        moreRecipes.Add("New recipe", (_, _) => { recipes.SelectedItem = null; name.Clear(); scene.Clear(); settings.Clear(); name.Focus(); });
        moreRecipes.Add("Delete recipe", (_, _) => Perform(() =>
        {
            if (recipes.SelectedItem is not BenchmarkRecipe r) return;
            store.Save(store.Data with { Recipes = store.Data.Recipes.Where(x => x.Id != r.Id).ToArray() });
            Refresh();
        }));
        recipes.SelectionChanged += (_, _) =>
        {
            if (recipes.SelectedItem is BenchmarkRecipe r)
            {
                name.Text = r.Name; scene.Text = r.Instructions; settings.Text = r.Settings;
                warm.Text = r.WarmupSeconds.ToString(); duration.Text = r.CaptureSeconds.ToString(); count.Text = r.Runs.ToString();
            }
            moreRecipes.Visibility = recipes.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateRunButton();
        };
        moreRecipes.Visibility = Visibility.Collapsed;

        var previous = new StackPanel(); savedRuns.Content = previous; panel.Children.Add(savedRuns);
        previous.Children.Add(history); history.DisplayMemberPath = "StartedUtc";
        history.SelectionChanged += (_, _) =>
        {
            var set = history.SelectedItem as BenchmarkSet;
            results.Text = set is null ? "" : set.Recipe.Name + "\n" + set.Recipe.Instructions + "\n" + set.Recipe.Settings + "\n" + BenchmarkResults.Describe(set);
            openReports.Visibility = set?.Runs.Any(r => r.ReportPath is not null) == true ? Visibility.Visible : Visibility.Collapsed;
        };
        previous.Children.Add(results); previous.Children.Add(openReports);
        openReports.Visibility = Visibility.Collapsed;
        openReports.Click += (_, _) => Perform(() =>
        {
            if (history.SelectedItem is BenchmarkSet set)
                foreach (var path in set.Runs.Select(r => r.ReportPath).OfType<string>()) open(path);
        });
        timer.Tick += (_, _) => { if (Running) status.Text = $"Run {active?.Runs.FirstOrDefault(r => r.Status is "Warming" or "Capturing")?.Index}: {runner.Phase}; elapsed {runner.Elapsed:0.0}s; remaining {runner.Remaining:0.0}s. Cancel is available."; };
        timer.Start(); Refresh(); recipeEditor.IsExpanded = !recipes.HasItems; status.Text = store.Notice ?? "";
        Closing += (_, e) => { if (Running) { e.Cancel = true; closing = true; cancel?.Cancel(); } };
        Closed += (_, _) => { closed = true; timer.Stop(); };
    }
    void Refresh(Guid? recipeId = null)
    {
        var data = store.Data;
        recipeId ??= (recipes.SelectedItem as BenchmarkRecipe)?.Id;
        var choices = data.Recipes.Where(r => r.GameKey == gameKey).ToArray();
        recipes.ItemsSource = choices;
        recipes.SelectedItem = choices.FirstOrDefault(r => r.Id == recipeId) ?? choices.FirstOrDefault();
        var setId = active?.Id ?? (history.SelectedItem as BenchmarkSet)?.Id;
        var sets = data.Sets.Where(s => s.Recipe.GameKey == gameKey).Reverse().ToArray();
        history.ItemsSource = sets;
        history.SelectedItem = sets.FirstOrDefault(s => s.Id == setId) ?? sets.FirstOrDefault();
        savedRuns.Visibility = sets.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRunButton();
    }

    void UpdateRunButton()
    {
        var next = active?.Runs.FirstOrDefault(r => r.Status == "Ready");
        var recipe = next is null ? recipes.SelectedItem as BenchmarkRecipe : active!.Recipe;
        bool chooseRecipe = recipes.Items.Count > 1 || recipes.HasItems && recipe is null;
        recipes.Visibility = chooseRecipe ? Visibility.Visible : Visibility.Collapsed;
        recipeLabel.Visibility = recipes.HasItems ? Visibility.Visible : Visibility.Collapsed;
        recipeLabel.Text = chooseRecipe ? "Benchmark recipe" : recipe?.Name;
        runButton.Visibility = Running || recipe is not null ? Visibility.Visible : Visibility.Collapsed;
        runButton.IsEnabled = true;
        runButton.Content = Running ? "Cancel run" : next is null ? "Start benchmark" : $"Start run {next.Index}";
        runActions.Visibility = !Running && next is not null ? Visibility.Visible : Visibility.Collapsed;
        recipes.IsEnabled = recipeEditor.IsEnabled = !Running && next is null;
        instructions.Text = recipe is null ? "Save a recipe to run a benchmark."
            : string.Join(Environment.NewLine, new[] { "Set up the scene before starting.", recipe.Instructions, recipe.Settings }.Where(text => text.Length > 0));
    }
    async Task Run()
    {
        if (Running || active == null) return;
        var r = active.Runs.FirstOrDefault(r => r.Status == "Ready"); if (r == null) return;
        cancel = new(); UpdateRunButton();
        try
        {
            active = active with { Runs = active.Runs.Select(x => x.Id == r.Id ? r with { Status = "Warming" } : x).ToArray() }; store.Put(active);
            var result = await capture(active.Recipe, r, runner, checkpoint => { active = active with { Runs = active.Runs.Select(x => x.Id == r.Id ? checkpoint : x).ToArray() }; store.Put(active); }, cancel.Token);
            active = active with { Runs = active.Runs.Select(x => x.Id == r.Id ? result : x).ToArray() }; store.Put(active);
            if (!closed) { Refresh(); savedRuns.IsExpanded = true; status.Text = active.Runs.Any(x => x.Status == "Ready") ? "Run finished. Reset the scene before the next run. " + result.Reason : "Benchmark finished. " + result.Reason; }
        }
        catch (Exception ex) { if (!closed) { status.Text = "Run could not finish/save: " + ex.Message; if (active != null) results.Text = BenchmarkResults.Describe(active); } }
        finally { cancel.Dispose(); cancel = null; if (!closed) UpdateRunButton(); if (closing && !closed) _ = Dispatcher.BeginInvoke(new Action(() => { if (!closed) Close(); })); }
    }
}
