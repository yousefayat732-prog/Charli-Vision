#!/usr/bin/env python3
"""CHARLI VISION optional offline audio/lyrics forced alignment.

Requires explicit local audio+lyrics files. No Spotify stream access; no audio upload.
Writes only actual word timestamps returned by WhisperX forced alignment.
"""
from __future__ import annotations
import argparse
import json
import os
import re
import shutil
import sys
import time
import unicodedata
from difflib import SequenceMatcher
from pathlib import Path

SUPPORTED_AUDIO = {".wav", ".mp3", ".flac", ".m4a"}
SUPPORTED_TEXT = {".txt", ".lrc"}
MAX_DURATION_SEC = 8 * 60
MAX_AUDIO_BYTES = 250 * 1024 * 1024

def progress(phase: str, percent: int) -> None:
    print("PROGRESS\t" + str(percent) + "\t" + phase, flush=True)

def normalized(value: str) -> str:
    return "".join(c for c in value.casefold() if unicodedata.category(c)[0] in "LN")

def lines_from_lyrics(path: Path) -> list[str]:
    if path.stat().st_size > 200_000:
        raise ValueError("Lyrics file too large (>200 KB)")
    raw = path.read_text(encoding="utf-8-sig").replace("\r", "")
    if path.suffix.lower() == ".lrc":
        output = []
        for line in raw.splitlines():
            if re.match(r"^\[(?:ar|ti|al|by|offset):", line, re.IGNORECASE):
                continue
            text = re.sub(r"\[(?:\d{1,3}):\d{2}(?:[.,]\d{1,3})?\]", "", line).strip()
            if text:
                output.append(text)
    else:
        output = [x.strip() for x in raw.splitlines() if x.strip()]
    output = [x for x in output if len(x) <= 300 and normalized(x)]
    if not output or len(output) > 500:
        raise ValueError("No useful lyrics found or too many lines")
    if sum(len(x) for x in output) > 40_000:
        raise ValueError("Lyrics too long")
    return output

def verify_own_lyrics(samples, lines: list[str], language: str, cache: Path) -> float:
    """Independent small local ASR sanity check; a CTC aligner can force wrong text."""
    import gc
    from faster_whisper import WhisperModel
    progress("Prüfe Audio und eigenen Liedtext unabhängig", 19)
    model_id = "tiny.en" if language == "en" else "tiny"
    asr = WhisperModel(model_id, device="cpu", compute_type="int8",
                       download_root=str(cache / "asr-models"),
                       cpu_threads=min(4, max(1, os.cpu_count() or 4)),
                       num_workers=1)
    transcript, _ = asr.transcribe(
        samples, language=language, beam_size=1,
        condition_on_previous_text=False, vad_filter=False)
    spoken = " ".join(segment.text for segment in transcript)
    del asr
    gc.collect()
    expected_tokens = [normalized(w) for w in " ".join(lines).split()]
    actual_tokens = [normalized(w) for w in spoken.split()]
    expected_tokens = [w for w in expected_tokens if w]
    actual_tokens = [w for w in actual_tokens if w]
    similarity = SequenceMatcher(
        None, expected_tokens, actual_tokens, autojunk=False).ratio()
    # Strong mismatch: refuse confident word highlighting, rather than
    # forcing unrelated lyric tokens onto arbitrary audio.
    if similarity < 0.50:
        raise ValueError("Text/Audiodatei stimmen nicht ausreichend überein "
                         "(lokaler Vergleich: "+str(round(similarity, 2))+"). "
                         "Bitte dieselbe Songversion und Sprache auswählen.")
    return round(similarity, 4)

def setup_ffmpeg(cache: Path) -> None:
    # imageio-ffmpeg supplies a compatible local FFmpeg executable.
    import imageio_ffmpeg
    tool = Path(imageio_ffmpeg.get_ffmpeg_exe())
    bin_dir = cache / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    ffmpeg = bin_dir / "ffmpeg.exe"
    if not ffmpeg.exists() or ffmpeg.stat().st_size != tool.stat().st_size:
        shutil.copyfile(tool, ffmpeg)
    os.environ["PATH"] = str(bin_dir) + os.pathsep + os.environ.get("PATH", "")

def validate_paths(audio: Path, lyrics: Path, output: Path, cache: Path) -> None:
    if audio.suffix.lower() not in SUPPORTED_AUDIO or not audio.is_file():
        raise ValueError("Audio must be an existing WAV, MP3, FLAC or M4A file")
    if lyrics.suffix.lower() not in SUPPORTED_TEXT or not lyrics.is_file():
        raise ValueError("Lyrics must be an existing local TXT or LRC file")
    if audio.stat().st_size <= 0 or audio.stat().st_size > MAX_AUDIO_BYTES:
        raise ValueError("Audio is empty or larger than 250 MB")
    if output == audio or output == lyrics:
        raise ValueError("Output must be a separate file")
    cache.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)

