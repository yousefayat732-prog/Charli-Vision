# CHARLI VISION v1.5.0 — LYRICS FULL RESTORATION V6

## Root cause — reproduced on the real Windows PC
In v1.4.0 the LRCLIB HTTP client sent **Lrclib-App-Id: CharliVision/1.4** although the app was not registered with that identifier. The LRCLIB API rejected all three tested requests (get/search/broad) with **HTTP 401 Unauthorized**. Removing the unregistered App-Id returned HTTP 200 for the exact same title and artist. LRCLIB explicitly permits anonymous use with an ordinary client identifier: https://lrclib.net/docs

The current v1.4.0 Spotify/live-overlay diagnostic before the fix showed Spotify playing, a valid position clock and active audio loopback, yet **LyricsCount=0, LyricsProvider=Keine, TextVisible=False**. Thus the primary root cause was blocked provider retrieval, **not** a hidden-but-populated WPF TextBlock.

## Changes
- **LyricsService.cs:** remove unregistered App-Id, send public Lrclib-Client instead. Preserve free public LRCLIB and lyrics.ovh, strict song/version matching, legitimate local imports, Word/Line/Plain parsing, caching and cooldown handling.
- **MainWindow.xaml.cs:** display real loaded source lyrics automatically. A failed lookup now schedules cancellable, backoff-controlled searches (transient error: 65 seconds initially, increasing to a 300-second maximum; a true not-found: 120 seconds initially). Song changes and manual rescan cancel old requests. Failed results are never cached permanently.
- **MediaController.cs:** a track whose valid duration arrives after metadata now emits a refreshed track event so the correct version is searched, without mistaking tiny timeline variations for a new song.
- **MainWindow.xaml.cs:** before the first real time cue or while the Spotify timeline is temporarily invalid, display the first actual upcoming source line in muted styling; do not claim false word sync. Correctly restore previous/current/next visibility when the clock becomes valid.
- **MainWindow.xaml.cs:** include pipeline states Searching/Loading/WordSynced/LineSynced/PlainLyrics/NotFound/ProviderError/Offline, visible WPF renderer checks, retry count and safe status diagnostics in the existing Settings panel. No technical error popup in the desktop overlay.
- **LyricsRecoveryPolicy.cs:** no additional WPF timer; bounded asynchronous network retry policy.
- **V6OverlayQa.cs, App.xaml.cs, SelfTests.cs:** opt-in public-provider -> parser -> actual native WPF Word/Line/Plain visual QA and regression tests. Never runs normally.
- No external paid service or unauthorized YouTube Music/Spotify extraction; no separate lyrics engine and no changes to Charli FPS.

## Verified
- **Before fix, 14 provider cases:** Word=0, Line=0, Plain=8, None=6. **After fixing the header:** Word=4, Line=7, Plain=2, None=1. These are live network checks for a fixed 14-song metadata selection; availability varies with provider and song.
- Actual Spotify session after the correction: valid clock, LRCLIB Plain result and **ActualLyricsVisible=True**, 2,125 source text characters in the WPF reader; image captured privately from the running app and verified. No manual search was needed.
- True end-to-end WPF QA using public LRCLIB data and a **synthetic QA playback clock**: Word (37 cue lines, active word panel plus previous/next) PASS, Line (36 cue lines, active line plus previous/next) PASS, Plain (text reader) PASS. Actual QA screenshots were visually inspected against the user's 12-second video reference. These synthetic-clock cases are **not** claimed to be real Spotify word-sync sessions.
- All existing V4 glass/menu/scrollbar tests and music playback tests are retained; the real phone/OS pointer environment can affect simulated mouse dragging.
- The source backup, installed v1.4.0 executable and settings were saved privately before changes. Personal lyrics library, offsets, keybinds, album themes and user's Cinematic profile are not changed by the release.
- The LRCLIB request regression explicitly checks the absence of an unregistered App-Id.
- Official **v1.5.0 self-contained win-x64 release build**: 125 self-tests PASS, 0 FAIL; real public LRCLIB → native WPF Word/Line/Plain QA PASS; **11 WPF GUI checks PASS, 0 skipped/failed** in the release QA run; Spotify-session smoke PASS.
- A separate temporary Windows **v1.5.0 installer installation/uninstallation** returned exit code 0, deployed EXE hash matched the published candidate, the deployed EXE passed its self-tests and WPF GUI checks, and personal settings SHA-256 remained unchanged.
- Installer SHA-256 is bound to the official JSON update manifest, signed with the project's private RSA-PSS key and verified against the corresponding public key. No signing private material is distributed.

## Remaining limitations
- No verified lyrics provider can guarantee matching text for every song. Missing lyrics remain blank; invented timestamps or mismatched versions are forbidden.
- The user's reference recording was examined as still frames, not used for exact frame-by-frame color-motion correspondence.
- Real Word/Line QA used a synthetic clock and actual public-provider lyrics; the real Spotify session that was available for live testing provided Plain lyrics. Do not claim that native hardware, all-song karaoke timing or zero frame stutter were fully certified.
- YouTube Music unofficial lyrics extraction is not enabled due unconfirmed authorized provider access.
- The Windows installer lacks an Authenticode code-signing certificate. The public update manifest and installer SHA-256 are verified using the existing private project RSA-PSS release-signing key, which must never be published.

## Final release and production validation
- Public GitHub release **v1.5.0** contains the self-contained x64 installer, SHA-256-linked JSON manifest and detached RSA-PSS signature. All three assets were independently verified as present via GitHub's public release API.
- **Real update discovery (v1.4.0 -> v1.5.0): PASS.** The existing v1.4 Settings update button displayed **Installed 1.4.0 / Available 1.5.0 / Update verified** with the signed public manifest. User settings SHA-256 did not change. The actual installation was then performed with the verified standard Windows installer; the entire UI-driven one-click installer sequence was not separately repeated for this release.
- **Production install: PASS.** The existing Windows installation was updated in place, deployed EXE SHA-256 matched the release build, 125 deployed self-tests passed and Spotify smoke passed. The production app loaded LRCLIB Plain lyrics during real Spotify playback, with `ActualLyricsVisible=True`, a valid clock, and 2,125 source-text characters in its native WPF reader. The production application was then restarted without diagnostic environment variables and confirmed running normally.
- Settings SHA-256 was identical before and after deployment; the existing Cinematic rendering profile and enabled global hotkeys were preserved.
- **Release frame callback benchmarks:** High Refresh: mean **8.71 ms**, P95 **11.89 ms**, P99 **16.67 ms**, max **46.04 ms**, 145.8 MB RAM; Balanced: mean **16.87 ms**, P95 **30.15 ms**, P99 **33.54 ms**, max **38.74 ms**, 138.1 MB RAM. Both benchmarks returned PASS. These are **WPF compositor callback intervals**, not timestamps of frames actually presented by the GPU and do **not** certify zero microstutter.
- Earlier v1.4.0 installation and settings were backed up outside the public GitHub repository before deployment, allowing rollback if required.
