# Meh Tools

A small Windows desktop companion for game recording, NVIDIA digital vibrance, and Windows power plans. It watches for games locally and applies only the features you enable.

## What it does

- Starts an existing OBS Studio installation in recording, replay-buffer, or combined mode when a selected game's executable is running, and asks that OBS instance to close after the game ends.
- Selects the game's window in your existing OBS Game Capture source and can update the recording folder, while keeping the rest of the scene, encoder, and audio tracks intact.
- Switches NVIDIA digital vibrance between a desktop value and a game value for games you select.
- Switches between existing Windows power plans for your selected games or all detected Steam games.
- Logs performance locally and saves a report you can share with an AI for troubleshooting.
- Shows an optional movable on-screen monitor for CPU, NVIDIA GPU and game frame measurements.
- Runs in the notification area and can start with Windows. No telemetry or account is required.

## Requirements

- 64-bit Windows 10 or 11.
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), unless using a self-contained build.
- OBS Studio on Windows, already configured with an active scene containing a **visible Game Capture** source. Portable and standard installations are supported.
- An NVIDIA GPU and NVIDIA-controlled displays for digital vibrance. Power and recording controls work independently of NVIDIA.

## Getting started

1. Launch Meh Tools. Automation starts paused on a new installation.
2. In **Games**, add a running Steam game or browse for its actual executable, then enable **Capture**, **Vibrance**, or both. The path must match the executable that runs during the game, which may differ from its launcher.
3. In **Recording**, select OBS, its configuration folder, and a recording folder. The app tries to discover the configuration folder when you choose OBS. For portable OBS, it is normally `<OBS folder>\config\obs-studio`; for a standard installation it is normally `%APPDATA%\obs-studio`.
4. Choose your vibrance values and Windows power plans. Enable each feature you want, then **Save** and **Start watching** on the Overview.
5. Optionally enable **Start with Windows** in Settings. Closing the window leaves the app in the notification area by default; there is also an option to minimize to the notification area instead of the taskbar. Use the tray menu to quit.

Select a game row and expand **Advanced options for the selected game** to choose recording, replay buffer, or both, and change matching, its power plan, colour strength, or stop delay. Blank colour and delay overrides use the global defaults. On **Recording**, set the replay buffer maximum (15–600 seconds) and the global **Save clip** hotkey (default **Ctrl+Alt+F10**; blank disables it). Use **Save** or **Ctrl+S** to keep changes. The Recording page includes **Check OBS setup** to help find setup problems before playing.

On **Colours → Vibrance**, choose **All displays** for the legacy behavior, **Selected display** to limit changes to one NVIDIA-controlled screen, or **Game window display** to follow the visible window of a matched game. Refresh displays to discover current Windows/NVIDIA display identities; a saved but disconnected selection stays visible and is never replaced automatically. Display discovery only reads settings. **Apply game colours only while the game is focused** uses the foreground game's process ID; moving a window and focus changes are noticed on the regular check interval (2–30 seconds), not immediately. In a targeted mode, only displays Meh Tools changes are restored to the configured desktop slider value, and a later manual/other-app value is respected. NVIDIA mapping is resolved again for each write, so a changed enumeration order cannot redirect a saved selection. If the driver cannot map a target or the game's display is ambiguous, targeted vibrance waits instead of changing another screen. The HDR pause is intentionally conservative: HDR detected on any active display pauses vibrance globally, and a failed/unknown HDR query also waits.

Use **Game profiles…** on the Games page to create, duplicate, rename, and edit reusable capture, folder, replay length, stop delay, vibrance, power plan, and overlay preset values. Assign a profile to a game, then fill any game-specific overrides; overrides win over profile values, and blank profile values inherit global settings. The per-game **Record** and **Colours** checkboxes remain explicit permissions and profiles do not enable them. Deleting an assigned profile is refused until its games are reassigned. Profile edits are drafts until **Save** in the main window; cancelling the editor discards them. Capture mode and folder changes take effect on the next OBS launch and never retarget a running capture. A profile recording folder can satisfy that game's output-folder requirement even when the global folder is blank.

