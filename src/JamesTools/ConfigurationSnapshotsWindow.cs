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
    readonly ComboBox other=new(){MinWidth=200};
    readonly TextBox name=new(){Text="Configuration snapshot"}, output=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    RestorePlan? plan;
    bool busy,closed;
    public bool Busy=>busy;
    ConfigSnapshot Selected => snapshots.SelectedItem as ConfigSnapshot ?? throw new IOException("Select a snapshot.");
    string FilePath => files.SelectedItem as string ?? throw new IOException("Select one snapshot file.");
    public ConfigurationSnapshotsWindow(ConfigSnapshotStore store,GameRule game,Action<string,string> openReport)
    {
        this.store=store; this.game=game.Copy();this.openReport=openReport;
        WindowTheme.Apply(this);
        Title ="Configuration snapshots — "+game.Name;Width=950;Height=850;MinWidth=600;MinHeight=500;
        var top=new StackPanel{Margin=new Thickness(12)};Content=new ScrollViewer{Content=top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        output.Height=300;
        top.Children.Add(new TextBlock{Text="Explicit local text files only • 10 files, 1 MiB each • restore one file while game is closed",TextWrapping=TextWrapping.Wrap});
        var selection=new TextBlock{TextWrapping=TextWrapping.Wrap};top.Children.Add(selection);
        void RefreshSelection()=>selection.Text="Current allowlist: "+string.Join("; ",store.Selection(game.ExecutablePath));
        Button Button(string title,Func<Task> action) {var b=new Button{Content=title,Margin=new Thickness(2)};b.Click+=async(_,_)=>await OperationAsync(action);return b;}
        top.Children.Add(Button("Select/replace config allowlist…",async()=>{var d=new OpenFileDialog{Multiselect=true,Filter="Text configurations|*.ini;*.cfg;*.conf;*.json;*.xml;*.yaml;*.yml;*.toml;*.txt|All files (unsupported formats rejected)|*.*"};if(d.ShowDialog(this)==true){await Task.Run(()=>store.Select(game.ExecutablePath,d.FileNames));RefreshSelection();}}));
        void Label(string text) => top.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 5), TextWrapping = TextWrapping.Wrap });
        Label("Snapshot name");top.Children.Add(name);top.Children.Add(Button("Create immutable snapshot",async()=>{var label=name.Text;var created=await Task.Run(()=>store.Create(game.ExecutablePath,label));Refresh(created.Id);}));
        snapshots.DisplayMemberPath="Label";Label("Saved snapshots");top.Children.Add(snapshots);Label("Files in selected snapshot");top.Children.Add(files);
        snapshots.SelectionChanged+=(_,_)=>{plan=null;if(snapshots.SelectedItem is ConfigSnapshot s){files.ItemsSource=s.Files.Select(f=>f.Path).ToArray();reports.ItemsSource=s.Reports.Select(p=>p+(System.IO.File.Exists(p)?"":" [missing]")).ToArray();output.Text=$"{s.CreatedUtc:u} • {s.Id}";}else{files.ItemsSource=null;reports.ItemsSource=null;}};
        files.SelectionChanged+=(_,_)=>plan=null;
        top.Children.Add(new TextBlock{Text="Compare against current allowlist file, or select another snapshot:",TextWrapping=TextWrapping.Wrap});other.DisplayMemberPath="Label";top.Children.Add(other);
        var compare = new Button{Content="Compare with current / prepare restore"}; top.Children.Add(compare);
        compare.Click+=async(_,_)=>await OperationAsync(async()=>{var s=Selected;var path=FilePath;var p=await Task.Run(()=>store.Preview(game.ExecutablePath,s.Id,path));output.Text=$"Target: {p.Path}\nSnapshot: {s.Name} ({s.Id}) {s.CreatedUtc:u}\nCurrent SHA256: {p.CurrentHash}\nSnapshot SHA256: {p.SnapshotHash}\n{p.Difference}";plan=p;output.BringIntoView();});
        var compareOther=new Button{Content="Compare with other snapshot"};top.Children.Add(compareOther);
        compareOther.Click+=async(_,_)=>await OperationAsync(async()=>{var id=Selected.Id;var path=FilePath;var otherId=(other.SelectedItem as ConfigSnapshot ?? throw new IOException("Select comparison snapshot.")).Id;output.Text=await Task.Run(()=>store.Compare(game.ExecutablePath,id,path,otherId));output.BringIntoView();});
        Label("Comparison and restore preview");top.Children.Add(output);
        var restore=new Button{Content="Restore reviewed file…",Margin=new Thickness(2)};top.Children.Add(restore);
        restore.Click+=async(_,_)=>await OperationAsync(async()=>{var p=plan ?? throw new IOException("Prepare a current comparison first.");if(MessageBox.Show(this,$"Restore ONE file?\n{p.Path}\nSnapshot {p.Snapshot}\nHash {p.SnapshotHash}\nClose the game first. A verified backup will be retained.","Confirm configuration restore",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;plan=null;output.Text=await Task.Run(()=>store.Restore(p,()=>ConfigSnapshotStore.ClosedGuard(this.game,Task.Run(ProcessSnapshot.Capture).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult())));output.BringIntoView();},false);
        Label("Linked performance reports");top.Children.Add(reports);
        top.Children.Add(Button("Link saved report…",async()=>{var s=Selected;var d=new OpenFileDialog{Filter="Saved performance report|*.md;*.txt"};if(d.ShowDialog(this)==true){await Task.Run(()=>store.Link(game.ExecutablePath,s.Id,d.FileName));Refresh();}}));
        string Report()=>Selected.Reports.ElementAtOrDefault(reports.SelectedIndex) ?? throw new IOException("Select a linked report.");
        top.Children.Add(Button("Unlink selected report",async()=>{var id=Selected.Id;var path=Report();await Task.Run(()=>store.Link(game.ExecutablePath,id,path,true));Refresh();}));
        top.Children.Add(Button("Open linked report",async()=>{var p=Report();var text=await Task.Run(()=>ConfigSnapshotStore.ReadReport(p));openReport(text,Path.GetFileName(p));}));
        Refresh();RefreshSelection();
        Closing+=(_,e)=>{if(busy){e.Cancel=true;output.Text+="\nWait for the current file operation to finish before closing.";}};
        Closed+=(_,_)=>closed=true;
    }
    async Task OperationAsync(Func<Task> action,bool clearPlan=true)
    {
        if(busy || closed)return;
        busy=true;((UIElement)Content).IsEnabled=false;if(clearPlan)plan=null;
        try{await action();}catch(Exception ex){if(!closed)output.Text=ex.Message;}
        finally{busy=false;if(!closed)((UIElement)Content).IsEnabled=true;}
    }
    void Refresh(Guid? preferred=null){var id=preferred ?? (snapshots.SelectedItem as ConfigSnapshot)?.Id;var list=store.Snapshots(game.ExecutablePath);snapshots.ItemsSource=list;other.ItemsSource=list;snapshots.SelectedItem=list.FirstOrDefault(s=>s.Id==id);}
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
        if(PerformanceGame.SelectedItem is not GameRule game){PerformanceStatus.Text="Select a configured game first.";return;}
        if(_openingConfigSnapshots)return;
        _openingConfigSnapshots=true;var captured=game.Copy();
        try{var store=await Task.Run(()=>new ConfigSnapshotStore(_dataDirectory));if(!_exiting){var window=new ConfigurationSnapshotsWindow(store,captured,(text,title)=>ShowReportTimeline(text,title)){Owner=this};_configSnapshotWindows.Add(window);window.Closed+=(_,_)=>_configSnapshotWindows.Remove(window);window.Show();}}
        catch(Exception ex){PerformanceStatus.Text="Snapshot store unavailable; existing data retained. "+ex.Message;}
        finally{_openingConfigSnapshots=false;}
    }
}
