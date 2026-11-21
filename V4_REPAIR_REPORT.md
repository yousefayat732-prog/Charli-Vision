# CHARLI VISION v1.3.0 — PROJECT TRUE LIQUID GLASS REPAIR V4

Date: 2026-11-21. Repair of the existing application, not a new product.

## Root causes found and repaired

1. **Duplicate rectangular background** — SettingsWindow.xaml used the opaque window background #0D141D, plus a blurred screenshot and an ambient rectangle **behind** the rounded glass shell. Nonlayered WindowStyle=None could not provide true transparent corners. Repaired with one WPF layered alpha-transparent window, all screenshot/tint/highlight layers **inside one clipped rounded shell** (SettingsGlassContent), no opaque outer backing rectangle, and lighter translucent material brushes. The compact menu also had redundant OuterShell.Background darkening its separate tint; it is transparent now. Actual rendered corner alpha: **0 / 0**. Native DWM backdrop is intentionally not applied to layered WPF windows; it cannot be relied on for their per-pixel alpha. The desktop is captured once per window open, not continuously.
2. **Scrollbar visually present but unreliable** — custom ScrollBar Track lacked Orientation=Vertical, correct Minimum, Maximum, ViewportSize, and a two-way bound Value. V4 binds all of these and preserves native scroll commands; the scrollbar is not hidden.
3. **Wheel animation self-cancelling** — WPF ScrollChanged can fire after ScrollToVerticalOffset() returns. The previous controller treated its own delayed event as external input and stopped after about 1 pixel. V4 tracks the last commanded offset across deferred WPF layout updates; real thumb dragging and other native scrolling can still interrupt the wheel animation. The compositor subscription is short-lived, easing uses a 65ms time constant and retargets immediately on repeated input.
4. **Unreachable keybind actions on small screens** — removed fixed-width columns; responsive two-line rows use WrapPanels. At 760px window width, all **24 Change/Remove actions** fit horizontally and remain reachable. Switching sections resets old scroll targets.
5. **Oversized windows** — advanced window clamps to the monitor working area; compact menu clamps to the work area as well. Both remain scrollable.
6. **Visual material** — shared album colors, eight existing premium icons, rounded translucent panels, restrained edges and thinner shadows are preserved. Background capture/blur is clipped inside the shell.
7. **Profiles** — compact menu now offers Ultra Light, Balanced, Smooth, High Refresh and Cinematic. No saved profile is changed automatically.

## Verified real Windows tests

- **110 / 110 automated project self-tests** passed; 0 failed.
- **11 / 11 real WPF GUI test groups** passed; 0 failed or skipped in final test run.
- WPF Tier **2** on the tested computer. Actual window corner alpha **0** at both sampled upper corners.
- Vertical ScrollBar orientation and real linked Track extent, mouse wheel down about **85.7px** and back up about **0.6px**, PageDown/PageUp commands, rapid wheel reversal.
- **Physical mouse dragging of the WPF Thumb** moved the content **353.37px**, with one DragStarted and seven DragDelta events and actual OS pointer capture. This was not merely assigning a scrollbar value.
- All six categories opened and reset scroll properly, 760px window width exposed all 24 hotkey buttons, compact menu remained scrollable at height 490px.
- 12 measured category switches: mean **39.69ms**, maximum **78.58ms**. These are descriptive single-run UI measurement values, not guaranteed latency.
- Actual Spotify-session smoke test: **PASS**. Release .NET build: **0 errors and 0 warnings**.
- Current advanced settings and compact menu screenshots inspected; they remain local because blurred desktop content can contain personal information.

## Honest limitations

- Layered WPF per-pixel transparency works in the real GUI test. Native DWM system Acrylic/Mica cannot be assumed to compose this layered window; glass is a translucent **one-shot blurred desktop snapshot**, not a continuous native backdrop.
- Hardware touchpad gestures in every external app, protected games, every DPI/monitor and Wallpaper Engine combination have not been independently verified. WPF PanningMode is enabled, but synthetic wheel tests do not prove a real touchpad.
- No guarantee of zero GPU frame drops or 180 physically presented FPS; WPF compositor callback timings are not GPU-present timestamps.
- Windows sometimes refuses automated foreground changes; the QA runner reports skipped pointer tests in such sessions. The final successful run verified a genuine physical WPF-thumb mouse drag.
- No extra UI framework dependencies were added; CHARLI FPS was not modified.
- **v1.3.0 isolated Windows setup and uninstall: PASS.** Installed binary matched the tested published SHA-256; the installed program passed 110 self-tests and 11 WPF GUI checks; installer/uninstaller returned exit code 0; personal settings SHA-256 remained identical. Public GitHub update availability and final production update are verified separately.

## Data protection and delivery

The user's actual saved render profile is **Cinematic**, even though the original prompt mentions Smooth. It is preserved unchanged along with other preferences stored under %APPDATA%\CharliVision.
The installer is not Windows Authenticode signed without an eligible certificate; SmartScreen may warn. The official update manifest is independently RSA-PSS signed and binds the exact setup SHA-256/size. No signing private keys or user settings are distributed.
