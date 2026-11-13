using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using JamesTools.Core.Automation;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Sessions;
namespace JamesTools;
public partial class MainWindow
{
    private const int RollingHotkeyId=0x4A58;
    private readonly object _rollingGate=new();
    private readonly RollingDiagnosticBuffer _rolling=new();
    private readonly Stopwatch _rollingClock=Stopwatch.StartNew();
    private CancellationTokenSource? _rollingCancellation;
    private Task? _rollingTask;
    private Task _rollingSaveTask = Task.CompletedTask;
    private long _rollingGeneration;
    private string? _rollingIdentity;
    private Guid? _rollingOwner;
    private int _rollingBefore, _rollingAfter;
    private bool _rollingActive, _rollingSaving, _rollingDeliveryPending;
    private string? _rollingFaultIdentity;
    private string _rollingHotkeyNotice = "";
    private string? _lastRolling;
    private bool RollingSettingsValid => _appliedSettings.RollingBeforeSeconds is >= 10 and <= 300 && _appliedSettings.RollingAfterSeconds is >= 0 and <= 60;
    private void SetRollingStatus(string message) => RollingStatus.Text = message + _rollingHotkeyNotice;
    private void ApplyRollingHotkey()
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd,RollingHotkeyId);
        _rollingHotkeyNotice = "";
        _rollingFaultIdentity = null; // Saving settings explicitly retries a failed sampler.
        if(_appliedSettings.RollingDiagnosticsEnabled && !string.IsNullOrWhiteSpace(_appliedSettings.RollingHotkey))
        {
            if(!TryParseHotkey(_appliedSettings.RollingHotkey,out var mod,out var key) || !RegisterHotKey(hwnd,RollingHotkeyId,mod|MOD_NOREPEAT,key))
                _rollingHotkeyNotice=" Hotkey unavailable: conflict or invalid key; use the button.";
        }
        if(!_appliedSettings.RollingDiagnosticsEnabled || !_appliedSettings.AutomationEnabled || !RollingSettingsValid ||
            _rollingBefore != _appliedSettings.RollingBeforeSeconds || _rollingAfter != _appliedSettings.RollingAfterSeconds) InvalidateRolling();
        else SetRollingStatus("Waiting for the next game detection update.");
    }
    private void InvalidateRolling()
    {
        RollingCapture? done;
        lock(_rollingGate) { _rollingActive=false; _rollingGeneration++; _rollingIdentity=null; done=_rolling.Configure(null,null,Math.Clamp(_appliedSettings.RollingBeforeSeconds,10,300),Math.Clamp(_appliedSettings.RollingAfterSeconds,0,60)); }
        if(done is not null) SaveRolling(done);
        else SetRollingStatus(RollingSettingsValid ? "Disabled or waiting for exactly one configured game while watching." : "Correct the rolling capture time limits and Save settings.");
    }
    private void UpdateRolling(AutomationStatus status)
    {
        var paths=(status.ActiveGamePaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(!_appliedSettings.RollingDiagnosticsEnabled || !_appliedSettings.AutomationEnabled || !RollingSettingsValid || paths.Length!=1) { InvalidateRolling(); return; }
        var path=SessionLibraryStore.NormalizeExecutable(paths[0]);
        if(!_appliedSettings.Games.Any(g=>SessionLibraryStore.NormalizeExecutable(g.ExecutablePath)==SessionLibraryStore.NormalizeExecutable(path))) { InvalidateRolling(); return; }
        if (_rollingFaultIdentity == path) return;
        _rollingFaultIdentity = null;
        var owner=_sessionTracker?.FindActive(path)?.Id;
        RollingCapture? done;
        lock(_rollingGate) { if(_rollingIdentity!=path || _rollingOwner!=owner || _rollingBefore!=_appliedSettings.RollingBeforeSeconds || _rollingAfter!=_appliedSettings.RollingAfterSeconds) _rollingGeneration++; _rollingIdentity=path; _rollingOwner=owner; _rollingBefore=_appliedSettings.RollingBeforeSeconds; _rollingAfter=_appliedSettings.RollingAfterSeconds; done=_rolling.Configure(SessionLibraryStore.NormalizeExecutable(path),owner,_appliedSettings.RollingBeforeSeconds,_appliedSettings.RollingAfterSeconds); _rollingActive=true; }
        if(done is not null) SaveRolling(done);
        if(_rollingTask is null || _rollingTask.IsCompleted)
        {
            _rollingCancellation?.Dispose();
            _rollingCancellation=new(); var token=_rollingCancellation.Token;
            _rollingTask=Task.Run(async ()=>
            {
                RollingHardwareSource? source = null;
                long sourceGeneration = -1;
                double lastSampleTime = double.NegativeInfinity;
                try
                {
                    while(!token.IsCancellationRequested)
                    {
                        RollingCapture? capture=null; string? message=null;
                        long generation; bool active;
                        lock(_rollingGate) { generation=_rollingGeneration; active=_rollingActive; }
                        if (!active || sourceGeneration != generation || _rollingClock.Elapsed.TotalSeconds-lastSampleTime > RollingDiagnosticBuffer.MaximumSampleGapSeconds)
                        {
                            source?.Dispose(); source=null;
                        }
                        HardwareReading? sample = null;
                        if (active)
                        {
                            try
                            {
                                source ??= new RollingHardwareSource();
                                sourceGeneration=generation;
                                sample=source.Sample(()=>_rollingClock.Elapsed.TotalSeconds);
                                if (sample.Seconds-lastSampleTime > RollingDiagnosticBuffer.MaximumSampleGapSeconds)
                                    sample=sample with { Cpu=null, Pages=null, Read=null, Write=null, Latency=null };
                                lastSampleTime=sample.Seconds;
                            }
                            catch (Exception ex)
                            {
                                source?.Dispose(); source=null;
                                await Dispatcher.InvokeAsync(()=>
                                {
                                    if (_exiting || generation != _rollingGeneration) return;
                                    _rollingFaultIdentity=_rollingIdentity;
                                    InvalidateRolling();
                                    SetRollingStatus("Rolling sampling failed: "+ex.Message+". Save settings to retry.");
                                    RollingResultStatus.Text="Sampling failed: "+ex.Message;
                                });
                            }
                        }
                        lock(_rollingGate)
                        {
                            if(_rollingActive && generation==_rollingGeneration && sample is not null) { capture=_rolling.Add(sample); if(capture is not null) _rollingDeliveryPending=true; message=_rolling.Capturing ? "Capturing following interval…" : $"Buffering: {_rolling.Count} readings. Warm-up may shorten pre-trigger coverage."; }
                        }
                        if(capture is not null) await Dispatcher.InvokeAsync(()=> { lock(_rollingGate) _rollingDeliveryPending=false; if(!_exiting) SaveRolling(capture); });
                        else if(message is not null) await Dispatcher.InvokeAsync(()=> { if(!_rollingSaving && !_exiting && generation==_rollingGeneration) SetRollingStatus(message); });
                        await Task.Delay(1000,token);
                    }
                }
                catch(OperationCanceledException) { }
                catch(Exception ex) { await Dispatcher.InvokeAsync(()=> { if(_exiting) return; _rollingFaultIdentity=_rollingIdentity; InvalidateRolling(); SetRollingStatus("Rolling sampling failed: "+ex.Message+". Save settings to retry."); }); }
                finally { source?.Dispose(); }
            });
        }
    }
    private void RollingCapture_Click(object sender,RoutedEventArgs e)=>TriggerRolling();
    private void TriggerRolling()
    {
        RollingCapture? done;
        lock(_rollingGate)
        {
            if(_exiting || _rollingSaving || _rollingDeliveryPending || !_rolling.Trigger(_rollingClock.Elapsed.TotalSeconds)) { SetRollingStatus(_rolling.Capturing || _rollingSaving || _rollingDeliveryPending ? "Already capturing or saving." : "Enable rolling capture, watch one configured game, and wait for a fresh reading."); return; }
            done=_rolling.Advance(_rollingClock.Elapsed.TotalSeconds);
        }
        SetRollingStatus("Capturing following interval…");
        if(done is not null) SaveRolling(done);
    }
    private void SaveRolling(RollingCapture capture) => _rollingSaveTask=SaveRollingAsync(capture);
    private async Task SaveRollingAsync(RollingCapture capture)
    {
        _rollingSaving=true;
        try
        {
            var path=Path.Combine(_dataDirectory,"Diagnostics","rolling-"+Guid.NewGuid().ToString("N")+".md");
            await Task.Run(()=> { Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var stream=new FileStream(path,FileMode.CreateNew); using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false)); writer.Write(capture.Report()); });
            _lastRolling=path;
            try
            {
                if(_sessionStore is null) throw new IOException("Session library unavailable");
                _sessionStore.AddAttachment(capture.SessionId,"Unassigned diagnostic","",SessionAttachmentKind.PerformanceReport,path,DateTime.UtcNow);
                RefreshSessionList(); RollingResultStatus.Text="Capture saved and attached. Open last capture to review. "+capture.Reason;
                ShowTrayNotification(new AutomationNotification("Diagnostic capture saved", "Open the capture from Performance or Sessions.", false));
            }
            catch(Exception ex) { RollingResultStatus.Text="Report saved at "+path+"; library attachment failed: "+ex.Message; }
        }
        catch(Exception ex) { RollingResultStatus.Text="Capture save failed: "+ex.Message; }
        finally { _rollingSaving=false; }
    }
    private void OpenRolling_Click(object sender,RoutedEventArgs e)
    {
        try { if(_lastRolling is not null) ShowReportTimeline(File.ReadAllText(_lastRolling),"What just happened?"); else RollingResultStatus.Text="No capture saved this run."; }
        catch(Exception ex) { RollingResultStatus.Text=ex.Message; }
    }
    private async Task StopRollingAsync()
    {
        _rollingCancellation?.Cancel();
        lock(_rollingGate) { _rollingActive=false; _rollingGeneration++; _rolling.Finish("Cancelled at shutdown"); SetRollingStatus("Pending rolling capture cancelled at shutdown."); }
        try { await Task.WhenAll(_rollingTask ?? Task.CompletedTask,_rollingSaveTask).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { AddActivity("Rolling diagnostics did not finish shutting down within 3 seconds."); }
        catch (Exception ex) { AddActivity("Rolling shutdown: "+ex.Message); }
        if (_rollingTask?.IsCompleted != false) _rollingCancellation?.Dispose();
    }
}
