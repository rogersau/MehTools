# Meh Tools

A fast, lightweight Windows 10/11 gaming companion that runs quietly in your tray — automating OBS capture, NVIDIA Digital Vibrance, Windows power plans, Steam highlight clips, and AI-ready performance/stutter diagnostics with zero telemetry or accounts.

---

## Features

- **OBS Capture Automation & Live Controls**
  Automatically launches your existing OBS Studio installation in **Recording**, **Replay Buffer**, or **Both** modes when a configured game starts, targets its window in your Game Capture source, optionally organizes captures by game and date, and closes OBS cleanly when you stop playing. Includes live OBS WebSocket controls and a global **Save clip** hotkey (`Ctrl+Alt+F10`).
- **Steam Game Recording Highlights**
  Reads native Steam Game Recording event timelines (kills, assists, revives, etc.) to review events and export a clean MP4 highlight montage automatically after a session ends or on demand — no OBS or OCR required.
- **NVIDIA Digital Vibrance**
  Boosts Digital Vibrance in game and restores your desktop level on exit, with per-game strength overrides, display targeting (**All displays**, **Selected display**, or **Game window display**), optional HDR auto-pause, and focus-only mode.
- **Windows Power Plans**
  Automatically switches between your desktop and gaming power plans when any configured game — or any detected Steam, Epic, GOG, EA, Ubisoft, or Xbox PC game — is running.
- **AI-Ready Performance & Stutter Diagnostics**
  Captures PresentMon frame times, NVML GPU sensors, CPU/RAM/process I/O, optional ETW hard faults, and live stutter markers (`Ctrl+Alt+F9`) into a structured Markdown report. Inspect spikes in the built-in **Stutter Investigator** and interactive timeline viewer, compare baseline vs. retest runs, or click **Copy for AI** to paste the full diagnostic report straight into ChatGPT, Claude, or Gemini for troubleshooting.
- **Session History**
  Keeps a searchable local log of detected play sessions with attached recordings, clips, performance reports, and notes.

All settings auto-save as you adjust them (`Ctrl+S` saves immediately).

---

## Download & Install

Grab the latest release from [GitHub Releases](https://github.com/rogersau/MehTools/releases/latest):

- **Installer**: `MehTools-setup-<version>-win-x64.exe` — per-user install (no admin required), Start Menu shortcut, optional desktop icon, and uninstaller.
- **Portable**: `MehTools-<version>-win-x64.zip` — extract anywhere and run `MehTools.exe` (keep the folder contents together).

Your settings and session data are stored locally in `%LOCALAPPDATA%\JamesTools`.

---

## Quick Start

1. Open **Meh Tools** and go to **Games** → **Add game…** to pick a running game or browse for its `.exe`.
2. Toggle **OBS Capture** and **Colours** per game (or configure global defaults on **Recording** and **Display & Power**).
3. If using OBS, make sure your active OBS scene has a visible **Game Capture** source and close OBS before launching a game so Meh Tools can manage it.
4. Click **Start watching** on **Overview** (or from the tray icon).
5. To capture performance/stutter evidence for an AI, open **Performance** (or **Stutter Investigator…**), run a capture while reproducing the issue, and click **Copy for AI**.

---

## Requirements

- **OS**: 64-bit Windows 10 (version 2004 / build 19041+) or Windows 11
- **Runtime**: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (already included in self-contained release builds)
- **Optional**:
  - **OBS Studio** (standard or portable) for automatic recording/replay buffer
  - **NVIDIA GPU & driver** for Digital Vibrance and NVML GPU telemetry
  - **FFmpeg / ffprobe** on `PATH` (or in `tools/ffmpeg`) for Steam highlight exports

---

## Build & Test

```powershell
dotnet build JamesTools.sln -c Release
dotnet run --project tests/JamesTools.Tests -c Release --no-build
dotnet run --project tests/JamesTools.UiTests -c Release --no-build
dotnet publish src/JamesTools/JamesTools.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o artifacts/publish
```

Optional installer build (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
iscc /DAppVersion=1.0.0 installer/MehTools.iss
```

---

## Guides

- [Performance diagnostics](docs/performance-diagnostics.md)
- [Stutter Investigator](docs/stutter-investigator.md)
- [Steam highlights](docs/steam-wardogs.md)

---

## License

[MIT](LICENSE) — Copyright © 2026 JamesTools contributors.  
Bundles [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT).
