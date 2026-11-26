# WARDOGS recording review

Open **Recording → WARDOGS review…** and choose **Scan recordings…**, or drop videos into the review window. Scanning starts automatically with bundled FFmpeg and Windows OCR. If English OCR is missing, open Windows **Settings → Time & language → Language & region** and add **Optical character recognition** under the English language options.

The status shows progress and an estimated time remaining for the current file once the scanner has measured its pace. Use **Cancel scan** for the active manual scan or **Scan actions → Cancel queued manual scans** for that queue. Closing the review cancels manual work. Interrupted manual scans can be retried with **Rescan selected**. This creates a separate scan with current settings and the original session association.

**Tools and sampling** contains the video decode thread count (1–16, default 4), sample rate, HUD crop and optional FFmpeg folder. **Reset sampling** restores the defaults. Each queued file saves a copy of these settings. The default crop covers the top-right 35 percent of the frame and samples twice per second. Adjust it for a different HUD position.

## Events and cash

Select a completed scan in **Saved scans**. From **Sessions**, choose a recording and use **File actions → Review saved WARDOGS events…** to open its latest completed scan. The event table shows kills, headshots, revenge kills, kill assists, revives, revive assists, healing a teammate and waiting to be revived. A cash reward appears beside an event when the scanner could read it. Cash and XP can appear in succession on the same notification, so the scanner follows the row and retains any cash it read. Click an event's **Video time** or either balance timestamp to open the original recording at that point.

The review shows the first and last confirmed account balances with their video timestamps, and **Net** shows the difference. The scanner looks for the balance in the menu and match HUD and requires it to agree across two consecutive samples. A recording can start or end before the balance becomes visible. **Detected rewards** adds readable payouts from matched notifications. Net change reflects all balance movements between the two readings, including spending. Cropped, short-lived or obscured notifications can be missed, and OCR can misread amounts or duplicate an event when rows shift. Review the source around a timestamp to confirm an amount.

For session totals, select an attached recording in **Sessions** and choose **File actions → Scan for WARDOGS events…**. Adding a recording from the review window also brings in its session when there is a unique match. The session uses the first and last balances in recording attachment order and reports how many recordings have completed scans. Only the newest completed scan per full recording contributes to totals. Clips can be reviewed separately. Existing scans retain their review decisions. Use **Rescan selected** to review cash and events with current settings.

Enable **Recording → Scan session recordings when no game is detected** to scan linked full recordings automatically, including older recordings that need the current scanner. The checkbox is saved immediately. The app scans one file at a time and can continue with the review window closed or while Meh Tools is in the tray. A detected game or an active connected OBS recording pauses background work. The interrupted file starts again when the computer is idle. Manual scans can still be started from review.

Meh Tools also checks configured recording folders for existing OBS files named with the standard date and time format. It links a file when its creation and last-write times fit a single finished WARDOGS session. Ambiguous recordings, replay clips and files with custom names can be attached from **Sessions**.

## Review and export

Set each event to Keep, Reject or Unreviewed and add a label if needed. Use **Set decision for selection** for multiple rows. Review edits save as you work. **Highlight timing** controls the time before and after each event and the gap for merging intervals. Defaults are 10 seconds before and 5 seconds after. The preview shows up to 100 merged Keep intervals. **Remove saved scan** removes review metadata. Your source video stays in place.

Selecting a candidate generates one cancellable timestamp thumbnail. **Open moment** verifies the source before opening the existing player, whose playback depends on installed codecs. Changed or missing recordings retain their saved evidence and decisions. Rescan a changed recording to open moments and export again.

**Export…** publishes a verified MKV montage, CSV timeline or SRT markers from kept moments. Every export checks the source size and last-modified time, refuses to overwrite a destination and supports cancellation. MKV uses nearby keyframes and preserves all audio streams or fails verification. CSV includes the source timestamp, label, event type, OCR text, any recorded confidence, detected cash reward and padded range. SRT uses independently padded source windows that can overlap. A verified montage can be attached to the session. CSV and SRT are text exports.

## Implementation

`NativeWardogsScanWork` constructs `FfmpegWardogsFrames` with a Windows `IHudOcr` adapter for `WardogsScanner`. FFmpeg streams grayscale HUD crops from the first primary video stream. A read lock protects the source through probing and scanning. The decoder is bounded by sample limits and pipe backpressure. Audio-only tails produce empty samples. Cancellation stops the decoder and the active OCR request. The default limits are 100,000 samples, 10,000 candidates and 256 OCR rows per sample.

`WardogsReviewStore` stores preferences, scan snapshots and review edits in `wardogs-reviews.json` in the settings directory. The schema retains up to 200 records and 32 MiB, with atomic writes and backup recovery. It preserves legacy scan results. `WardogsExporter` uses a deep snapshot of the completed scan and verifies the source under a continuous read lock through publication.

The regular test suites cover event tracking, balance confirmation, saved reviews, session aggregation and export. After publishing, run `dotnet run --project tests/JamesTools.UiTests -c Release -- --bundled-tools artifacts/publish` to exercise the production scanner with synthetic media when Windows English OCR is present.
