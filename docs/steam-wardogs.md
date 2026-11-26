# Steam WARDOGS highlights

1. In **Steam → Settings → Game Recording**, keep recording enabled for WARDOGS. If you want Steam to be the only recorder, uncheck **OBS Capture** for WARDOGS in **Meh Tools → Games**. The checkbox applies immediately to existing saved games and stops any OBS instance Meh Tools owns for that game. It does not close OBS opened by you or another app, change Steam settings, or disable detection, colours, power or diagnostics.
2. Open **Meh Tools → Recording → Steam WARDOGS highlights…**. The app checks the active Steam account for its recording folder. If it cannot find it, select the folder shown in Steam’s Game Recording settings, not its `video` or `timelines` subfolder.
3. Pick a Steam session, filter its events if you like, then select a marker. **Preview event** opens a temporary clip at the event. **Save MP4 clip…** saves a separate copy with about 10 seconds before and 10 after by default. Cut points follow Steam’s fragment boundaries, and the clip keeps the available audio tracks without re-encoding. You can attach the saved MP4 to a Meh Tools session using **Sessions → File actions**.

## Make a highlights video from an earlier session

In **Sessions**, select the WARDOGS session and choose **Make Steam highlights…**. Interrupted sessions can be matched too. If there’s no unique match, you can still select the Steam session by time in the review window. You can also get there from **Recording → Steam WARDOGS highlights…**.

Events with footage are checked initially. Uncheck individual rows, or use the event filter and **Use only shown** to start with one type. Switch filters and use **Include shown** to add another type without losing previous choices. **Save highlights video…** saves one MP4 from the checked events and attaches it if there’s one matching Meh Tools session. This works even with automatic highlights turned off.

**Steam K/D** appears next to the session duration when a unique finished Steam timeline has kill or death markers. It counts **Killed** and **Killed by** on the whole timeline, without needing the video or using the highlight selection. With no death markers the ratio is shown as a dash. These counts can differ from the in-game scoreboard.

## Make a highlights video automatically

In **Games**, select WARDOGS, enable **Make a highlights video after this game’s session ends**, and click **Save settings**. Keep Meh Tools watching during the session. After you exit the game, leave Meh Tools open or in the tray while Steam finalizes its timeline. Meh Tools matches the completed game session to one Steam timeline. It merges overlapping event windows, cuts out the gaps, and adds the resulting MP4 as a clip in **Sessions**.

Choose **Clip timing and event types…** from the game’s Steam highlights settings, or go to **Recording → Steam highlight timing and folders**. Set how many seconds to keep before and after each event (0–60 seconds each, at least one above zero, with 10 seconds on both sides by default). All event types are enabled by default for automatic exports. The review window’s filter and checkboxes apply only to manual exports. You can set a Steam recording folder if auto-detection fails and choose an output folder. The default output is **Videos → Meh Tools → Steam Highlights**. The status reports how many events had no remaining footage. No file is created for sessions with no usable events or no uniquely matched Steam timeline.

The MP4 keeps the original stream formats when they match. If Steam changed formats between parts of the session, Meh Tools converts the highlights to a shared format before joining them. Neither workflow edits Steam’s original.

This view reads events emitted by WARDOGS, including kills, assists, deaths and revives, when they appear on Steam’s timeline. It does not scan frames or invent events Steam didn’t record. Refresh after playing to see newly written timelines. Steam’s rolling recordings can expire. Events with no uniquely matched footage stay visible but cannot be clipped. Export rechecks the underlying footage and leaves Steam’s files untouched.

## Compatibility

Meh Tools reads Steam’s local WARDOGS timeline JSON, the recording spans in `gamerecording.pb`, and the corresponding background recording fragments. It uses the retained span for coverage and the original recording start for event-to-video offsets. The local files are a Steam client format, not a supported cross-app Steamworks API. If Steam changes the layout or the app cannot verify an event’s footage, you can still export an MP4 in Steam and attach it manually. [Valve Game Recording FAQ](https://help.steampowered.com/en/faqs/view/23B7-49AD-4A28-9590), [tracked Steam client field definitions](https://github.com/SteamTracking/Protobufs/blob/master/steam/webuimessages_gamerecordingfiles.proto).
