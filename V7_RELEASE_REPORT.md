# CHARLI VISION v1.6.0 — RELEASE-ABNAHME V7

## Umfang
- Kostenloser Better-Lyrics-Cache-Fallback als zusätzliche Lyrics-Quelle aus dem YouTube-Music-Umfeld; **keine offizielle YouTube-Music-API**.
- Bestehende LRCLIB-Priorisierung und lyrics.ovh-Fallback bleiben erhalten.
- Native TTML-Wortzeitmarken ohne erfundene Synchronisierung, XML-DTD/External-Entities verboten.
- Wiederverwendung von WPF-Karaoke-Farbressourcen und bestätigte vorhandene Visualisierungsfunktionen.

## Build und Test — ausgeführt auf dem Windows-Gerät
- `dotnet publish -c Release -r win-x64 --self-contained true`: PASS.
- Ausgelieferter selbstenthaltender `CharliVision.exe` Release 1.6.0: Selbsttests PASS, 0 fehlgeschlagen.
- Live LRCLIB: PASS, 47 synchronisierte Cues.
- Live Word-Sync: PASS, 75 Zeilen / 224 echte Zeitsegmente.
- Live Better-Lyrics-Cache: PASS, 36 Zeilen / 273 zeitcodierte Wortsegmente.
- `--v6-overlay-qa`: PASS, echtes WPF-Rendering der Modi Word/Line/Plain (synthetische Testuhr).
- `--lyrics-coverage-test`: PASS, 14 Szenarien.
- `--v4-window-qa`: PASS, Liquid-Glass-/Scroll-Oberflächentests.
- Benutzerkonfiguration vor/nach diesen Tests SHA-256-identisch.
- GitHub Actions Windows Build und Publish: grüner Build für V7-Quellcode.

## Sicherheitsprüfung
- Update-App enthält bestehenden RSA-3072-PSS-Public-Key, der gegen den alten privaten Schlüssel lokal abgeglichen wurde.
- Installer durch Inno Setup kompiliert, als Windows x64.
- Installer SHA-256 und exakte Größe sind im Manifest `CharliVision-update.json` gebunden.
- Manifest RSA-PSS-SHA256-signiert, Signatur gegen denselben öffentlichen Schlüssel erfolgreich verifiziert.
- Der private Schlüssel bleibt nur lokal; weder Repo noch Installer enthalten ihn.
- Keine Windows-Authenticode-Signatur; beim ersten Start kann Windows eine Warnung anzeigen.

## Einschränkungen
- Kein garantierter Lyrics-Treffer für jeden Song, da Anbieter-Verfügbarkeit schwankt.
- Das ist kein offizieller YouTube-Music-API-Zugang und kein Extrahieren geschützter Spotify-Musik.
- Wort-Sync wurde mit echten Provider-Zeitmarken und kontrollierter Testuhr geprüft, nicht als absolute Null-Latenz-Garantie.
- Dauerhafte hardwareseitige 120-FPS-Garantie wurde nicht erteilt.
- Der Nutzer führt das Update selbst über die App aus; die bestehende Installation wurde während des Builds nicht überschrieben.
