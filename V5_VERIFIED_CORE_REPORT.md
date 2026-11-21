# CHARLI VISION — V5 VERIFIED CORE UPDATE (v1.4.0)

**Development status: partial delivery of ULTIMATE MUSIC EXPERIENCE V5, not a claim that every request is complete.**

## Implemented, verified
- **Song/version handling:** different recording durations trigger a new lyric search even with identical title/artist/album text. Version matching includes remaster, clean, explicit, re-recorded and related variants. If Spotify duration is unavailable, a different *known* album is not accepted as a verified match.
- **Provider behavior:** preserves offline imports and confirmed forced-alignment data first, cached LRCLIB metadata/word/line candidates, LRCLIB broad search, and lyrics.ovh plaintext fallback. The public LRCLIB-App-Id request header is now supplied. No new unofficial scraping or paid API.
- **Plain lyrics:** no manual paging. A calm, clearly *estimated* reading position follows the actual monotonic playback clock for pause/resume/seek; playback duration and scroll extent determine approximate positioning. The renderer never invents word or line timestamps.
- **Cinematic word rendering:** wider glyph-only fill transition, reduced word scale, and bounded smaller shadow blur/opacity. The plain line-level glow is also reduced. Actual source word timing remains the only basis for word progress.
- **Lyrics quality:** the existing source/quality display now includes format, metadata assurance caveats, source, actual offset and request/cache status. Unsupported YouTube Music extraction is clearly marked as disabled.
- **Resource saving:** optional 20 FPS target only while fully occluded **if** Performance Autopilot is explicitly enabled. Default **off** (preserves user high-refresh preference); the property is excluded from JSON serialization when disabled. Existing vinyl time integration remains monotonic.
- **Already present and preserved from V3/V4:** real 92-band WPF equalizer, music-adaptive RPM, lyrics editor with source restoration/local backups, optional user-owned offline WhisperX alignment, cached album design tokens, real transparent rounded WPF settings, scroll wheel/thumb, signed GitHub update verification.

## Testing performed
- Release candidate v1.4.0 self-contained win-x64 build: 121 self-tests **PASS**, 0 FAIL.
- WPF GUI regression: 10 PASS, 0 FAIL, 1 SKIP (physical OS mouse event not delivered during that session). The V4 thumb drag was independently verified in an earlier live test; do not claim that every automation run can deliver synthetic input.
- Spotify-session smoke: PASS.
- New Lyrics settings page screenshot inspected on local Windows PC; no missing text/controls, dynamic album glass style visible.
- Live 14-case online lyrics coverage during this V5 run: **Word 0, Line 0, Plain 8, None 6, Cache hits 2**. LRCLIB was unavailable/rate-limited and lyrics.ovh delivered plain fallback. These results are external-provider dependent and **do not** establish reliable live word-sync availability.
- No changes to original user settings and profile as verified by file hash before build/QA. User profile was Cinematic; not silently changed to Smooth.
- V5 real callback spacing benchmark on the release build: High Refresh mean **10.11 ms**, P95 **15.43 ms**, P99 **18.51 ms**, max **43.28 ms**, about **143.8 MB RAM** and **70.2% of one CPU core**; Balanced mean **17.08 ms**, P95 **29.90 ms**, P99 **35.63 ms**, max **44.41 ms**, **137.6 MB RAM** and **57.4% of one core**. Both benchmark modes returned PASS. These are WPF rendering callback intervals, not actual GPU-present timestamps and do NOT prove zero stutter or 180 physically displayed FPS.
- The exact v1.4.0 Inno Setup was installed in a separate test directory and uninstalled successfully. The deployed EXE hash matched the self-contained release build, 121 project self-tests and WPF GUI regression completed, setup/uninstall exit codes were 0, and user settings SHA-256 stayed identical.
- Release update manifest was RSA-PSS signed with the existing private project key (not published) and validated against the matching public key, installer size and SHA-256.

## Not completed / blocked
- **YouTube Music lyrics / YouTube captions:** not activated. ytmusicapi is an unofficial client and YouTube provider terms/developer policies restrict unauthorized extraction. There is no confirmed licensed YouTube Music lyrics endpoint available to this distributed app. No cookies, authorization bypass, scraping, or copyrighted-music downloading.
- **New external paid providers:** none added (user requested free/no external charges).
- Romanization, translation, singer/duet attribution require legitimate translation/text or singer metadata; not fabricated.
- No real-hardware touchpad, all protected games, all monitor/DPI combinations, full GPU-present latency telemetry, or zero-stutter guarantee.
- Windows installer is **not Authenticode signed** because there is no valid signing certificate. Official GitHub update manifest is separately RSA-PSS signed and includes exact installer SHA-256 and size.
- **Real v1.3.0 → v1.4.0 update discovery PASSED** through the original v1.3 settings UI: the user-visible update button returned "Available: 1.4.0 — verified". The test left settings unchanged and restarted production. This verifies signed discovery, not a complete one-click installation flow for this version. Earlier v1.0→v1.1 and v1.1→v1.2 full UI-driven upgrades remain documented separately.

## Project safety
- Existing source, installed app and settings backed up privately outside the public repository before V5 changes.
- **Production v1.4.0 installation PASS:** The standard packaged Windows Setup updated the normal existing app location; actual installed EXE hash matched the release build. 121 deployed self-tests and the Spotify-session test passed, user preferences SHA-256 stayed unchanged, and the normal application was restarted and running. Current saved profile: Cinematic. The user's newer global-hotkey setting remains enabled.
- Official GitHub v1.4.0 release contains all three public artifacts (Setup, signed JSON manifest, detached signature); private RSA key was not published.
- The separate Charli FPS project was not changed.
- No access tokens, private PEM keys, Spotify secrets or personal settings are committed or published.
