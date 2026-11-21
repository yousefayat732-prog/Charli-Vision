# CHARLI VISION — LOCAL FORCED ALIGNMENT (2026-11-21)

## Existing WPF app, optional offline AI
Engine: WhisperX 3.8.6, Python 3.11.11, PyTorch 2.8.0+cpu,
imageio-ffmpeg 0.6.0, faster-whisper tiny.en as independent text check.
Only explicitly selected legal WAV/MP3/FLAC/M4A plus own TXT/LRC.
No Spotify recording, no password access, no audio uploads.
Python runs only when invoked, with a maximum of four CPU threads.
The main WPF app starts normally even if AI is unavailable.

## Settings workflow
Erweiterte Einstellungen -> Lyrics -> Sprache -> Eigene Audio + Text auswählen.
Audio is decoded locally, recognized words sanity-checked (wrong text
is rejected), word timestamps calculated with CTC forced alignment,
then returned for preview with the original KaraokeWords renderer.
Cancel kills the worker. Preview can scrub/play the local file.
Export Enhanced LRC/YAML, or explicitly confirm that the file matches
the currently playing Spotify version before saving into LyricsLibrary.
Confirmed word timings are reused automatically for the same song.

## Paths
Python/venv: <PROJECT_ROOT>\LocalAlignment\.venv
Worker: <PROJECT_ROOT>\LocalAlignment\align_audio.py
Config: %APPDATA%\CharliVision\alignment-engine.json
Models: <PROJECT_ROOT>\LocalAlignment\models
Result cache: %LOCALAPPDATA%\CharliVision\AlignmentResults
Up to 40 cached alignment outputs keyed by SHA256 of audio/text contents
and language. Only results, no original audio. Requirements versions:
requirements.txt and requirements-lock.txt. The large optional Python
environment is not embedded in CharliVision.exe or installed system-wide.

## Verification and limits
Windows-generated English speech sample: 20/20 words, 3/3 lines.
Faster repeated speech sample: 24/24 words, 3/3 lines.
Unrelated wrong lyrics: independently measured 12% similarity, rejected.
Real song accuracy (rap, singers, languages, instrumentals) is not
guaranteed by this synthetic speech test. No exact Spotify acoustic
offset claim. Only legally supplied local audio can be analyzed.