Overlay presets follow the existing buttons: **Minimal** shows FPS, **FPS + frame delay** shows those two metrics, and **Full** shows CPU, GPU, FPS, and frame delay. Game presets affect monitor creation only when a game is selected in the monitor control; opacity, position, and click-through remain global. The profiles do not start the monitor automatically. A selected power plan is only a target setting; it does not promise a performance improvement.

Use **Export settings** in Settings to back up the current configuration, including an unfinished setup. **Import settings** loads a backup for review; it keeps the current watching state and only applies the imported configuration to automation when you save it. Paths may need adjusting on another PC.

The Game Capture source must be visible in OBS's active scene. When changing to a different game, Meh Tools waits for its visible game window before starting OBS. The selected OBS profile must use standard file recording, rather than Custom Output (FFmpeg).

For live output status or manual controls, enable the OBS WebSocket server in OBS (**Tools → WebSocket Server Settings**) and connect from the **Live OBS capture status** section. It connects only to localhost (default port 4455); the optional password is used for that session only and is never saved. Connecting is explicit and lets Meh Tools control that OBS session. The panel reports recording and replay-buffer state, and can start/stop either output or save a clip. The global hotkey uses the same explicit connection; automatic replay-buffer launch works without WebSocket, but saving clips through Meh Tools requires connecting. A clip is shown as saved only after OBS confirms its new save event and path.

OBS replay length is a maximum, not a guarantee: available memory, keyframe spacing, and encoder settings can shorten the actual clip. Replay capture uses the profile's standard recording encoder and audio tracks; simple output profiles set to **Same as stream** must be changed in OBS to a dedicated recording quality first. Close OBS before game automation so Meh Tools can apply the replay-buffer length and game target safely.

OBS audio tracks are configured in OBS, not in Meh Tools. An existing multitrack setup remains intact, including separate game, Discord and microphone tracks. To edit individual tracks, mute the combined track in your editor first. Test a short recording after changing games or audio devices.

Process automation leaves an OBS instance it did not launch alone. When it launches OBS, its status names the requested capture mode because process liveness does not confirm that OBS is recording or buffering. The connected recording panel is the source for live output status; check a short capture to verify picture and sound.

## Performance reports

Open **Performance**, choose a game if it is in your list, add any useful notes and select **Start log**. Play until the issue happens, then choose **Stop and save**. Open the resulting text file and paste it into an AI chat, or attach it if the chat supports files. Review it before sharing; it contains PC hardware details, performance readings, timestamps and any notes you add. Logging stays on your PC until you choose to share the report.

Add your **Game settings and test scene** (resolution, graphics, upscaling/frame generation, FPS cap, refresh rate, map and recording/background apps) and describe the symptoms and target FPS. These details are self-reported; Meh Tools does not read game settings. Add markers during logging for stutters, loading screens or alt-tab. The global **Stutter marker** hotkey defaults to **Ctrl+Alt+F9** (blank disables it); it records when the key is received without taking focus. A marker is an approximate observation time, not the exact stalled frame. If no log is active, it tells you to start one.

After stopping, use **View report timeline**. You can also open a saved report from Performance or choose **View report** on a session attachment; **Open selected file** still opens the text report separately. The timeline shows aligned frame-window, CPU/GPU, memory and disk panels, markers, and sample readings. Select a stutter marker to zoom to roughly ten seconds on either side. Select a time range and add excluded intervals such as loading or alt-tab; the selection summary includes only retained hardware samples and complete frame windows, and shows it separately from the original full-report summary. Excluded or boundary-crossing frame windows are omitted as whole windows.

For a controlled retest, choose a baseline report and enter the one setting changed before starting a new log. The completed report can be compared with that baseline in the app, with durations, capture availability and context shown alongside metric deltas. FPS deltas are skipped when the reports use different averaging methods. Different scenes, duration, capture availability or other workload changes limit interpretation; a metric change alone does not establish improvement or cause. Timeline windows are aggregates: frame-time averages are weighted by frame count where possible, while window maxima and stutter counts stay window-level. A selected range cannot provide exact per-frame P99 or 1% lows, and unavailable readings are not interpolated.

