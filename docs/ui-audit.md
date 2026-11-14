# UI consistency and close regression audit

The November 2026 audit reproduced the reported WARDOGS review crash: an idle `ShutdownAsync` could complete synchronously, causing `Close()` to run again inside the original `Closing` event. The review now drains work and queues the final close after that event returns. Benchmark cancellation and main-window exit also avoid re-entering close handlers. Clip review now cancels and awaits media loading/export before closing; app exit awaits those windows too.

`Theme.xaml` holds the app palette, typography, focus indicators and control templates. `WindowTheme` applies the shared window style and requests matching app-owned caption colours through the [supported Windows 11 DWM attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute), without changing the Windows theme. Older systems retain native captions. Tables, lists, dropdowns, editable cells, buttons, checkboxes, sliders, scrollbars, passwords and tooltips use the same colours. Windows file pickers, message boxes and the system tray retain their native OS presentation.

The audit covers all eight main pages and the benchmark, configuration snapshot, history, profile, timeline, recording review, live WARDOGS and offline WARDOGS windows, plus overlay markup. The two small inline dialogs use the same window/control resources. It also fixes blank inherited profile choices, broken colour-target/display option bindings, multiline text alignment, missing field labels, hidden media-preview space, and cramped layouts. WARDOGS settings, saved scans and review actions have bounded scrolling; expanding settings cannot consume the candidate table. The clip preview stays visible while controls scroll. Timeline details scroll independently at smaller sizes.

Run the core and Windows UI checks:

```powershell
dotnet build JamesTools.sln -c Release
dotnet run --project tests/JamesTools.Tests -c Release --no-build
dotnet run --project tests/JamesTools.UiTests -c Release --no-build
```

To save synthetic visual renders for inspection:

```powershell
dotnet run --project tests/JamesTools.UiTests -c Release --no-build -- --render artifacts/ui-audit
```

The UI harness checks 35 window/page layouts at normal and minimum sizes, shared dark surfaces, reachable scrolling, inherited selections, dropdown popups, slider commands and data-grid edits. Close tests cover review before initialization, after initialization, with pending work, repeated close, and live/benchmark cancellation. The original close regression was observed failing before the fix. CI and the packaging workflow run these checks.

Tests use temporary synthetic stores and isolated windows. Main-window and overlay markup are loaded without their code-behind, so tests cannot start automation, hardware sampling or OBS. They do not open personal recordings or restore real configurations. Renders inspect WPF content at 96-DPI layout units; they do not establish pixel-perfect behaviour at every monitor scale, native-dialog appearance, or gameplay/OCR accuracy. Generated images remain ignored local artifacts.
