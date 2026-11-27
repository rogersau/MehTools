using System.IO;
using System.Windows;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MehTools;component/Theme.xaml", UriKind.Relative) });
        int result = 0;
        var errors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        string data = Path.Combine(Path.GetTempPath(), "MehTools-ui-" + Guid.NewGuid().ToString("N"));
        _ = app.Dispatcher.InvokeAsync(async () => {
            try {
                await UiAudit.Run(data, args);
                await SteamWardogsUiTests.Run(data);
                await ObsReconnectUiTests.Run(data);
                await InvestigationUiTests.Run(args.Contains("--investigation-render"));
                if (args.Contains("--presentmon-integration")) await DiagnosticIntegration.Run();
                if (errors.Count > 0) throw new AggregateException("UI audit raised dispatcher exceptions", errors);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
        if (errors.Count > 0) result = 1;
        string ownedDirectory = Path.GetFullPath(data);
        if (Directory.Exists(ownedDirectory) && Directory.GetParent(ownedDirectory)?.FullName == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(ownedDirectory).StartsWith("MehTools-ui-", StringComparison.Ordinal)) Directory.Delete(ownedDirectory, true);
        return result;
    }
}