Reports include logical CPU load, system memory/commit, the selected processes' working set and private allocation with first/last changes, hard paging, disk throughput/latency, and NVIDIA GPU utilization, VRAM, power, temperature, graphics clock and driver where available. A CSV timeline preserves the latest 3,600 sample rows (about two hours); summaries cover the full recording and any omitted rows are counted. Blank cells mean unavailable or not queried, including GPU readings between polls. Full executable paths are excluded.

With PresentMon and a selected game, reports add frame-time P50/P95/P99/P99.9, overlapping counts above 33.333/50/100 ms, and the ten worst frames with approximate receipt offsets. The summary uses the presentation stream with most valid frames; each timeline window labels its own busiest stream. Average FPS is `1000 / mean frame interval`; this differs from older reports' mean of per-frame FPS. Report comparison skips FPS deltas when the averaging methods differ. Percentile-equivalent lows use `1000 / P99` and `1000 / P99.9`, rather than averaging the slowest frames.

The report is evidence for troubleshooting, not a definitive diagnosis. Missing counters stay unavailable. CPU clocks/temperature and throttling reasons are not measured. Working-set growth alone cannot prove a memory leak; commit is not pagefile usage, and system-wide paging/disk activity cannot identify which app caused a stall. PresentMon capture and offsets are best-effort, include loading/menus, and may be delayed by CSV buffering. Use comparable gameplay and repeat controlled tests before drawing conclusions.

## Sessions

**Sessions** keeps a local searchable history of configured games detected while watching, with references to confirmed OBS recordings, saved replay clips, and saved performance reports. Search by game, date, or notes; mark sessions as favourites, attach an existing file, open it or show it in its folder, and compare a report with the most recent available report from an earlier session of the same game. Session duration is detected time between observations, not a precise playtime measurement.

The library is stored as versioned JSON in `%LOCALAPPDATA%\JamesTools\sessions.json`; it does not scan folders or move/delete media. OBS and report files remain where they were saved. Capture and report associations keep the session identity known when recording/logging began, even if the game ends before the file is saved. Captures whose owner is unknown, including a recording already in progress when OBS is connected, appear in an **Unassigned** session and can be attached to a selected session explicitly. Missing files remain listed and are marked missing. Corrupt or unsupported history is preserved and reported instead of being replaced with an empty library.

## Recording storage

