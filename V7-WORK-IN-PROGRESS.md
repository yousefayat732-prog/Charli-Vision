# CHARLI VISION V7 — YouTube-Music-Lyrics-Fallback (Testkandidat, kein öffentliches Update)

Stand: 21.11.2026. Benutzer bestätigt die übrigen Funktionen als funktionsfähig; diese Freigabe bezieht sich auf die neu untersuchte Lyrics-Quelle, nicht auf eine erneute vollständige Performance-/Hardwarezertifizierung.

## Neuer kostenloser Lyrics-Fallback
- LRCLIB bleibt der primäre zeitcodierte Provider.
- Bei fehlenden synchronisierten LRCLIB-Lyrics wird die dokumentierte Better Lyrics API (`api.betterlyrics.org/getLyrics`) ohne API-Key als **Cache-Lookup** abgefragt. Better Lyrics ist ein Drittanbieter aus dem YouTube-Music-Ökosystem, nicht die offizielle YouTube-Music-API.
- Es werden Artist, Titel, Album und echte Spieldauer im Cache-Schlüssel übermittelt. Ohne gültige Dauer bzw. bei ungesicherter Titelvariante wird nicht abgefragt.
- Nicht gecachte Songs können HTTP 401 liefern. Es werden keine Schlüssel eingekauft/erfunden, keine YouTube-Cookies ausgelesen und keine geschützten Streamingdaten extrahiert. Danach greifen bestehende LRCLIB-Plain-/lyrics.ovh-Fallbacks.
- Bei gültigem TTML: echte Zeilen- und Wort-Zeitstempel in die bestehenden WPF-Karaoke-Renderer übernehmen, keine künstlichen Zeiten.
- DTD/External Entities im XML-Parser gesperrt, Antwortgrößen begrenzt, beschädigte Dokumente verworfen, Provider-Cooldowns berücksichtigt.

## Nachgewiesene Tests
- Windows Build Release: 0 Fehler, 0 Warnungen.
- Automatisierte Tests: 0 fehlgeschlagene; V7-Lyrics-Fallback-Tests inkl. HTTP-401-Failover und DTD-Sicherheit bestanden.
- Echte externe Better Lyrics API (öffentlich gecachter Beispielsong): HTTP 200, 36 Zeilen, 273 tatsächlich zeitcodierte Wortsegmente via produktivem C#-Parser.
- Bereits zuvor: LRCLIB Live-Sync, Online-Lyrics-Abdeckung, WPF-Word-/Line-/Plain-Renderer, Spotify-Songerkennung und lokales Alignment erfolgreich getestet.
- Vorsicht: Ein erfolgreiches Beispiel ist keine Garantie für alle Songs und keine offizielle YouTube-Music-Schnittstelle.

## Version und Release
- Projektversion weiterhin 1.5.0; daher kein gültiges eigenständiges Update über den integrierten Versionsvergleich.
- Für Update in der bestehenden App müssen neuer Installer, höherer Versionsstring, SHA-256-Manifest, RSA-PSS-Signatur mit dem **vorhandenen vertrauenswürdigen privaten Projektschlüssel** und GitHub-Release samt Upgrade-Test bereitgestellt werden.
- Niemals ein nicht signiertes oder mit einem neuen inkompatiblen Schlüssel signiertes Release als funktionsfähiges Auto-Update ausgeben.
- Der Windows-Testkandidat ist nicht die installierte Version. Persönliche Einstellungen/Charli FPS nicht verändern.
- Release-Signierschlüssel, Github-Release-Uploadwerkzeug und Installer-Compiler müssen im Ausführungssystem verfügbar und verifiziert sein, bevor ein echtes Update veröffentlicht werden darf.

## Pfade
- Quelle: C:\Users\ayatk\source\CharliVision
- Backup: C:\Users\ayatk\source\CharliVision-V7-Backup-20261121-verified
- Neuer Testkandidat: C:\Users\ayatk\source\CharliVision-V7-YouTubeFallback-Candidate-UNRELEASED