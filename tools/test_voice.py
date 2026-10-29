"""Tests of voice.py: joining takes with fixed pauses, and align.py's matching of numbers in Portuguese."""

import json
import os
import subprocess
import sys

import numpy as np
import soundfile as sf

from align import align, spoken_parts
from voice import SAMPLE_RATE, shorten_pauses, trim

HERE = os.path.dirname(os.path.abspath(__file__))


def tone(seconds: float, rate: int = SAMPLE_RATE, lead: float = 0.3) -> np.ndarray:
    t = np.arange(int(seconds * rate)) / rate
    body = 0.3 * np.sin(2 * np.pi * 220 * t).astype(np.float32)
    pad = np.zeros(int(lead * rate), np.float32)
    return np.concatenate([pad, body, pad])


def test_trim_and_pause_capping():
    audio = np.concatenate([tone(0.5), np.zeros(int(1.5 * SAMPLE_RATE), np.float32), tone(0.5)])
    trimmed = trim(audio)
    assert len(trimmed) < len(audio)
    capped = shorten_pauses(trimmed, 0.4)
    # About 0.5 + 0.4 (two leads around the gap merged into one long pause, capped) + 0.5 seconds.
    assert 1.3 < len(capped) / SAMPLE_RATE < 1.6


def test_assemble_joins_takes_with_section_pauses(tmp_path):
    takes = []
    for i, (seconds, rate) in enumerate([(1.0, 24000), (2.0, 44100)]):
        path = tmp_path / f"section-{i:02d}.wav"
        sf.write(path, tone(seconds, rate), rate)
        takes.append(str(path))
    plan = [{"audio": takes[0], "caption": "Em 1944, tudo mudou.", "spoken": "Em mil novecentos e quarenta e quatro, tudo mudou."},
            {"audio": takes[1], "caption": "O mar estava agitado naquela manhã.", "spoken": "O mar estava agitado naquela manhã."}]
    plan_path, out, timings_path = tmp_path / "plan.json", tmp_path / "voice.wav", tmp_path / "timings.json"
    plan_path.write_text(json.dumps(plan), encoding="utf-8")
    subprocess.run([sys.executable, os.path.join(HERE, "voice.py"), "assemble", "--plan", str(plan_path), "--out", str(out),
                    "--timings", str(timings_path), "--listener", "none", "--section-pause", "0.5"], check=True)
    timings = json.loads(timings_path.read_text(encoding="utf-8"))
    audio, rate = sf.read(out)
    assert rate == SAMPLE_RATE
    assert abs(len(audio) / rate - timings["duration"]) < 0.01
    # Two takes of about 1.1 and 2.1 s (trimmed, with edge pads) and a 0.5 s pause between them.
    assert 3.4 < timings["duration"] < 4.0
    first, second = timings["sections"]
    assert abs(second["start"] - first["end"] - 0.5) < 0.01
    assert [w["text"] for w in timings["words"]][:2] == ["Em", "1944,"]
    assert all(w["section"] == 1 for w in timings["words"][4:])


def test_align_matches_digits_to_spoken_portuguese_numbers():
    heard = [("Em", 0.0, 0.2), ("mil", 0.2, 0.4), ("novecentos", 0.4, 0.8), ("e", 0.8, 0.9), ("quarenta", 0.9, 1.2),
             ("e", 1.2, 1.3), ("quatro", 1.3, 1.6), ("tudo", 1.7, 1.9), ("mudou.", 1.9, 2.2)]
    times, score = align(["Em", "1944,", "tudo", "mudou."], heard, 0.0, 2.3, "pt")
    assert score == 1.0
    assert times[1] == (0.2, 1.6)
    assert spoken_parts("24.000", "pt") == ["vinte", "e", "quatro", "mil"]


def test_assemble_puts_the_set_pause_between_the_pieces_of_a_section(tmp_path):
    pieces = []
    for k in range(2):
        path = tmp_path / f"section-00-{k}.wav"
        sf.write(path, tone(1.0), SAMPLE_RATE)
        pieces.append(str(path))
    plan = [{"audio": pieces, "pauses": [0.45], "caption": "E o pior: a ideia foi dele.",
             "spoken": "E o pior: a ideia foi dele."}]
    plan_path, out, timings_path = tmp_path / "plan.json", tmp_path / "voice.wav", tmp_path / "timings.json"
    plan_path.write_text(json.dumps(plan), encoding="utf-8")
    subprocess.run([sys.executable, os.path.join(HERE, "voice.py"), "assemble", "--plan", str(plan_path), "--out", str(out),
                    "--timings", str(timings_path), "--listener", "none"], check=True)
    audio, rate = sf.read(out)
    # The silence between the two tones: the 0.45 s pause plus each piece's 0.05 s edge pad.
    quiet = np.flatnonzero(np.abs(audio) < 1e-4)
    runs = np.split(quiet, np.flatnonzero(np.diff(quiet) > 1) + 1)
    middle = max(len(r) for r in runs if r[0] > 0 and r[-1] < len(audio) - 1) / rate
    assert 0.5 < middle < 0.6
