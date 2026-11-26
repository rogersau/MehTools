# Live WARDOGS and automatic highlights

## Highlights-only capture

1. In **Games**, select **WARDOGS highlights** as the capture mode and enable recording for WARDOGS. Enable recording automation, save the settings, and start WARDOGS with OBS closed so Meh Tools can start its replay buffer.
2. In **Recording**, connect to the local OBS WebSocket. Under **Live WARDOGS → Detection settings**, select which events should save highlights. Standard kills, including revenge kills, and headshots are enabled by default. Kill assists and support events are optional. Settings apply when the detector next starts.
3. Play in the foreground. Recording shows detection and clip-save status. Confirmed saves appear as clips in **Sessions**.

Supported team events include revives, revive assists, healing a teammate and waiting to be revived. The detector uses Windows OCR. If it reports that English OCR is missing, open Windows **Settings → Time & language → Language & region** and add **Optical character recognition** under the English language options.

Highlights mode starts the OBS Replay Buffer. Meh Tools waits five seconds after a confirmation by default. An event close to a pending save can extend the wait so a streak lands in one clip. The delay is capped to keep lead-in footage. OBS saves the replay buffer available at that point. Set the game's **Replay length override** for shorter clips, such as 20–30 seconds. Closing the game or stopping the buffer before a pending save can lose the highlight. The detector starts when OBS confirms the replay buffer is running and stops if OBS disconnects or managed capture ends.

## Manual detection preview

In **Recording → Live WARDOGS**, choose the game executable and, if prompted, the window. Press **Start** and focus the selected game. **Detection settings** contains the sample rate, processing budget and HUD crop. The preview shows timestamps and totals for detected events since Start. To see cash balances and detected cash rewards, scan a full recording in [WARDOGS review](wardogs-scanner.md).

The detector samples only the selected foreground window and pauses while minimized or unfocused. It ignores notifications already visible when sampling starts or resumes. Changing resolution also resets that baseline. Windows Graphics Capture requires a supported window and graphics session. Windowed or borderless SDR generally provides a readable HUD. Exclusive fullscreen or protected content can yield blank frames, and HDR can reduce OCR quality. The crop follows the game's HUD position. With a different UI scale, adjust it to cover your own rewards in the top-right corner.

The preview reports its sample and OCR rates plus the last measured processing latency. A short notification may fall between samples or disappear before confirmation. Use the detection preview to check the crop before enabling automated clip saves.
