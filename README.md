# CHARLI VISION

Eigenständige und kostenlose Windows-Desktop-Musikvisualisierung für Spotify mit rotierender Vinyl-Disc, Audio-Visualizer, dynamischen Albumfarben, Lyrics und Karaoke.

## Aktueller Release v1.3.0 — TRUE LIQUID GLASS REPAIR V4

- **Reparatur:** Keine zweite dunkle rechteckige Hintergrundebene mehr. Ein echter WPF-Transparenzkanal mit Alpha 0 in den geprüften Fensterecken, ein einzelner abgerundeter Glass-Host und nur darin platzierte Blur-, Farb- und Lichtschichten.
- **Scrollen:** Fehler im vertikalen ScrollBar-Track und in der selbstabbrechenden Smooth-Scroll-Animation behoben. Mausrad, Seitenbefehle, schnelle Richtungswechsel und echtes Ziehen des WPF-Scrollbar-Griffs auf Windows getestet.
- **Kompakt und responsiv:** Alle 24 Keybind-Aktionen auch bei 760px Fensterbreite erreichbar. Sechs Kategorien, kompakter Scrollviewer bei 490px Fensterhöhe, abrufbare fünf Animationsprofile.
- **Qualität:** 110 Selbsttests und 11 echte WPF-GUI-Prüfgruppen bestanden, Spotify-Sitzungstest bestanden. 0 Compilerwarnungen und 0 Fehler. Keine neue UI-Abhängigkeit.
- **Transparenzhinweis:** Die Desktop-Unschärfe wird einmal beim Fensteröffnen erfasst, kein kontinuierliches natives Acrylic. Hardware-Touchpad, geschützte Spiele und vollständige GPU-Present-FPS nicht unabhängig geprüft.
- Die offiziellen Windows-Updates verwenden weiterhin signierte RSA-PSS-Manifeste; der Windows-Installer selbst besitzt mangels Zertifikat **keine Authenticode-Signatur**.

Die ausführliche Ursachenanalyse mit Messungen und Grenzen: [V4_REPAIR_REPORT.md](V4_REPAIR_REPORT.md).

## Vorheriger Release v1.2.0 — Premium Glass / V3

- Das **geprüfte v1.2.0 Windows-x64-Setup** ist unter [GitHub Releases](https://github.com/yousefayat732-prog/Charli-Vision/releases) verfügbar. Es benötigt keine separat installierte .NET-Laufzeit.
- Vergrößertes Premium-Liquid-Glass-Design mit getönten Cards, Reflexen, 8 Neon-Vektorsymbolen, native Windows-11-DWM-Präferenz (wo unterstützt), schlankem Scrollbar und Slider.
- Reparierter runder 92-Band-Equalizer ohne abgeschnittene Balken und mit sauberem 0°/360°-Übergang.
- Einstellbare Vinyl-Grundgeschwindigkeit (24–52 RPM) und zuschaltbare, verlässliche musikadaptive RPM-Regelung. Unsicherer BPM ⇒ feste Geschwindigkeit.
- Verbessertes Karaoke-Glow nur auf Buchstaben, zeitgerechte Zeilenübergänge; echte LRCLIB Wort/Zeilen-Lyrics, optionale rechtmäßige Offline-WhisperX-Ausrichtung und ungesynchter Lesetext.
- Erweiterte Update-Seite: **„Update suchen und installieren“** führt Prüfung, Download und Signatur-/SHA-256-Verifikation mit einem Klick durch; der normale Windows-Installer bestätigt die Installation.
- **110 Selbsttests bestanden**, echte Internet-Lyrics-Suche und Offline-Wort-Ausrichtung geprüft. **Zwei echte One-Click-Update-Tests** (v1.0.0→v1.1.0 und v1.1.0→v1.2.0) einschließlich GitHub-Download, Windows-Installer, EXE-Prüfsumme und vollständiger Einstellungserhaltung wurden bestanden.
- Globale Tastenkombinationen werden nativ registriert, nur nach Aktivierung. Simulierte Tasteneingaben an andere Anwendungen blockierte Windows im Test; Drittanwendungen und Anti-Cheat-Spiele sind nicht als bestätigt markiert.
- Die Auslieferung besitzt ein signiertes RSA-PSS-Update-Manifest, aber **keine Windows-Authenticode-Signatur** (kein Zertifikat verfügbar); Windows SmartScreen kann warnen. Keine Sicherheitsabfragen umgehen.

**FPS-Angaben sind Zielprofile, keine GPU-Present-Garantie.** Die Performance ist von WPF, Monitor, GPU und Windows-Compositor abhängig. Benutzerprofile, lokale Lyrics und Einstellungen bleiben beim Update erhalten. Weitere Details und offen ausgewiesene Testgrenzen stehen in [RELEASE_NOTES.md](RELEASE_NOTES.md).

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

Unter Releases die Datei CharliVision-1.3.0-Setup-x64.exe herunterladen und unter Windows 10 oder 11 (64 Bit) starten. Sie enthält die .NET-Laufzeit, installiert ins Benutzerprofil, erzeugt einen Startmenüeintrag und optional eine Desktop-Verknüpfung. Deinstallation über Windows möglich. Die App benötigt keine Installation von Charli FPS.

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