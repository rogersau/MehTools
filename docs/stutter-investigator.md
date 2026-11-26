# Stutter Investigator

Open **Performance → Stutter Investigator**, select a running application and start recording. The default capture lasts 60 seconds. Open **Recording settings** to change the duration or stall threshold, add notes, or enable Deep Investigation. Typical captures last 30–90 seconds. The maximum is 300 seconds, and the default stall threshold is 33.333 ms. Use the same threshold when comparing recordings.

Normal durations start at one second; Deep durations start at 30 seconds. Either mode can be stopped early.

Normal mode records device/audio notifications, available Windows event logs and relevant utility process transitions, together with the existing hardware/process samples and PresentMon frames. Selecting **System activity only** records events and hardware without inventing frame statistics. Frame capture follows the selected executable path and separates presentation streams; other instances of the same executable may be observed. Applications that do not present frames can still be investigated using system and deep thread evidence.

Deep mode requires starting Meh Tools with administrator permissions yourself. The application does not elevate automatically. It requests scheduling, CPU sampling, available stack addresses, module offsets, DPC/ISR, I/O and relevant GPU/driver evidence. **Deep USB tracing** additionally requests USB provider events for resets/failures that ordinary PnP notifications may miss. Privilege/provider failures appear in the report and normal collection continues. Symbol names are not inferred from DLL names; an address in `runtime.dll` does not identify a function or establish a cause.

Stop early with **Stop and analyse**, or let the configured deadline stop the recording. Closing the investigator stops and saves its capture. Deep tracing has an independent cleanup watchdog and owned-session recovery, scoped to Meh Tools session names and owner process identities. It does not stop another application's WPR/ETW sessions. Raw ETL files remain local and separate from exported summaries.

Investigation PresentMon captures also use PresentMon's own timed termination, so its child process can stop at the recording deadline if Meh Tools exits unexpectedly. Overrides without that capability are unavailable for investigation frame capture.

## Reading the result

Findings appear first, followed by the timeline and significant stall list. Select a stall or click a timestamp to see nearby events, CPU/GPU frame evidence and the enclosing hardware sample windows. Zoom to 20, 5 or 1 second. Hardware bars represent measured intervals, not interpolated per-frame readings. Technical evidence includes collector availability, loss, device topology and unresolved addresses.

The shared clock uses absolute QPC for PresentMon and ETW. PresentMon's documented `CPUStartQPC` column is ticks; `CPUStartQPCTime` is QPC converted to milliseconds. Legacy recording-relative columns are not assumed to share an origin. Callback-only notifications have unknown occurrence latency. Event-log UTC timestamps are mapped through the recording anchor and retain clock uncertainty. Delivery order is not event order.

Periodicity reports count, mean interval and variance. Correlations report matches, time offsets, event/background frequency and confidence in the timing association. They are **not confirmed causes**. A single coincident event, dense background activity, missing data or imprecise timestamps cannot establish a culprit. Multiple candidates remain competing explanations. No matching events means missing or unexplained evidence, not proof that devices or drivers are uninvolved.

Confidence checks include phase shifts preserving event recurrence, because independent periodic sources can keep coinciding after one accidental phase alignment. Normal device/audio notification delivery coincidences are shown separately from precise event matches: their physical occurrence latency remains unknown.

## Controlled comparisons

Export the first capture, then open **Compare a retest**, choose the baseline and describe the one change you made. Repeat the same application, scene/workload, duration, threshold, graphics settings and background workload. Compare after the second capture. The result includes FPS/lows, frame distribution, stalls, recurrence and event/correlation changes with limitations. Meh Tools never disables devices, modifies drivers, edits game binaries or changes security settings for an investigation.

## Local files and sharing

The existing `%LOCALAPPDATA%\JamesTools\Diagnostics` recording folder contains the performance report and raw frame/hardware data, plus `investigation.json`, `investigation-events.jsonl`, `investigation-events.csv` and `investigation-state.json`. A state still marked `recording` after the owning process has exited indicates an interrupted capture; raw files may be incomplete and no complete final analysis is implied.

**Export report** writes readable Markdown, structured JSON and event CSV. Existing format-3 performance JSON and CSV columns remain compatible; the machine summary gains an optional `stutterInvestigation` section. Portable device IDs are local salted pseudonyms, not raw device instance IDs or serial numbers. Review user-entered notes and device descriptions before sharing. Do not share raw traces without separately reviewing their sensitive contents. Nothing uploads automatically.

## Validation on Windows

Run from the repository in PowerShell with .NET 10 installed:

```powershell
dotnet build JamesTools.sln -c Release
dotnet run --project tests/JamesTools.Tests -c Release --no-build
dotnet run --project tests/JamesTools.UiTests -c Release --no-build -- --investigation-render --presentmon-integration
dotnet run --project tests/JamesTools.Mcp.BackendTests -c Release --no-build
dotnet run --project tests/JamesTools.Mcp.SessionTests -c Release --no-build
dotnet run --project tests/JamesTools.Mcp.Tests -c Release --no-build
dotnet run --project tests/JamesTools.Mcp.Tests -c Release --no-build -- --investigation-smoke --only investigation-windows
dotnet run --project tests/JamesTools.Tests -c Release --no-build -- --investigation-smoke
dotnet run --project tests/JamesTools.Tests -c Release --no-build -- --investigation-overhead
```

The smoke test subscribes to real Windows sources and tests timed completion/cancellation; it does not disconnect devices or change configuration. The overhead test measures an idle baseline and normal collector CPU/private-memory changes, including startup/disposal. One idle run does not establish overhead during an event storm or a loaded workload. Process CPU excludes service/kernel and external PresentMon work.

For manual validation, record a harmless USB device connection/removal and an intentional default audio endpoint change, verify identities and topology, and repeat in elevated Deep mode with USB tracing. Check source availability first. Test a representative stuttering workload with monitoring off, Normal, and Deep; compare CPU/memory, callback/collector time, losses and pacing. Preserve the workload and repeat runs to assess observer effects.

For elevated crash cleanup validation, use a disposable test capture: note only the new Meh Tools session names, terminate that test app, and inspect `logman query -ets` after the watchdog deadline. Verify those owned sessions disappear and unrelated trace sessions remain. Reopen Deep Investigation to exercise orphan recovery. Do not terminate your main unsaved application or stop all trace sessions globally.

## Primary API references

- [PresentMon CSV timestamp definitions](https://github.com/GameTechDev/PresentMon/blob/main/Tools/generate/readme/metrics.awk)
- [Configuration Manager notifications](https://learn.microsoft.com/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification)
- [Audio endpoint notification callbacks](https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)
- [USB ETW tracing](https://learn.microsoft.com/windows-hardware/drivers/usbcon/how-to-capture-a-usb-event-trace)
- [ETW session control](https://learn.microsoft.com/windows/win32/api/evntrace/nf-evntrace-controltracew)

See [architecture audit](stutter-investigator-architecture.md) for the reused collectors and recording/report boundaries.

See [implementation and validation results](stutter-investigator-validation.md) for measured overhead, test coverage, changed components and limitations of this environment.
