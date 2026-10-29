"""Narration helpers for Minuto Util, CPU only.

Commands:
  assemble  Joins the takes of each section (any WAV from Smallest.ai or Kokoro) into voice.wav: silence at the edges of
            each take is trimmed, pauses inside a take are capped, a section spoken in pieces gets each piece's set
            pause after it (the voice reads straight through colons), and a fixed pause goes between sections. Then
            faster-whisper (small, CPU, int8) times every word, matched to the script by align.py, so the captions
            keep the script's spelling with Whisper's timing.
  kokoro    The fallback voice: Kokoro pm_santa speaks each line of --text-file into --out-dir/section-NN.wav.

Usage:
  uv run voice.py assemble --plan plan.json --out voice.wav --timings timings.json [--listener whisper|none]
                           [--section-pause 0.4] [--max-pause 0.55]
  uv run voice.py kokoro --text-file lines.txt --out-dir folder [--voice pm_santa] [--speed 1.0]

plan.json: [{"audio": ["section-00-0.wav", "section-00-1.wav"], "pauses": [0.45], "caption": "script text",
             "spoken": "text the voice was given"}, ...]; "audio" may be one file, and "pauses" (seconds after each
           piece but the last) may be left out for a single take.
timings.json: {"duration", "sections": [{"start", "end"}], "words": [{"text", "start", "end", "section"}],
               "checks": [{"section", "score", "words"}]}
"""

import argparse
import json
import sys

import numpy as np
import soundfile as sf

from align import align

SAMPLE_RATE = 24000
WHISPER_RATE = 16000
# Keeps the start and end of a take from being clipped when its silence is trimmed.
EDGE_PAD = 0.05
FRAME = 0.02
SILENCE_DB = 40