def run(args: argparse.Namespace) -> int:
    audio_path = Path(args.audio).resolve(strict=True)
    text_path = Path(args.lyrics).resolve(strict=True)
    out_path = Path(args.output).resolve()
    cache_path = Path(args.cache).resolve()
    validate_paths(audio_path, text_path, out_path, cache_path)
    if args.language not in ("en", "de", "fr", "es", "it"):
        raise ValueError("Supported language codes: en, de, fr, es, it")
    text_lines = lines_from_lyrics(text_path)
    started = time.monotonic()
    progress("Initialisiere lokale Audio-Engine", 2)
    os.environ["HF_HOME"] = str(cache_path / "huggingface")
    os.environ["TORCH_HOME"] = str(cache_path / "torch")
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    setup_ffmpeg(cache_path)
    import torch
    import whisperx
    torch.set_num_threads(min(4, max(1, os.cpu_count() or 4)))
    torch.set_num_interop_threads(1)
    progress("Dekodiere lokale Audiodatei", 10)
    samples = whisperx.load_audio(str(audio_path))
    duration = len(samples) / 16000
    if not 0.5 < duration <= MAX_DURATION_SEC:
        raise ValueError("Audio must be 0.5 to 480 seconds; duration=" + str(round(duration, 1)))
    consistency = verify_own_lyrics(samples, text_lines, args.language, cache_path)
    progress("Lade sprachspezifisches Forced-Alignment-Modell", 40)
    model, metadata = whisperx.load_align_model(
        language_code=args.language, device="cpu",
        model_dir=str(cache_path / "align-models"))
    progress("Richte Text an den Audiosignalen aus", 60)
    transcript = [{"start": 0.0, "end": duration, "text": " ".join(text_lines)}]
    result = whisperx.align(transcript, model, metadata, samples, "cpu",
                            return_char_alignments=False, print_progress=False)
    aligned = [
        w for w in result.get("word_segments", [])
        if isinstance(w.get("start"), (int, float))
           and isinstance(w.get("end"), (int, float))
           and w.get("end") >= w.get("start")
    ]
    progress("Prüfe tatsächliche Wort-Zeitstempel", 88)
    # Strict monotonic token matching. Unmatched text is NOT assigned invented times.
    pos = 0
    lines = []
    uncertain = []
    good = 0
    total = 0
    for line_no, line in enumerate(text_lines, 1):
        segments = re.findall(r"\S+\s*", line)
        words = []
        bad = False
        for token in segments:
            token_id = normalized(token)
            if not token_id:
                continue
            total += 1
            if pos >= len(aligned):
                bad = True
                continue
            word = aligned[pos]
            candidate = normalized(word.get("word", ""))
            if candidate != token_id:
                # Do not attach an unrelated sung word to a lyric token.
                bad = True
                continue
            pos += 1
            score = float(word.get("score") or 0)
            start = float(word["start"])
            end = float(word["end"])
            if not 0 <= start <= end <= duration + 0.1 or score < 0.20:
                bad = True
                continue
            words.append({
                "text": token,
                "start_ms": int(round(start * 1000)),
                "end_ms": int(round(end * 1000)),
                "score": round(score, 3),
            })
            good += 1
        if words:
            # Only word-sync a line when every token has a verified match.
            current = {
                "text": line, "start_ms": words[0]["start_ms"],
                "words": words if not bad and len(words) == len(segments) else [],
            }
            lines.append(current)
        if bad or len(words) != len(segments):
            uncertain.append(line_no)
    if not lines or good < 2:
        raise ValueError("No reliable alignment found; check language, text and audio")
    coverage = round(good / max(1, total), 4)
    payload = {
        "engine": "whisperx-forced-alignment",
        "device": "cpu",
        "language": args.language,
        "duration_ms": int(round(duration * 1000)),
        "coverage": coverage,
        "asr_text_consistency": consistency,
        "matched_words": good,
        "source_word_count": total,
        "uncertain_lines": uncertain,
        "lines": lines,
        "elapsed_sec": round(time.monotonic() - started, 1),
    }
    tmp = out_path.with_suffix(out_path.suffix + ".tmp")
    tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(out_path)
    progress("Fertig: echte Wortzeitstempel gespeichert", 100)
    print("RESULT\t" + str(out_path), flush=True)
    return 0

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Offline alignment with user-owned local audio")
    parser.add_argument("--audio", required=True)
    parser.add_argument("--lyrics", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--cache", required=True)
    parser.add_argument("--language", default="en")
    config = parser.parse_args()
    try:
        sys.exit(run(config))
    except Exception as exc:
        print("ERROR\t" + type(exc).__name__ + ": " + str(exc), file=sys.stderr, flush=True)
        sys.exit(1)
