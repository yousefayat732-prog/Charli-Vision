# CHARLI VISION v1.2.0 — Infinity Perfection V3: verified completion release

Windows 10/11 x64 — free standalone installer.

## Changes from v1.1.0
- Extended frosted-glass WPF Settings: larger layouts, shared album-tinted gradients and lighting for headers, cards, sidebar and footer, rounded panels, shiny vector icon tiles and subtle hover animations.
- Windows 11 DWM native glass/corner preference where supported, fallback to one-time blurred WPF background. Does not force unsupported live Acrylic/Mica.
- Custom low-profile glass ScrollBar and slider style, still native WPF with usable input and keyboard access.
- Fixed real circular equalizer clipping: 92 evenly spaced round-cap FFT bars stay within the 350px viewport including glow; confirmed complete 0/360-degree seam geometry.
- Vinyl: saved manual 24–52 RPM and optional real-audio BPM adaptation; no fabricated BPM.
- Karaoke: subtler glyph-only glow; real word timestamp sweep remains intact. Line transitions use real timing with optional subtle tempo-responsive easing; no artificial word timestamps.
- Online lyrics continued from validated existing LRCLIB word/line and lyrics.ovh plaintext sources, with cache, version/duration checks and fallback. Optional offline WhisperX alignment remains opt-in.
- Update Center improved: one explicit “Update suchen und installieren” button now checks, downloads, signature-/hash-verifies and invokes the standard Windows installer. Network timeout reports are clearer; no silent installs.
- Added fail-closed Authenticode signing script for when a valid certificate and Windows SDK signtool become available. No private key is stored in Git.
- Added opt-in global-hotkey OS smoke test (Ctrl+Alt+F10 only, no keylogger). System registration confirmed; injected key delivery across third-party apps was blocked by Windows, so not claimed as successful.

## Actual validation performed
- 110 automated self-tests passed, 0 failed (including tampered manifest and malicious URL rejection).
- Real internet lyrics coverage: 14 cases = 5 Word, 6 Line, 2 Plain, 1 deliberately nonexistent; 2 cache hits.
- Real owned local WhisperX demo voice/audio: 20 of 20 words aligned; no uncertain lines.
- **Real one-click E2E** previously completed with live 1.0.0 app: clicked its UI Check/Install, downloaded validated 1.1.0 GitHub installer, clicked Windows Setup Install/Finish, confirmed installed hash and unchanged settings; production app restarted.
- WPF screen captures of playback, design, hotkey settings and glass menu visually reviewed; text remains legible and controls fit. New dark glass scrollbar replaces bright native strip.
- Performance run: High Refresh (180Hz monitor) WPF callback mean 5.63ms, p95 6.35ms, max 27.79ms; Smooth target 120 FPS mean 10.91ms, p95 23.12ms, max31.66ms; raw compositor callbacks were also delayed (p95 22.64ms). These are callback intervals, not actual GPU-present FPS.
- Global RegisterHotKey registration: PASS. Chrome absent and Windows rejected synthetic input to ChatGPT/Discord; Spotify foreground switching blocked, therefore external-app physical hotkey delivery NOT VERIFIED.
- v1.2.0 self-contained installer isolated install/uninstall and public signed-download verification must also pass before release publication.

## Blocked or not fully verified
- Authenticode: no valid private code-signing certificate or Windows SDK signtool present; installer remains unsigned at the Windows level, SmartScreen can still warn. The release **manifest** is independently RSA-PSS signed.
- No physical-frame-rate guarantee for layered WPF/Windows compositor, no guarantee of zero microstutter.
- Unsupported anti-cheat games, all third-party apps, every Windows screen/DPI configuration and exact reference-pixel reproduction not independently verified.
- syncedlyrics aggregator may access providers with different Terms; existing LRCLIB/lyrics.ovh plus owned local alignment retained without introducing unapproved scraping.
- Native real-time Windows backdrop depends on OS compatibility; one-time local frosted WPF fallback is used.

## User data
Existing user data including render profile, registered keybindings, desktop placement, karaoke offsets and custom lyric imports stay in %APPDATA%\CharliVision. Installer targets the same existing app ID; no forced preference reset or artificial 180-FPS promise.
