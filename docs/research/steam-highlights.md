# Steam highlights: public-source sanity check

Checked 2026-11-12 (Australia/Sydney). Public API claims use official primary sources; the final section separately records local media checks and the current Meh Tools integration.

**Recommendation:** Proceed with a small experimental Steam highlights importer after validating a fresh Season 03 kill marker against its video. Keep OBS for separate game/chat/mic tracks. Defer a Steam-plus-custom-audio recorder: it adds synchronization work without a supported Steam capture-control API.

**Latest local result (November 13):** Playing with Steam recording Off and then exiting Wardogs left no fresh event JSON; Steam removed the temporary session header on exit. Do not build an OBS marker importer that relies on Off mode. Test Record on demand without starting capture as a separate hypothesis, followed by a recording-enabled positive control if necessary.

**Rollout timing:** At the time of this check (2026-11-11 22:44 UTC), the announced November 12 13:00–15:00 UTC maintenance window was still in the future: November 13 00:00–02:00 in Sydney. Existing recordings ending November 10 predate the announced rollout; their lack of kill events is not evidence that the new integration is broken. Validate after the update actually ships. [Official Season 03 announcement](https://steamcommunity.com/gid/103582791475494490/announcements/detail/688648135317127222).

**Engineering decision:** Steam markers are useful highlight candidates, but the public SDK does not provide Meh Tools with an external, cross-game marker reader or recorder controller. Use a separately validated marker input and accessible media, then own the window selection and exported clips. This is an inference from the app-scoped SDK and interface surface, not a claim about private Steam internals. [Timeline reference](https://partner.steamgames.com/doc/api/ISteamTimeline), [SDK initialization](https://partner.steamgames.com/doc/sdk/api).

**Public Timeline APIs — confirmed surface:**

- `AddInstantaneousTimelineEvent`, range-event methods, tooltips, and game modes describe events/state. `StartRangeTimelineEvent` / `EndRangeTimelineEvent` delimit metadata, not video capture. Phase methods describe matches/chapters; recording-existence checks and overlay navigation refer to those events/phases. [ISteamTimeline](https://partner.steamgames.com/doc/api/ISteamTimeline).
- Neither the public reference nor Valve's SDK 1.65 header (`STEAMTIMELINE_INTERFACE_V004`) exposes recording start/stop, encoder/audio configuration, arbitrary timeline enumeration, video retrieval, or export. **No public recent-recording retrieval/save API was found.** Valve does document a client quick-capture hotkey for recent gameplay in background mode; that is a user feature, not a documented companion-app API. [Valve SDK header](https://github.com/ValveSoftware/Proton/blob/proton_11.0/lsteamclient/steamworks_sdk_165/isteamtimeline.h), [Game Recording](https://store.steampowered.com/gamerecording).
- `ETimelineEventClipPriority` values `None=1`, `Standard=2`, `Featured=3` describe clip suitability/promotion. Standard/Featured nominate suggestions; they do not guarantee a persistent clip or exported file. Event handles expire between runs. [ISteamTimeline](https://partner.steamgames.com/doc/api/ISteamTimeline).

**App boundary — confirmed facts and inference:** Steam initialization establishes an App ID context and requires the active user's license for that App ID. Timeline methods accept local event handles or game-provided phase IDs, with no target-game App ID parameter. Consequently, Meh Tools cannot use the supported public interface as a general reader/controller of WARDOGS' timeline. Owning the game as a player does not supply a documented cross-app interface. This is an API-contract conclusion, not proof that spoofing/private interfaces are technically impossible. [SDK initialization](https://partner.steamgames.com/doc/sdk/api), [Valve header](https://github.com/ValveSoftware/Proton/blob/proton_11.0/lsteamclient/steamworks_sdk_165/isteamtimeline.h).

**Metadata without video — partly confirmed, partly unknown:** Timeline metadata is not proof that corresponding video exists: the API explicitly checks recording availability, and Steam documents missing/overwritten footage on a timeline. However, the reviewed public sources do **not** explicitly guarantee that fresh timeline metadata is persisted while Game Recording is globally Off. Do not claim either “Off means no metadata” or “metadata always persists while Off” without the root's local evidence; distinguish that from old metadata surviving after footage disappears. [ISteamTimeline](https://partner.steamgames.com/doc/api/ISteamTimeline), [Steam FAQ](https://help.steampowered.com/en/faqs/view/23B7-49AD-4A28-9590).

**WARDOGS — announcement verified:** App 1867240's latest official announcement returned by Steam's news feed was *WARDOGS | Season 03 Patch Notes*, published 2026-11-11 17:59:23 UTC. Its Steam replay section announces unique timeline icons for **kills, revives, and giving up**, with more events planned. Season 03 was scheduled for November 12, with maintenance 13:00–15:00 UTC. This confirms the announcement, not successful rollout or marker behavior in the installed build. [Official patch notes](https://steamcommunity.com/gid/103582791475494490/announcements/detail/688648135317127222), [official news feed](https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=1867240&count=5&maxlength=0&feeds=steam_community_announcements).

**Audio — scope confirmed; separate tracks unconfirmed:** Steam captures gameplay video rather than desktop/other-program video, and can include other programs' audio, such as voice chat. Valve also documented a microphone-recording setting. Selecting included audio sources/channels does not establish separately editable game/Discord/mic tracks. No separate-track export guarantee or definitive track count was found in the reviewed Valve documentation; do not assert either true multitrack support or universally single-track output from these sources. [Game Recording](https://store.steampowered.com/gamerecording), [microphone setting release notes](https://store.steampowered.com/news/posts/?enddate=1727913032&feed=steam_community_announcements).

**Storage/export — supported workflow versus unknown formats:** Valve documents raw video/timeline storage, temporary background recordings that may be overwritten, permanent saved clips, and MP4 export. Saving a Steam clip and exporting a standalone MP4 are distinct operations. No public schema/compatibility contract for local timeline files, Steam-specific MPD layouts, or programmatic raw-file export was found in the reviewed interface/FAQ. Any local-file parser should therefore be treated as an experimental adapter; MPD observations are for the root to verify. [Steam FAQ](https://help.steampowered.com/en/faqs/view/23B7-49AD-4A28-9590), [public interface](https://partner.steamgames.com/doc/api/ISteamTimeline).

**WASAPI correction:** “One WASAPI session makes mic/Discord/game share a clock” is incorrect. A session groups streams for control, belongs to one endpoint, and cannot combine capture and rendering streams. [Microsoft Audio Sessions](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-sessions). Device position and QPC time are correlated but distinct; `IAudioClock::GetPosition` requires the device frequency to interpret position, while `IAudioCaptureClient::GetBuffer` provides packet frame positions and QPC timestamps in 100-ns units. [GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition), [GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer). **Engineering inference:** independent mic/output devices can have distinct clocks; timestamp each stream against a common timebase, account for offsets/discontinuities, and resample as needed to correct drift. Merely using one process, equal nominal sample rates, or simultaneous start calls does not establish synchronization. Microsoft's clock-synchronization discussion confirms the underlying reference/sample-clock drift problem, although that article describes a driver wave sink rather than a ready-made WASAPI capture recipe. [Clock synchronization](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/clock-synchronization).

**Recommended minimal flow — proposal, not an existing Steam integration:**

1. Obtain Steam marker timestamps only through an explicitly validated input path. The public SDK alone supplies no external WARDOGS marker feed; a local adapter remains experimental, while manual Steam clip/MP4 export is documented. [Public interface](https://partner.steamgames.com/doc/api/ISteamTimeline), [export workflow](https://help.steampowered.com/en/faqs/view/23B7-49AD-4A28-9590).
2. Normalize markers to the chosen media's timestamps, deduplicate, apply configurable pre/post padding, merge overlapping/nearby windows, and clamp to available footage. Preserve marker provenance and timing confidence; skip windows without media.
3. Export those windows into Meh Tools-owned persistent clips with a small manifest of source, timestamps, and events. Validate the final output before marking a highlight saved; a marker or suggested clip is insufficient.
4. For independently editable game/chat/mic audio, use **OBS as the capture option**, with distinct sources assigned to separate recording tracks and optionally a combined track for playback. OBS explicitly documents this and recommends MKV with later MP4 remuxing. This is a supported multitrack capability; the proposed Meh Tools/Steam alignment still needs local validation. [OBS multitrack guide](https://obsproject.com/kb/advanced-recording-guide-and-multi-track-audio), [source-to-track assignment](https://obsproject.com/kb/multiple-audio-track-recording-guide/).

## Local evidence and integration assessment

Inspected 12 November 2026, Australia/Sydney. These are observations of this PC, not a public Steam format contract.

### Findings

- `D:/clips/timelines` contains 22 Wardogs timeline JSON files (AppID 1867240), dated 23 September through 10 November 2026. Exactly one has an `entries` array: `timeline_186724020261018_075816.json`, containing an `achievement` event (`ACH_ECONOMY_SINGLE_LIFE_PROFIT`). There are no observed kill/revive/give-up events. Do not infer that custom game events work locally yet, or that all empty files have a particular cause.
- `D:/clips/video` contains 12 Wardogs recording folders with readable `session.mpd` files. All 12 manifests declare one video adaptation set and one AAC audio adaptation set. This establishes this PC's observed output, not an immutable Steam limitation or what inputs were mixed.
- `bg_1867240_20261110_013318/session.mpd` declares HEVC 2560x1440 at nominal 32 Mbps and stereo AAC 48 kHz at nominal 128 kbps, with 3-second segments and a manifest duration of 398.178 seconds. FFprobe reads both streams directly; its reported format duration is rounded to 398 seconds. This folder has 133 video and 133 audio chunks, both starting at chunk 1.
- A wire-format-only inspection of `D:/clips/gamerecording.pb` finds a timeline record linking `timeline_186724020261110_013150` to `bg_1867240_20261110_013318`, with nested integer fields 2=87950, 3=398178, 10=87950. Their apparent meanings are timeline start offset, duration, and original recording offset, in milliseconds. This interpretation matches the manifest duration and the approximately 88-second delay between timeline and recording filenames; field names and a complete mapping have NOT been verified against a schema.
- The Nov 10 timeline's JSON `endtime` is 2436436 (about 40m36s under the inferred millisecond units), while the only linked retained recording lasts 6m38s. A timeline's existence/duration does not establish video coverage for every event.
- The same protobuf inspection links `timeline_186724020261103_084504` to TWO video folders, `bg_1867240_20261103_084545` and `bg_1867240_20261103_084612`, with apparent timeline starts 41142 and 68347 and durations 26986 and 10192506. One timeline can span multiple recordings.
- `timeline_186724020261102_080230` links to `bg_1867240_20261102_080324` with nested values 2=10311262, 3=902986, 10=54262. The manifest is 902.986 seconds long. The mismatch between the apparent current start (10311.262 seconds) and original start (54.262 seconds) is consistent with rolling-buffer trimming. Do not map events to media using folder timestamps or subtract only the original recording start. Exact fields and retained media presentation timestamps still require validation.

### Media proof

Exported a requested 5-second interval at source media time 120 seconds using the installed FFmpeg with input seeking, `-map 0:v:0 -map 0:a? -c copy -movflags +faststart` to `artifacts/research/steam-source-probe.mp4`.

- FFmpeg extraction exit code: 0.
- Exported streams: HEVC video plus stereo AAC audio.
- Reported durations: video 5.105s; audio 5.005s; container 5.105s.
- Output size: 30,626,488 bytes.
- Decoding both exported streams to a null output completed with exit code 0.
- Stream copy is a viable fast export path; it does not prove frame-accurate clip boundaries. Exact boundary handling requires re-encoding or explicit keyframe-aligned behavior. No event-to-video alignment, multitrack export, HDR fidelity, or browser playback compatibility was validated by this check.

### Current application integration

- `src/JamesTools.Core/Recording/ObsRecorder.cs` launches OBS with `--startrecording`, and reports a start request rather than independently verifying the video/audio capture or precise media time. It configures existing capture/folder settings and preserves the user's OBS audio setup.
- `IRecordingController` in `src/JamesTools.Core/Automation/AutomationDependencies.cs` only exposes owned-process state, start and stop. It has no output-file identity, media timestamp, pause/resume timeline, or capture-clock anchor suitable for accurate Steam-event alignment yet.
- `SteamGame` and `LibraryGame` in the current discovery code carry names/root directories but no Steam AppID. A Steam importer needs an explicit AppID association, e.g. Wardogs 1867240, rather than matching display names or nearby modification times.
- A first Steam feature should be an importer/exporter, independent of OBS process ownership. Read timeline events and recording coverage, generate padded merged clip windows, export to the user's output directory, preserve all discovered audio streams, and report missing footage instead of producing a falsely matched clip.
- For OBS multitrack, reuse the marker/window planner but add a verified Steam-timeline-to-OBS-media mapping. The current process launch timestamp/file modification time is insufficient. A fresh recorded match with a known kill and an observable video cue is the next essential fixture.

No application code was changed. Existing unrelated working-tree edits were left intact.

## Recording-disabled follow-up: 13 November 2026

At 08:43 UTC (19:43 Sydney), the user reported playing Wardogs and getting kills with Steam recording disabled. The active Steam account's local recording settings have `BackgroundRecordMode=0`, consistent with that report. The configured recording directory is still `D:/clips`. Settings were read only; no recording mode was changed.

- `D:/clips/gamerecording.pb` now contains a new Wardogs session record, `timeline_186724020261113_084117`, whose recorded Unix start time is 1794559277. The observed record has a timeline ID, AppID and start date, but no recording spans or event payload. This establishes a session header even while capture is off; it does not establish persisted kill markers.
- There is no corresponding new timeline JSON in `D:/clips/timelines`. The newest saved Wardogs JSON is still from November 6. There are currently 13 old Wardogs JSON files and no Wardogs recording folders under `D:/clips/video`.
- The default `userdata/*/gamerecordings` locations for all three local Steam accounts were also checked; none contains fresh Wardogs event files.
- Wardogs was still running during this inspection. A normal game exit and another read are needed before deciding whether events are absent or simply have not been flushed to disk. Do not interpret the new session header alone as confirmation that Steam can supply OBS with kill markers while recording is disabled.

The timestamped inspection summary is kept locally at `artifacts/research/steam-recording-off-check.json`. This was the initial inspection while the game was running; the post-exit check follows.

### Post-exit result

At 12:11 UTC on November 13 (23:11 Sydney), after the user exited Wardogs:

- No Wardogs processes remained. The active account's saved recording mode was still `BackgroundRecordMode=0`.
- Steam had updated `D:/clips/gamerecording.pb` at 12:07 UTC and removed the temporary `timeline_186724020261113_084117` session record.
- No matching timeline JSON or other fresh Wardogs event file appeared in the configured directory or the local accounts' default recording directories. The newest saved Wardogs timeline remained November 6; no Wardogs video folders were present.

**Observed outcome:** This recording-Off session produced no persisted kill-marker data available to a local-file importer, even after game exit. This is a negative result for the proposed Off-mode event source on this machine. It does not establish how every Steam version behaves or prove that Wardogs' new markers function when recording is enabled, since that positive control has not yet been tested.

**Next experiment:** Select Steam's Record on demand mode, leave video capture stopped, get a known kill and exit the game. Valve documents that this mode only captures video when started; whether it retains game timeline markers without capture remains an untested hypothesis. If it does, it could supply timestamps alongside OBS without simultaneous Steam video recording. If it does not, test a short recording-enabled match to establish whether usable Wardogs markers are produced at all. [Steam recording modes](https://help.steampowered.com/en/faqs/view/23B7-49AD-4A28-9590).

Post-exit inspection summary: `artifacts/research/steam-recording-off-after-exit.json`. No Steam settings or application code were changed.