def load_mono(path: str) -> np.ndarray:
    """Any WAV as 24 kHz mono float32."""
    from scipy.signal import resample_poly

    audio, rate = sf.read(path, dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if rate != SAMPLE_RATE:
        from math import gcd

        g = gcd(SAMPLE_RATE, rate)
        audio = resample_poly(audio, SAMPLE_RATE // g, rate // g).astype(np.float32)
    return audio


def frame_db(audio: np.ndarray) -> np.ndarray:
    """Level of each 20 ms frame in dB below the loudest frame."""
    hop = int(FRAME * SAMPLE_RATE)
    count = len(audio) // hop
    if count == 0:
        return np.zeros(0)
    rms = np.sqrt(np.mean(audio[: count * hop].reshape(count, hop) ** 2, axis=1))
    peak = rms.max()
    if peak <= 0:
        return np.full(count, -120.0)
    return 20 * np.log10(rms / peak + 1e-12)


def trim(audio: np.ndarray) -> np.ndarray:
    """Cuts leading and trailing silence, keeping a short pad."""
    levels = frame_db(audio)
    loud = np.flatnonzero(levels > -SILENCE_DB)
    if len(loud) == 0:
        return audio
    hop = int(FRAME * SAMPLE_RATE)
    pad = int(EDGE_PAD * SAMPLE_RATE)
    start = max(loud[0] * hop - pad, 0)
    end = min((loud[-1] + 1) * hop + pad, len(audio))
    return audio[start:end]


def shorten_pauses(audio: np.ndarray, max_pause: float) -> np.ndarray:
    """Cuts every silence longer than max_pause down to max_pause, keeping half of it on each side of the cut."""
    hop = int(FRAME * SAMPLE_RATE)
    quiet = frame_db(audio) < -SILENCE_DB
    count = len(quiet)
    keep = int(round(max_pause / FRAME))
    pieces, last, frame = [], 0, 0
    while frame < count:
        if not quiet[frame]:
            frame += 1
            continue
        run_end = frame
        while run_end < count and quiet[run_end]:
            run_end += 1
        if run_end - frame > keep:
            pieces.append(audio[last : (frame + keep // 2) * hop])
            last = (run_end - (keep - keep // 2)) * hop
        frame = run_end
    pieces.append(audio[last:])
    return np.concatenate(pieces)


class Listener:
    """faster-whisper on the CPU. It gets a 16 kHz float32 array: a file path fails in some av builds."""

    def __init__(self, language: str = "pt"):
        from faster_whisper import WhisperModel

        self.language = language
        self.whisper = WhisperModel("small", device="cpu", compute_type="int8")

    def listen(self, audio: np.ndarray) -> list[tuple[str, float, float]]:
        from scipy.signal import resample_poly

        samples = resample_poly(audio, 2, 3).astype(np.float32)
        segments, _ = self.whisper.transcribe(samples, language=self.language, word_timestamps=True, beam_size=5,
                                              condition_on_previous_text=False, vad_filter=False)
        return [(w.word.strip(), w.start, w.end) for seg in segments for w in seg.words or [] if w.word.strip()]


def join_pieces(item: dict, max_pause: float) -> np.ndarray:
    """One section's audio: each piece trimmed and capped, then followed by its set pause."""
    files = item["audio"] if isinstance(item["audio"], list) else [item["audio"]]
    pauses = list(item.get("pauses") or []) + [0.0] * len(files)
    parts = []
    for k, (path, pause) in enumerate(zip(files, pauses)):
        parts.append(shorten_pauses(trim(load_mono(path)), max_pause))
        if pause > 0 and k < len(files) - 1:
            parts.append(np.zeros(int(round(pause * SAMPLE_RATE)), dtype=np.float32))
    return np.concatenate(parts)


def assemble(plan: list[dict], listener, section_pause: float, max_pause: float):
    """Returns (audio, timings dict)."""
    parts, words, sections, checks = [], [], [], []
    offset = 0.0
    for index, item in enumerate(plan):
        if index > 0:
            parts.append(np.zeros(int(section_pause * SAMPLE_RATE), dtype=np.float32))
            offset += section_pause
        audio = join_pieces(item, max_pause)
        length = len(audio) / SAMPLE_RATE
        script_words = item["caption"].split()
        heard = listener.listen(audio) if listener else []
        times, score = align(script_words, heard, 0.0, length, "pt")
        for word, (s, e) in zip(script_words, times):
            words.append({"text": word, "start": round(offset + s, 3), "end": round(offset + e, 3), "section": index})
        checks.append({"section": index, "score": round(score if listener else 0.0, 3), "words": len(script_words)})
        sections.append({"start": round(offset, 3), "end": round(offset + length, 3)})
        parts.append(audio)
        offset += length
    audio = np.concatenate(parts)
    return audio, {"duration": round(len(audio) / SAMPLE_RATE, 3), "sections": sections, "words": words,
                   "checks": checks}


def kokoro(lines: list[str], out_dir: str, voice: str, speed: float) -> None:
    import os

    from kokoro import KPipeline

    # The first letter of a Kokoro voice is its language: p = Brazilian Portuguese.
    pipeline = KPipeline(lang_code=voice[0], repo_id="hexgrad/Kokoro-82M", device="cpu")
    for index, line in enumerate(lines):
        chunks = []
        for result in pipeline(line, voice=voice, speed=speed, split_pattern=None):
            audio = result.audio.cpu().numpy() if hasattr(result.audio, "cpu") else np.asarray(result.audio)
            chunks.append(audio.astype(np.float32))
        sf.write(os.path.join(out_dir, f"section-{index:02d}.wav"), np.concatenate(chunks), SAMPLE_RATE)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    a = commands.add_parser("assemble")
    a.add_argument("--plan", required=True)
    a.add_argument("--out", required=True)
    a.add_argument("--timings", required=True)
    a.add_argument("--listener", choices=["whisper", "none"], default="whisper")
    a.add_argument("--section-pause", type=float, default=0.4)
    a.add_argument("--max-pause", type=float, default=0.55)
    k = commands.add_parser("kokoro")
    k.add_argument("--text-file", required=True)
    k.add_argument("--out-dir", required=True)
    k.add_argument("--voice", default="pm_santa")
    k.add_argument("--speed", type=float, default=1.0)
    args = parser.parse_args()

    if args.command == "kokoro":
        with open(args.text_file, encoding="utf-8") as f:
            lines = [line.strip() for line in f if line.strip()]
        if not lines:
            print("No text to speak.", file=sys.stderr)
            return 1
        kokoro(lines, args.out_dir, args.voice, args.speed)
        return 0

    with open(args.plan, encoding="utf-8") as f:
        plan = json.load(f)
    if not plan:
        print("The plan has no takes.", file=sys.stderr)
        return 1
    listener = Listener("pt") if args.listener == "whisper" else None
    audio, timings = assemble(plan, listener, args.section_pause, args.max_pause)
    sf.write(args.out, audio, SAMPLE_RATE, subtype="PCM_16")
    with open(args.timings, "w", encoding="utf-8") as f:
        json.dump(timings, f, ensure_ascii=False, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
