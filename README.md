# Meh Tools

A small Windows desktop companion for game recording, NVIDIA digital vibrance, and Windows power plans. It watches for games locally and applies only the features you enable.

## Install and get started

1. Open the [latest release](https://github.com/rogersau/MehTools/releases/latest) and download **`MehTools-setup-<version>-win-x64.exe`** under **Assets**.
2. Run the installer and keep **Launch Meh Tools** checked. You can also open **Meh Tools** from the Start menu. The installer works per user and includes the .NET Desktop Runtime.
3. On **Overview**, choose **Record my games** to set up OBS and your first game, or choose **Monitor performance** and click **Show monitor** to try the app right away. Watching starts off. You can return to the welcome screen from **Settings → Show getting started**.

The installer supports Windows 10 version 2004 or later and Windows 11 on systems that can run x64 apps. For a portable copy, download `MehTools-<version>-win-x64.zip`, extract the entire archive, and open `MehTools.exe` in the extracted folder. Both editions use your local settings in `%LOCALAPPDATA%\JamesTools`, which are kept on uninstall.

## What it does

- Starts an existing OBS Studio installation in recording, replay-buffer, or combined mode when a selected game's executable is running, and asks that OBS instance to close after the game ends.
- Selects the game's window in your existing OBS Game Capture source and can update the recording folder, while keeping the rest of the scene, encoder, and audio tracks intact.
- Switches NVIDIA digital vibrance between a desktop value and a game value for games you select.
- Switches between existing Windows power plans for your selected games or all detected Steam games.
- Logs performance locally and saves a report you can share with an AI for troubleshooting.
- Shows an optional movable on-screen monitor for CPU, NVIDIA GPU and game frame measurements.
- Runs in the notification area and can start with Windows. No telemetry or account is required.

## AI diagnostics with MCP

Meh Tools includes a separate, headless MCP server for read-only system diagnostics, live telemetry, bounded recording sessions, historical measurements, game-component inspection and Windows diagnostic events. Local clients launch `tools/mcp/MehTools.Mcp.exe` using stdio; the desktop application does not need to stay open. Optional HTTP requires authentication and explicit configuration.

See the [MCP setup, tool reference and testing guide](docs/mcp.md) and [architecture overview](docs/mcp-architecture.md). Open **Settings → MCP diagnostics…** to configure access and view server status. Sensor support depends on the hardware and driver; unavailable readings remain explicitly unavailable.

## Feature requirements

- Automatic recording uses OBS Studio with a **visible Game Capture** source in the active scene. Both standard and portable OBS installations work. Close OBS before playing to let Meh Tools manage the capture.
- Digital vibrance requires an NVIDIA GPU and NVIDIA-controlled displays. Power and recording controls work independently of NVIDIA.

## Configure additional games and automation

1. In **Games**, add an open game or browse for its actual executable. Select **Record**, **Colours**, or both for each game you want to automate. Choose the executable that runs during play, which may differ from the launcher.
2. Use **Recording** for manual OBS setup or to change the video folder. When you select the OBS executable, Meh Tools looks for its settings. They are usually in `<OBS folder>\config\obs-studio` for portable OBS and `%APPDATA%\obs-studio` for a standard install.
3. Enable the features you want in **Recording**, **Colours**, and **Power**, then select **Start watching** on **Overview** to save and apply them. Use **Save** to apply changes if watching is already on.
4. In **Settings**, you can enable **Start with Windows**. Closing the window leaves Meh Tools in the notification area by default. Use the tray menu to quit.

Select a game row and expand **Advanced options for the selected game** to choose recording, replay buffer, or both, and change matching, its power plan, colour strength, or stop delay. Blank colour and delay overrides use the global defaults. On **Recording**, set the replay buffer maximum (15–600 seconds) and the global **Save clip** hotkey (default **Ctrl+Alt+F10**; blank disables it). Use **Save** or **Ctrl+S** to keep changes. The Recording page includes **Check OBS setup** to help find setup problems before playing.

On **Colours → Vibrance**, choose **All displays** for the legacy behavior, **Selected display** to limit changes to one NVIDIA-controlled screen, or **Game window display** to follow the visible window of a matched game. Refresh displays to discover current Windows/NVIDIA display identities; a saved but disconnected selection stays visible and is never replaced automatically. Display discovery only reads settings. **Apply game colours only while the game is focused** uses the foreground game's process ID; moving a window and focus changes are noticed on the regular check interval (2–30 seconds), not immediately. In a targeted mode, only displays Meh Tools changes are restored to the configured desktop slider value, and a later manual/other-app value is respected. NVIDIA mapping is resolved again for each write, so a changed enumeration order cannot redirect a saved selection. If the driver cannot map a target or the game's display is ambiguous, targeted vibrance waits instead of changing another screen. The HDR pause is intentionally conservative: HDR detected on any active display pauses vibrance globally, and a failed/unknown HDR query also waits.

Use **Game profiles…** on the Games page to create, duplicate, rename, and edit reusable capture, folder, replay length, stop delay, vibrance, power plan, and overlay preset values. Assign a profile to a game, then fill any game-specific overrides; overrides win over profile values, and blank profile values inherit global settings. The per-game **Record** and **Colours** checkboxes remain explicit permissions and profiles do not enable them. Deleting an assigned profile is refused until its games are reassigned. Profile edits are drafts until **Save** in the main window; cancelling the editor discards them. Capture mode and folder changes take effect on the next OBS launch and never retarget a running capture. A profile recording folder can satisfy that game's output-folder requirement even when the global folder is blank.

Overlay presets follow the existing buttons: **Minimal** shows FPS, **FPS + frame delay** shows those two metrics, and **Full** shows CPU, GPU, FPS, and frame delay. Game presets affect monitor creation only when a game is selected in the monitor control; opacity, position, and click-through remain global. The profiles do not start the monitor automatically. A selected power plan is only a target setting; it does not promise a performance improvement.

Use **Export settings** in Settings to back up the current configuration, including an unfinished setup. **Import settings** loads a backup for review; it keeps the current watching state and only applies the imported configuration to automation when you save it. Paths may need adjusting on another PC.

The Game Capture source must be visible in OBS's active scene. When changing to a different game, Meh Tools waits for its visible game window before starting OBS. The selected OBS profile must use standard file recording, rather than Custom Output (FFmpeg).

For live output status or manual controls, enable the OBS WebSocket server in OBS (**Tools → WebSocket Server Settings**) and connect from the **Live OBS capture status** section. It connects only to localhost (default port 4455); the optional password is used for that session only and is never saved. Connecting is explicit and lets Meh Tools control that OBS session. The panel reports recording and replay-buffer state, and can start/stop either output or save a clip. The global hotkey uses the same explicit connection; automatic replay-buffer launch works without WebSocket, but saving clips through Meh Tools requires connecting. A clip is shown as saved only after OBS confirms its new save event and path.

**Recording health alerts** are optional and default off. On the Recording page, enable them and Save; then connect explicitly to OBS. A known recording request gets 20 seconds to become active. While active and unpaused, Meh Tools can alert if OBS reports that recording stopped or its output-byte counter has not increased for the configured 10–300 second threshold (default 30). Replay-only launches do not raise missing-recording or stalled-output alerts. Pause suspends byte-stall checks, and resume starts a fresh monitoring window. Stops requested by Meh Tools (including game-end, watching-off, and shutdown) are treated as intentional. A failed stop command restores monitoring. Tray alerts honor **Show tray notifications**; sound is a separate opt-in and defaults off. Each issue alerts once until output recovers or monitoring is reset.

These alerts are signals from OBS status counters, not proof that the recording contains the expected picture or sound. In particular, unchanged bytes can mean possible stalled output, but muxer buffering can also cause it. A lost OBS connection makes health unknown; it is never treated as proof that recording failed. An already-running external OBS output can be monitored after explicit connection, but Meh Tools does not assume ownership or change its configuration. Health monitoring never connects to OBS automatically, starts or stops outputs, saves media, scans recording folders, or reads output files.

OBS replay length is a maximum, not a guarantee: available memory, keyframe spacing, and encoder settings can shorten the actual clip. Replay capture uses the profile's standard recording encoder and audio tracks; simple output profiles set to **Same as stream** must be changed in OBS to a dedicated recording quality first. Close OBS before game automation so Meh Tools can apply the replay-buffer length and game target safely.

OBS audio tracks are configured in OBS, not in Meh Tools. An existing multitrack setup remains intact, including separate game, Discord and microphone tracks. To edit individual tracks, mute the combined track in your editor first. Test a short recording after changing games or audio devices.

Process automation leaves an OBS instance it did not launch alone. When it launches OBS, its status names the requested capture mode because process liveness does not confirm that OBS is recording or buffering. The connected recording panel is the source for live output status; check a short capture to verify picture and sound.

## Performance reports

Open **Performance**, choose a game if it is in your list, add any useful notes and select **Start log**. Play until the issue happens, then choose **Stop and save**. Open the resulting text file and paste it into an AI chat, or attach it if the chat supports files. Review it before sharing; it contains PC hardware details, performance readings, timestamps and any notes you add. Logging stays on your PC until you choose to share the report.

Add your **Game settings and test scene** (resolution, graphics, upscaling/frame generation, FPS cap, refresh rate, map and recording/background apps) and describe the symptoms and target FPS. These details are self-reported; Meh Tools does not read game settings. Add markers during logging for stutters, loading screens or alt-tab. The global **Stutter marker** hotkey defaults to **Ctrl+Alt+F9** (blank disables it); it records when the key is received without taking focus. A marker is an approximate observation time, not the exact stalled frame. If no log is active, it tells you to start one.

After stopping, use **View report timeline**. You can also open a saved report from Performance or choose **View report** on a session attachment; **Open selected file** still opens the text report separately. The timeline shows aligned frame-window, CPU/GPU, memory and disk panels, markers, and sample readings. Select a stutter marker to zoom to roughly ten seconds on either side. Select a time range and add excluded intervals such as loading or alt-tab; the selection summary includes only retained hardware samples and complete frame windows, and shows it separately from the original full-report summary. Excluded or boundary-crossing frame windows are omitted as whole windows.

For a controlled retest, choose a baseline report and enter the one setting changed before starting a new log. The completed report can be compared with that baseline in the app, with durations, capture availability and context shown alongside metric deltas. FPS deltas are skipped when the reports use different averaging methods. Different scenes, duration, capture availability or other workload changes limit interpretation; a metric change alone does not establish improvement or cause. Timeline windows are aggregates: frame-time averages are weighted by frame count where possible, while window maxima and stutter counts stay window-level. A selected range cannot provide exact per-frame P99 or 1% lows, and unavailable readings are not interpolated.

Format-3 reports add per-frame CPU/GPU Busy and Wait evidence, latency and presentation details where PresentMon supports them, QPC timestamp alignment, automatic investigations of the ten worst qualifying frames, rolling FPS, slow-frame clusters, warm-up exclusions, and structured test conditions. The timeline viewer shows a selectable stutter's surrounding frames and the actual hardware sampling windows. Generated/application frame types remain separate when instrumentation supplies them.

GPU collection uses direct NVML with individually optional sensors (fan percentage/RPM, memory clock, P-state, power limits and clock-limit reasons), with the existing basic nvidia-smi fallback in normal mode. An optional one-second mode increases sampling frequency. Process I/O accounting, combined soft/hard page faults and selected-process dedicated/shared GPU allocation augment the existing system counters. Optional privileged ETW can attribute hard faults/read bytes without retaining file names; unavailable access leaves normal recording active.

Reports and raw CSV/JSONL recordings stay under the local Diagnostics folder. Comparison includes metric deltas, percentages, durations, normalised stutter rates, coverage and workload limitations, including legacy reports. Missing sensors remain unavailable: CPU package temperatures/power/effective clocks, GPU hotspot/voltage and external game VRAM budget are not fabricated. High utilisation, memory growth, disk coincidence or a temperature improvement alone do not establish a cause.

See [performance diagnostics](docs/performance-diagnostics.md) for usage, measurement definitions, schema/retention details, exact sensor limitations, overhead measurements and remaining hardware checks.

## Sessions

**Sessions** keeps a local searchable history of configured games detected while watching, with references to confirmed OBS recordings, saved replay clips, and saved performance reports. Search by game, date, or notes; mark sessions as favourites, attach an existing file, open it or show it in its folder, and compare a report with the most recent available report from an earlier session of the same game. Session duration is detected time between observations, not a precise playtime measurement.

The library is stored as versioned JSON in `%LOCALAPPDATA%\JamesTools\sessions.json`; it does not scan folders or move/delete media. OBS and report files remain where they were saved. Capture and report associations keep the session identity known when recording/logging began, even if the game ends before the file is saved. Captures whose owner is unknown, including a recording already in progress when OBS is connected, appear in an **Unassigned** session and can be attached to a selected session explicitly. Missing files remain listed and are marked missing. Corrupt or unsupported history is preserved and reported instead of being replaced with an empty library.

## Recording storage

The **Recording** page shows free space for the effective configured output folder (global or a selected game's resolved folder). Capacity can be unknown when a volume is disconnected or inaccessible. Optional low-space warnings default off and do not stop a capture. **Organise new managed captures by game and date** also defaults off; when enabled, new managed captures use `base/game/yyyy-MM-dd`. Existing media is never moved, and an OBS instance already running outside Meh Tools may use another output folder.

The same page can preview cleanup suggestions from recording attachments already referenced by session history. Favourites, clips, active or incomplete sessions, reports, files outside configured roots, and unsafe or unavailable files are protected or excluded. Age and storage-budget limits only shape suggestions; they are not enforced. A preview is read-only and may become stale. Refresh it before acting on the information; Meh Tools does not delete or move recordings.

## On-screen monitor

On **Performance**, select **Show monitor** for CPU and GPU readings; select a game to add FPS and frame measurements. Meh Tools includes a bundled [Intel PresentMon](https://github.com/GameTechDev/PresentMon) console capture tool (used when available) for FPS/frame metrics; you can also choose a custom PresentMon executable if needed. Drag the monitor by its heading; close it with × or **Hide monitor**. It keeps running when the main window is minimized to the tray. CPU and NVIDIA GPU readings work without PresentMon or a selected game, including when PresentMon fails to start. Reopen the monitor to apply a different game or executable.

The monitor samples system-wide CPU and the first NVIDIA GPU about once a second. GPU usage requires a working NVIDIA driver with NVML; unsupported readings show a dash. PresentMon (bundled or custom) matches the executable name, so identically named processes can be included. FPS is derived from measured frame intervals, and frame latency is PresentMon's frame-start-to-display latency, not input-to-photon latency. Frame readings show a dash when no valid data arrives, including when PresentMon cannot capture the game. PresentMon may require additional Windows permissions (e.g. ETW access). The monitor is a topmost desktop window, so games in exclusive fullscreen can cover it; use borderless windowed mode for best results. Performance reports can include PresentMon frame pacing summaries when PresentMon and a target game are available. When opening the monitor or starting a report, Meh Tools resolves the selected game's actual executable from one observed process snapshot using the game's matching rule. If no unique accessible executable is found, it starts without a PresentMon target and reports why; choose/reopen the monitor or restart the report after the game appears or its executable changes. Reports and sessions still use the configured game's canonical path as identity.

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


### Recording moment bookmarks

Connect explicitly to OBS on Recording, then use **Bookmark moment** with an optional label or **Ctrl+Alt+F8** during a confirmed full recording. Configure Bookmark hotkey and Save settings; blank disables it. Registration conflicts appear in the status bar. This neither starts capture nor saves replay clips. Notifications follow the notification preference.

Moments store a fresh OBS output-duration counter (also while paused), so pauses add no wall time. Timing is approximate because of request latency and frame granularity. OBS WebSocket [GetRecordStatus](https://github.com/obsproject/obs-websocket/blob/master/src/requesthandler/RequestHandler_Record.cpp) uses the [frame-based duration helper](https://github.com/obsproject/obs-websocket/blob/master/src/utils/Obs_NumberHelper.cpp); [OBS pause handling](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-output.c) excludes paused output frames.

Sessions lists moments for the selected session; clear the selection to see Unassigned moments. Labels can be edited and moments removed without changing media. Ownership is pinned when capture is confirmed, including if the game ends before OBS reports its path. External ownership stays Unassigned. Paths remain visibly unresolved until confirmed for the same capture; connection gaps and counter resets leave older moments unresolved rather than guessing a file. Metadata is versioned in local `bookmarks.json` with atomic replacement and a backup; corrupt metadata is preserved and reported.

**Open at moment** uses a read-only WPF player and seeks after media opens. Codec/container support depends on Windows, and an actively written file may not play yet. Unsupported or missing media is reported. Copy timestamp and open-file fallback remain available; opening a file externally does not seek. The shared review window also offers a selected-file thumbnail and approximate stream-copy clip export; Steam integration is not included.

Automatic bookmark-to-file linking requires observing the start, stopping, and stopped events of an unsplit recording on one connection. Connecting partway through recording still permits approximate output-counter bookmarks, but their file path stays unresolved because earlier file splits cannot be ruled out. A reported file split suspends bookmarking until a new recording starts; output duration can span files, so it is not a proven position in the last segment. Stop-command replies alone do not link moments. These boundaries follow OBS's [recording events](https://github.com/obsproject/obs-websocket/blob/master/src/eventhandler/EventHandler_Outputs.cpp).

### Review and export clips

In Sessions, select a Recording or Clip and Open file to review it. Bookmark **Open at moment** uses the same window with the approximate saved offset. Use playback/seek, enter start/end in seconds (decimal point), or Set start/end here, then choose a new named `.mkv` destination and Export. Requested boundaries and verified output duration are shown. WPF codec failures leave manual range entry, export, copy timestamp and external open available; external open does not seek.

FFmpeg and ffprobe are optional local dependencies, never bundled or downloaded by Meh Tools. In the review window, Browse to their tools folder and save, or clear the folder for PATH discovery (also checks the app's `tools` folder). Missing tools disable probing/export through an actionable error, without preventing app startup or external playback. The preference persists in settings and snapshots.

Export copies the first video stream and all audio tracks into MKV without transcoding. Cuts are fast and approximate/keyframe limited, not frame accurate; HDR fidelity is not guaranteed. See the official [FFmpeg seeking and stream-copy documentation](https://ffmpeg.org/ffmpeg.html). Unsupported stream/container combinations fail visibly. Sources must be existing local MKV/MP4/MOV/WebM/AVI/M4V files; network inputs and playlists are rejected. A read handle blocks concurrent writing/deletion, so stop recording before export. Originals remain unchanged. Existing destinations are refused, including publication races. Output is verified before publication and before adding a Clip reference to the session captured when review opened; unassigned bookmarks produce unassigned clips. Cancel/close stops this operation and removes its temporary output. Thumbnails are generated only for the opened review file in temporary storage and removed after loading.

Portable media tests run in the normal console suite. Optional installed-tool synthetic integration (no recordings accessed):

```powershell
dotnet run --project tests/JamesTools.Tests -c Release -- --media-integration
```

This explicit integration requires FFmpeg/ffprobe on PATH, generates isolated synthetic media with two audio tracks, checks export duration/stream counts, original hash, collisions and cancellation, then removes its test directory. Skip this command when tools are unavailable.

### What just happened? hardware capture

On Performance, enable rolling capture and Save settings, then watch a configured game. No manual log is needed. Default Ctrl+Alt+F7 (or the button) saves up to 60 seconds before and 15 seconds after; bounds are 10–300 and 0–60 seconds. Open last capture uses the timeline viewer. Reports are uniquely saved under the app data Diagnostics folder and attached to the session pinned at trigger, or Unassigned.

Sampling is approximately once per second, system-wide CPU/GPU, physical memory and available paging/disk counters only. No frame/FPS capture or bottleneck inference. Missing counters stay unavailable; warm-up, gaps and partial completion are reported. Buffers have age and 720-reading limits. A trigger requires a fresh reading; only one trigger can capture/save at once. Game/session changes and stopping watching finish a partial report; shutdown cancels a pending capture. Counter baselines reset on game/session changes and sampling gaps over 2.5 seconds, so interval readings do not bridge these gaps. Multiple detected games suspend buffering. A sampling failure pauses capture until settings are saved to retry, or a different game is detected. Saved reports remain portable and omit executable paths.

### Repeated manual benchmarks
On Performance, select a game and open **Benchmark recipes…**. Save a named recipe with scene instructions, declared settings, warm-up, capture length and run count. Begin a set, arrange the scene yourself, then choose **Ready / Start next run**. Reset the scene manually between runs. Cancel or close the window to stop its logger. Games are never launched or controlled. A unique accessible matching process is required; exits/target changes, cancellation and scheduling gaps over three seconds make a run incomplete.

Recipes and immutable run-set snapshots live in the local versioned `benchmarks.json` store with atomic replacement and backup. Reports are saved under app-owned `Diagnostics` paths and attached to the session pinned at capture start (or an explicitly labelled diagnostic session). Results survive recipe edits/deletion and restart; interrupted captures become incomplete. Attachment failures retain the saved report path. Open saved reports from the recipe window using the existing timeline viewer.

Only completed runs within the capture-duration tolerance contribute to available hardware medians and min/max ranges. Missing measurements stay unavailable. Individual FPS/frame-time summaries include frame and sample counts. FPS/frame-time medians additionally require at least two runs with the same FPS methodology and one identical frame stream; older reports without stream metadata, mixed streams and changed stream identities are not combined. These descriptive results imply no statistical significance; scene/settings context is user declared. Target checks run about once per second and may miss shorter process transitions. Rolling diagnostics and the overlay keep their own collectors. Manual logging and recipe runs cannot run simultaneously.


Network diagnostics are opt-in in Performance. Enter up to three plain hostnames or IP addresses (one per line), then Save settings. Only Start log or the timed capture phase of a benchmark starts ICMP probes. Startup, warm-up, recipe editing, report viewing and rolling hardware capture do not probe. Benchmark windows pin the saved network configuration until closed; reopen to apply changes. Target names and resolved addresses are included in portable format-2 reports.

The saved timeline offers a target selector, RTT points and observation status table alongside hardware/frame timelines. Range and exclusion summaries use actual retained observations, without interpolation or multiplying frame windows. RTT uses successful replies; jitter is the mean absolute RTT difference between adjacent successful probes to the same address, resetting after failures, excluded intervals or send gaps over 2.5 seconds. Counts distinguish replies, timeouts and errors/unavailable/cancelled observations; no game packet-loss percentage is inferred. ICMP can behave differently from a game's connection and correlation does not establish causation. A local operation that exceeds its deadline stops probing that target; it is reported separately from ICMP timeouts. Malformed network metadata is ignored while the rest of the performance report remains readable.

Each target resolves once (2-second cancellable DNS limit) and pins one supported address for the capture. Probes have a 1-second timeout, at least a second between sends, one outstanding per target and at most three concurrent. Stalls do not cause catch-up bursts. Reports retain at most 3600 observations and the last hour per target. Unavailable DNS/unsupported targets retain their status, with missing RTT rather than zero. Older reports remain readable. Different network configurations prevent benchmark aggregation.

Configuration snapshots: select a configured game under Performance and open **Configuration snapshots…**. Use the file picker to explicitly allow up to ten local text configuration files (1 MiB each); unsupported encodings/formats are refused. Name and create a snapshot, select its file, then compare against the current allowlist or another snapshot. Snapshot payloads preserve exact bytes, including UTF-8/BOM UTF-16 encoding. Saved reports can be explicitly linked/unlinked; missing reports remain referenced. Snapshot history survives allowlist changes.

Restore is a separate confirmed action for one reviewed file at its original, currently selected path. The preview shows current-to-snapshot changes, both hashes, and encoding changes; byte-only differences are labelled. Close the game first. The app checks process matching before writing, then rechecks process matching, target hash, selection metadata and snapshot integrity immediately before replacement. It keeps a unique verified backup beside the target, atomically replaces that file and verifies the result. Changed, missing, read-only, network/device/ADS and reparse targets are refused. A failed restore reports replacement status and whether its backup was verified; backups are never automatically rolled back over later edits. File operations run one at a time, and the app waits for them to finish before allowing the window/app to close. Diff rendering is capped at 500 lines / 64,000 characters and uses replacement blocks rather than a minimal edit sequence. Linked reports must be supported saved timelines (up to 32 MiB). This feature does not discover, tune or edit configurations automatically. Snapshot data lives in the app-owned ConfigurationSnapshots directory; corruption blocks opening the store and retains diagnostics/metadata backups.

Performance history: select a configured game in Performance, then choose Performance history. Existing same-game session report references are indexed without scanning folders; Import saved report explicitly assigns another report to that game. Create a named group and declare the common scene/settings/purpose, select a report and Add/remove membership. Select a baseline for differences, and save user annotations separately from captured metadata. Open timeline checks the fingerprint first. Verify sources flags missing or changed files; Reindex creates a new immutable version requiring explicit membership. Group rename/deletion preserves reports and notes.

History uses a versioned local performance-history.json with atomic replacement and backup. Opening unchanged history does not rewrite metadata; new session report references are indexed once. Indexed measurements are immutable, and stale windows cannot overwrite newer metadata. Local report reads/hashes are bounded to 32 MiB; unsupported/missing files remain listed. Whole-report recorded summaries are used; absent P99/stutter data stays unavailable. Frame trends exclude unverified, unknown/multiple-stream or unsupported methodologies and differing network configurations. Stream IDs are exposed and may change between runs. Group declarations do not prove causal comparability or statistical significance. Recording context is Unknown; no OBS connection or recording-attachment inference is performed. The chart selector shows discrete means, P99, stutter counts/rates or secondary CPU/GPU/memory metrics without interpolation, using the latest 200 group members; all reports remain listed. Dates use validated capture start time, otherwise explicitly observed/imported time. Source verification time is displayed; Open timeline rechecks content. No report files are changed or removed.

WARDOGS candidate review is available from **Recording → WARDOGS review…** and a selected Sessions recording/clip via **Scan selected video…**. Explicitly choose local FFmpeg/Tesseract tools, add files, review approximate OCR guesses, keep/reject rows and save labels and merged interval choices. Export KEEP moments as a verified MKV montage, CSV timeline or SRT source markers. Exports verify the source fingerprint, refuse overwrite and support cancellation. Montage cuts are keyframe approximate; all audio is preserved or export fails. Selected candidates have timestamp thumbnails with a text fallback. The preset has no real-game accuracy validation. See [scanner and saved-review guide](docs/wardogs-scanner.md).

Live WARDOGS detection is an explicit opt-in under **Recording → Live WARDOGS**. Select an exact game window and trusted external Tesseract 5 / English tessdata paths, then Start and focus the game. It pauses on focus loss and never creates clips or bookmarks. See [workflow, diagnostics and limitations](docs/live-wardogs.md).
