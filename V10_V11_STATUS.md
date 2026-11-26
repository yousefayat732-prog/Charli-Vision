# V10 / V11 — Release candidate 1.7.0

Status: local candidate, not a stable release.

## Implemented
- V10: bounded persistent LRCLIB cache (positive verified results only), automatic retrieval, strict recording matching, source timing preserved, no fabricated timestamps.
- Five-row lyrics focus, responsive active text, coordinated cue movement, reduced-motion support, album-reactive feathered backgrounds, plain reader and unavailable/instrumental states.
- V11: Velopack 1.2.161, early bootstrap, single update engine, authenticated RSA-PSS channel index, SHA-256 and package identity validation, user-initiated update and restart.
- Production ID: CharliVision.Desktop. Existing roaming settings directory retained. No change to Chexx.

## Verified
- 134 self-tests passed; native Word/Line/Plain rendering passed after latest visual refinement.
- Earlier isolated installed QA packages A 1.7.0, B 1.7.1, C 1.7.2: both updates initiated through real WPF UI, automatic restart and version verification; selected settings, configured hotkey and test cache hash preserved.
- Earlier QA C: 11 GUI tests passed, Spotify session passed; 14 online cases: 5 word, 6 line, 2 plain, 1 missing.
- Local signed file feed only. Delta packages created; actual delta selection not independently measured.
- Latest source refinement was tested separately; final production package is not yet installed or publicly tested.

## Migration and release gates
1. Back up existing roaming data (settings, offsets, imported lyrics, cache) and old installation.
2. Close old Vision normally. Do not delete or overwrite the existing local signing/update directory.
3. Install the verified new Velopack Setup once. Old Inno updater cannot directly consume the Velopack index. Verify shortcuts and autostart point to the new executable, and that original settings/data remain available. Keep rollback copy.
4. Test public signed GitHub channel using two consecutive version transitions; validate download, signature, restart and data preservation. Test a clean Windows installation and migration from 1.6.0.
5. Only then publish stable release. Upload signed releases.win.json and detached signature together with the matching full/delta assets. Keep private signing key outside source and CI artifacts.

No Authenticode certificate is available; the installer is unsigned by Windows. Release metadata signing is separate. No all-song lyrics or zero-stutter guarantee. No new paid service. Production 1.6.0 remains unchanged pending gates.
