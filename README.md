# Meh Tools

A small Windows desktop companion for game recording, NVIDIA digital vibrance, and Windows power plans. It watches for games locally and applies only the features you enable.

## What it does

- Starts an existing OBS Studio installation when a selected game's executable is running, and asks that OBS instance to close after the game ends.
- Selects the game's window in your existing OBS Game Capture source and can update the recording folder, while keeping the rest of the scene, encoder, and audio tracks intact.
- Switches NVIDIA digital vibrance between a desktop value and a game value for games you select.
- Switches between existing Windows power plans for your selected games or all detected Steam games.
- Logs performance locally and saves a report you can share with an AI for troubleshooting.
- Runs in the notification area and can start with Windows. No telemetry or account is required.

## Requirements

- 64-bit Windows 10 or 11.
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), unless using a self-contained build.
- OBS Studio on Windows, already configured with an active scene containing a **visible Game Capture** source. Portable and standard installations are supported.
- An NVIDIA GPU and NVIDIA-controlled displays for digital vibrance. Power and recording controls work independently of NVIDIA.

## Getting started

1. Launch Meh Tools. Automation starts paused on a new installation.
2. In **Games**, add a running Steam game or browse for its actual executable, then select **Record**, **Vibrance**, or both. The path must match the executable that runs during the game, which may differ from its launcher.
3. In **Recording**, select OBS, its configuration folder, and a recording folder. The app tries to discover the configuration folder when you choose OBS. For portable OBS, it is normally `<OBS folder>\config\obs-studio`; for a standard installation it is normally `%APPDATA%\obs-studio`.
4. Choose your vibrance values and Windows power plans. Enable each feature you want, then **Save** and **Start watching** on the Overview.
5. Optionally enable **Start with Windows** in Settings. Closing the window leaves the app in the notification area by default; use its tray menu to quit.

The Game Capture source must be visible in OBS's active scene. When changing to a different game, Meh Tools waits for its visible game window before starting OBS. The selected OBS profile must use standard file recording, rather than Custom Output (FFmpeg).

OBS audio tracks are configured in OBS, not in Meh Tools. An existing multitrack setup remains intact, including separate game, Discord and microphone tracks. To edit individual tracks, mute the combined track in your editor first. Test a short recording after changing games or audio devices.

If OBS was already open, Meh Tools leaves it alone and will not automatically start or stop a recording in that session. Its status reports that OBS was launched with recording requested; it does not independently verify that OBS captured picture or sound. Check a short recording before relying on a new setup.

## Performance reports

Open **Performance**, choose a game if it is in your list, add any useful notes and select **Start log**. Play until the issue happens, then choose **Stop and save**. Open the resulting text file and paste it into an AI chat, or attach it if the chat supports files. Review it before sharing; it contains PC hardware details, performance readings, timestamps and any notes you add. Logging stays on your PC until you choose to share the report.

The report is evidence for troubleshooting, not a definitive diagnosis. FPS, frame times and individual CPU core load are not measured; overall CPU use can hide a single busy core. NVIDIA GPU metrics require a working `nvidia-smi`; unavailable measurements are marked as such. Use a frame-time tool and repeat the test if the AI needs more evidence.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then:

```powershell
dotnet build JamesTools.sln -c Release
dotnet run --project tests/JamesTools.Tests -c Release
dotnet publish src/JamesTools/JamesTools.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

Run `artifacts/publish/MehTools.exe` from the published folder. The folder contains the self-contained runtime; keep its files together.

## How it behaves

Settings and a small activity log are kept in `%LOCALAPPDATA%\JamesTools`; they are not part of the repository. The app watches processes using limited Windows query access and matches selected games by their executable paths. It detects Steam games from local library manifests; it does not access your Steam account.

OBS profile and scene files are backed up before changes, and only edited while OBS is closed. If another app changes a configuration during preparation, Meh Tools stops instead of overwriting it. If OBS closes unexpectedly, the app does not repeatedly restart it during the same game session. If Meh Tools itself crashes, an already-running OBS instance is left alone when it is reopened.

Vibrance uses NVIDIA's driver interface, which depends on the installed driver and the displays it controls. Windows power-plan changes use the existing plans on the PC; Meh Tools does not create or modify plan settings.

## License

MIT. See [LICENSE](LICENSE).
