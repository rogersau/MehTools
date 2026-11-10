using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;

namespace JamesTools;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private CancellationTokenSource? _pipeCancellation;
    private string _pipeName = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "JamesTools-" + user;
        _instance = new Mutex(true, @"Local\JamesTools-" + user, out var isFirst);
        if (!isFirst)
        {
            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                    client.Connect(1500);
                    client.WriteByte(1);
                }
                catch (IOException) { }
                catch (TimeoutException) { }
            }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        _pipeCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(window, _pipeCancellation.Token);
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)) window.Show();
    }

    private async Task ListenForActivationAsync(MainWindow window, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                await Dispatcher.InvokeAsync(window.ShowFromTray);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(300, cancellationToken); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancellation?.Cancel();
        _pipeCancellation?.Dispose();
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }
}
