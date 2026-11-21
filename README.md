# CHARLI VISION

Eigenständige und kostenlose Windows-Desktop-Musikvisualisierung für Spotify mit rotierender Vinyl-Disc, Audio-Visualizer, dynamischen Albumfarben, Lyrics und Karaoke.

## Neu in v1.1.0 — Chromatic UI & Lyrics-Verbesserungen

Diese Version bildet die **erste getestete Phase von Project Infinity Perfection V3**. Sie ist ausdrücklich **nicht** als Abschluss aller V3-Entwicklungsziele zu verstehen.

- Gemeinsame **ChromaticDesignEngine** mit maximal 25 Materialaktualisierungen pro Albumwechsel und ca. 720 ms sanfter Farbinterpolation
- Gemeinsame Farb-Tokens für Album-Vinyl, Equalizer, karaokeaktive Wörter, Text, Icons, Glasflächen, Auswahl und Hover
- Neue, leichtgewichtige **WPF-Vektoricons** für alle sechs Einstellungsbereiche sowie Minimieren und Schließen
- Farbharmonisierung mit Lesbarkeitsprüfung für Primär-, Sekundär- und aktive Texte
- Ruhiger automatischer Lesetext-Scroller ohne manuelle Seiten-Buttons. Er behauptet bewusst **keine** erfundenen Wort-Zeitmarken.
- Konservativer lokaler Audio-Onset-Tempo-Estimator; passt Vinyl-Drehgeschwindigkeit nur bei **stabil erkanntem Takt** an, sonst bleibt 33⅓ RPM
- Weniger überstrahlendes Karaoke-Glow und gemeinsame, interruptible Smooth-Scroll-Animation
- Dezent verschwommener, einmaliger In-Memory-Hintergrundausschnitt im erweiterten Einstellungsfenster (kein kontinuierliches Live-Acrylic)
- Alle vorhandenen Windows-Hotkeys und die signaturgesicherte GitHub-Update-Architektur bleiben erhalten.

**Teststand:** 102 automatische Selbsttests bestanden. Echte WPF-Visual-Previews und 180-Hz-Frame-Test wurden durchgeführt. Tests auf anderer Hardware, vollständiger GitHub-v1.0.0→v1.1.0-One-Click-Update-Test und die weiteren V3-Designphasen sind nicht als abgeschlossen verifiziert.


## Installation

Unter Releases die Datei CharliVision-1.1.0-Setup-x64.exe herunterladen und unter Windows 10 oder 11 (64 Bit) starten. Sie enthält die .NET-Laufzeit, installiert ins Benutzerprofil, erzeugt einen Startmenüeintrag und optional eine Desktop-Verknüpfung. Deinstallation über Windows möglich. Die App benötigt keine Installation von Charli FPS.

Der Installer ist noch NICHT mit Windows Authenticode signiert; SmartScreen kann warnen. Prüfsummen stehen im Release bereit.

## Funktionen

- Windows Spotify-Mediasession, Wiedergabesteuerung und Vinyl mit Albumcover
- 92-Band-Audiovisualizer, albumabhängige Farben und automatische Lyrics
- Wortgenaues Karaoke, sofern die Quelle echte Wort-Zeitstempel bereitstellt
- Desktop-only-Overlay und System-Tray
- 30/60/120/180-FPS-Zielprofile, abhängig von Windows und Display (keine FPS-Garantie)
- Globale Windows-Hotkeys nach ausdrücklicher Aktivierung. Play/Pause: Strg+Alt+P.
- App > Update Center: manuelle und optionale automatische UPDATE-PRÜFUNG. Updates werden nie ungefragt installiert.
- Keine kostenpflichtigen API-Keys für Hauptfunktionen.

## Update-Sicherheit

Der Updater akzeptiert nur signierte Release-Metadaten mit SHA-256 und RSA-PSS-Signatur eines fest eingebetteten öffentlichen Schlüssels sowie den passenden Hash und die Dateigröße des offiziellen Installers. Private Release-Schlüssel werden nicht veröffentlicht. Unsichere Updates werden abgelehnt.

## Build

Windows 10/11 mit .NET 10 SDK:

    dotnet restore CharliVision.csproj
    dotnet build CharliVision.csproj -c Release
    dotnet publish CharliVision.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win-x64

Installer mit Inno Setup 6.7.4 aus installer/CharliVision.iss bauen, nachdem die self-contained EXE unter dist/win-x64/CharliVision.exe vorliegt. Optionaler Python-Audio-Alignmentcode unter LocalAlignment; große Modelle und Python-Laufzeiten werden NICHT mitgeliefert.

## Persönliche Daten

Einstellungen und eigene Lyrics-Korrekturen liegen im Benutzerprofil unter %APPDATA%\CharliVision. Installieren oder Deinstallieren löscht diese persönlichen Daten nicht pauschal. Lyrics-Abfragen gehen bei aktivierter Suche an externe Anbieter. Die App wird nicht von Spotify offiziell unterstützt.

Siehe LICENSE, THIRD_PARTY_NOTICES.md und RELEASE_NOTES.md. Die Grafik docs/images/vinyl-test-preview.png ist ein tatsächlicher WPF-Test-Render mit synthetischem Testcover.