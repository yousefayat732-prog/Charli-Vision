# CHARLI VISION — v1.1.0 (Windows x64)

## Project Infinity Perfection V3 — Chromatic UI Foundation

**Neu / verbessert**
- Zentrale ChromaticDesignEngine und gemeinsam synchronisierte Album-Farbwelt für Vinyl, Lyrics, Glass-Highlights, Symbole, Buttons und Texte.
- Palette wird asynchron aus einem eingefrorenen, skalierten Albumcover berechnet, der UI-Übergang läuft mit nur einer zeitgesteuerten 720-ms-Farbinterpolation (maximal 25 Stufen).
- Primäre/sekundäre/aktive Text-Tokens mit geprüften Mindestkontrasten gegen die dunkle Glasbasis.
- Acht neue leichtgewichtige WPF-Vektorsymbole (sechs Einstellungsbereiche, Minimieren, Schließen), dynamisch über gemeinsame Brushes gefärbt.
- Gefrorene Albumcover-Textur und bestehender 92-Band-Visualizer bleiben erhalten.
- Automatisches Scrollen nicht synchronisierter Lyrics statt „Seite 1/10“ oder manuellen Seitenwechseln; ausdrücklich **ohne erfundene Synchronisierung**.
- Kleinerer, weniger fleckiger Glow am aktiven Karaoke-Wort; farbiger Glyphen-Gradient bleibt wortzeitbasiert.
- Konservativer BPM-Schätzer aus lokalen, tatsächlich erfassten Bassimpulsen. Bei unzuverlässigem Takt gilt stabiler 33⅓-RPM-Fallback.
- Sanftes, unterbrechbares Scrolling im Glasmenü und im erweiterten Einstellungsfenster.
- Subtiler einmaliger lokaler Frosted-Backdrop im großen Einstellungsfenster; nicht als live aktualisiertes Mica beworben.
- Global-Hotkeys, Spotify-Steuerung, Desktop-only-Overlay, persönliche Einstellungen und signaturgeprüftes GitHub Update Center bleiben erhalten.

**Verifiziert**
- .NET 10 Release Build: 0 Fehler, 0 Warnungen.
- 102 automatisierte Selbsttests bestanden; 0 fehlgeschlagen.
- Lokaler Spotify-Sitzungstest bestanden.
- WPF High-Refresh-Test (180 Hz Monitor, GTX 1070 Ti): Durchschnitt 5,74 ms, P95 7,45 ms, P99 10,22 ms, max 27,24 ms zwischen UI-Callbacks. Keine Garantie für GPU-Present-FPS.
- Screenshots beider WPF-Menüs aus der laufenden App manuell geprüft.
- Windows x64 Self-contained-Installer: Installation, 102 Selbsttests und Deinstallation im getrennten QA-Zielordner bestanden.
- Persönliche Einstellungen durch Install/Uninstall unverändert erhalten.

**Offen / bekannte Einschränkungen**
- Kein Authenticode-Code-Signing-Zertifikat: Windows SmartScreen kann warnen. Der Update-Kanal verwendet davon unabhängig ein eigenes RSA-PSS-signiertes Manifest mit SHA-256.
- Nicht alle im umfangreichen Master-Prompt „Infinity Perfection V3“ beschriebenen Funktionen wurden abgeschlossen; insbesondere exakte Reproduktion sämtlicher gelieferten Bildreferenzen, hardwareübergreifende FPS-Garantien und ein produktionsnaher One-Click-Updater-Test mit zwei veröffentlichten Builds müssen noch weiter getestet werden.
- Musikadaptive Drehzahl ist nur bei genügend stabiler, zuverlässiger lokaler Tempoerkennung aktiv; die App erfindet keine BPM.
- Keine Spotify-DRM-Umgehung oder unauthorisierten Downloads.

## v1.0.0

Erste eigenständige, öffentlich getestete Windows-x64-Version mit Spotify-Integration, Vinyl, Audiovisualizer, Lyrics, globalen Hotkeys und signaturgeschützter Update-Infrastruktur.