The **Recording** page shows free space for the effective configured output folder (global or a selected game's resolved folder). Capacity can be unknown when a volume is disconnected or inaccessible. Optional low-space warnings default off and do not stop a capture. **Organise new managed captures by game and date** also defaults off; when enabled, new managed captures use `base/game/yyyy-MM-dd`. Existing media is never moved, and an OBS instance already running outside Meh Tools may use another output folder.

The same page can preview cleanup suggestions from recording attachments already referenced by session history. Favourites, clips, active or incomplete sessions, reports, files outside configured roots, and unsafe or unavailable files are protected or excluded. Age and storage-budget limits only shape suggestions; they are not enforced. A preview is read-only and may become stale. Refresh it before acting on the information; Meh Tools does not delete or move recordings.

## On-screen monitor

On **Performance**, select **Show monitor** for CPU and GPU readings; select a game to add FPS and frame measurements. Meh Tools includes a bundled [Intel PresentMon](https://github.com/GameTechDev/PresentMon) console capture tool (used when available) for FPS/frame metrics; you can also choose a custom PresentMon executable if needed. Drag the monitor by its heading; close it with × or **Hide monitor**. It keeps running when the main window is minimized to the tray. CPU and NVIDIA GPU readings work without PresentMon or a selected game, including when PresentMon fails to start. Reopen the monitor to apply a different game or executable.

The monitor samples system-wide CPU and the first NVIDIA GPU about once a second. GPU usage requires a working NVIDIA driver with NVML; unsupported readings show a dash. PresentMon (bundled or custom) matches the executable name, so identically named processes can be included. FPS is derived from measured frame intervals, and frame latency is PresentMon's frame-start-to-display latency, not input-to-photon latency. Frame readings show a dash when no valid data arrives, including when PresentMon cannot capture the game. PresentMon may require additional Windows permissions (e.g. ETW access). The monitor is a topmost desktop window, so games in exclusive fullscreen can cover it; use borderless windowed mode for best results. Performance reports can include PresentMon frame pacing summaries when PresentMon and a target game are available. When opening the monitor or starting a report, Meh Tools resolves the selected game's actual executable from one observed process snapshot using the game's matching rule. If no unique accessible executable is found, it starts without a PresentMon target and reports why; choose/reopen the monitor or restart the report after the game appears or its executable changes. Reports and sessions still use the configured game's canonical path as identity.

## Install

Download `MehTools-setup-<version>-win-x64.exe` from the [latest release](https://github.com/rogersau/MehTools/releases/latest) and run it. The installer is per user, needs no administrator rights, adds a Start-menu shortcut (and an optional desktop icon), and includes an uninstaller that also removes the app's "Start with Windows" entry. Your settings in `%LOCALAPPDATA%\JamesTools` are kept on uninstall.

Prefer no install? Download `MehTools-<version>-win-x64.zip` and run `MehTools.exe` from the folder, keeping its files together.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then:

```powershell
dotnet build JamesTools.sln -c Release
dotnet run --project tests/JamesTools.Tests -c Release
dotnet publish src/JamesTools/JamesTools.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
iscc /DAppVersion=0.3.0 installer/MehTools.iss
```

The last step needs the [Inno Setup](https://jrsoftware.org/isinfo.php) compiler (`iscc`) and produces `artifacts/installer/MehTools-setup-<version>-win-x64.exe`.

Run `artifacts/publish/MehTools.exe` from the published folder. The folder contains the self-contained runtime; keep its files together.

## How it behaves

Settings and a small activity log are kept in `%LOCALAPPDATA%\JamesTools`; they are not part of the repository. The app watches processes using limited Windows query access and matches selected games using the configured exact path by default. On **Games**, open **Advanced options** to add or remove exact executable aliases for another install or executable. Browse to a full `.exe` path for each alias. Aliases are case-insensitive exact paths; they do not change the canonical executable path used for game, profile, recording-owner, session, and report identity. Existing name and folder match modes continue to work as configured.

Use **Why isn't this game detected? → Check detection** on **Games** for a read-only process snapshot and an explanation from the same matcher used by runtime automation. It defaults to current draft settings; choose saved active settings to inspect the last applied snapshot. The view lists matching processes and likely name/folder candidates, including inaccessible paths and mismatches, then reports configured action eligibility. It does not run automation or verify OBS output, HDR, focus, window presence, or hardware state; those remain runtime conditions. Likely candidates are informational unless the selected rule's actual match mode accepts them. Competing game rules are resolved in games-list order. Within one rule, process choice prefers the primary path, then an exact alias path, then the configured name/folder mode; ties use the lowest PID. An owned recording keeps its matching PID across process snapshot reorderings. An inaccessible executable path cannot be passed to OBS with a PID, so automatic capture waits for an accessible path rather than substituting the canonical path.

When a selected game matches multiple different executable paths, performance capture needs a unique accessible target. In that case Meh Tools still starts system-level logging/monitoring but does not request game frame capture; reopen the monitor or restart the report after resolving the ambiguity. PresentMon's live monitor filters by executable name and can still include identically named processes.

The app detects Steam games from local library manifests; it does not access your Steam account.

OBS profile and scene files are backed up before changes, and only edited while OBS is closed. If another app changes a configuration during preparation, Meh Tools stops instead of overwriting it. If OBS closes unexpectedly, the app does not repeatedly restart it during the same game session. If Meh Tools itself crashes, an already-running OBS instance is left alone when it is reopened.

Vibrance uses NVIDIA's driver interface, which depends on the installed driver and the displays it controls. Windows power-plan changes use the existing plans on the PC; Meh Tools does not create or modify plan settings.

## License

MIT. See [LICENSE](LICENSE).

Meh Tools may bundle [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT) for optional FPS/frame metrics; its license is included at `tools/PresentMon/LICENSE-PresentMon.txt` when bundled.
