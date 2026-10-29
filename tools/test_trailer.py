"""Tests of trailer.py on generated trailers: shots found at the cuts, slots on word starts, an exact frame count."""

import json
import os
import random
import subprocess
import sys

from trailer import assign, plan_slots

HERE = os.path.dirname(os.path.abspath(__file__))


def fake_trailer(path, seconds: int) -> None:
    """A 1280x720 trailer whose picture changes completely every 4 seconds, like hard cuts between shots."""
    sources = ["testsrc2", "smptehdbars", "rgbtestsrc", "testsrc"]
    count = seconds // 4
    inputs = sum((["-f", "lavfi", "-i", f"{sources[i % len(sources)]}=size=1280x720:rate=30:duration=4"] for i in range(count)), [])
    chain = "".join(f"[{i}:v]setsar=1,format=yuv420p,hue=h={i * 47}[v{i}];" for i in range(count))
    chain += "".join(f"[v{i}]" for i in range(count)) + f"concat=n={count}:v=1:a=0[out]"
    subprocess.run(["ffmpeg", "-y", "-v", "error", *inputs, "-filter_complex", chain, "-map", "[out]",
                    "-c:v", "libx264", "-preset", "ultrafast", str(path)], check=True)


def test_slots_start_on_words_and_cover_the_whole_video():
    starts = [i * 0.4 for i in range(60)]
    slots = plan_slots(starts, 30.0, random.Random(1))
    assert slots[0][0] == 0 and slots[-1][1] == 30.0
    assert all(a[1] == b[0] for a, b in zip(slots, slots[1:]))
    # Fast in the first five seconds, never longer than 2.6 s, and every cut while words are spoken on a word start
    # (the silent tail after the last word is filled with two-second slots).
    assert all(e - s <= 1.6 + 1e-9 for s, e in slots if e <= 5)
    assert all(e - s <= 2.6 + 1e-9 for s, e in slots)
    assert all(round(s, 3) in {round(t, 3) for t in starts} for s, _ in slots[1:] if s <= starts[-1])


def test_assign_alternates_trailers_and_reuses_only_when_out_of_shots():
    shots = [{"file": f, "start": k * 5.0, "end": k * 5.0 + 3.0, "motion": 10 - k, "center": 0.5}
             for f in ("a.mp4", "b.mp4") for k in range(3)]
    plan = assign([(i * 2.0, i * 2.0 + 2.0) for i in range(8)], shots)
    assert all(x["file"] != y["file"] for x, y in zip(plan, plan[1:]))
    firsts = plan[:6]
    assert len({(p["file"], p["start"]) for p in firsts}) == 6


def test_cuts_trailers_into_a_track_of_exactly_the_frames_asked(tmp_path):
    trailers = tmp_path / "trailers"
    trailers.mkdir()
    fake_trailer(trailers / "steam-1.mp4", 40)
    fake_trailer(trailers / "steam-2.mp4", 40)
    words = {"words": [{"text": "w", "start": round(i * 0.45, 3), "end": round(i * 0.45 + 0.4, 3)} for i in range(30)]}
    timings = tmp_path / "speech.json"
    timings.write_text(json.dumps(words), encoding="utf-8")
    out, cuts = tmp_path / "footage.mp4", tmp_path / "cuts.json"
    subprocess.run([sys.executable, os.path.join(HERE, "trailer.py"), "--trailers", str(trailers), "--timings", str(timings),
                    "--frames", "420", "--out", str(out), "--cuts", str(cuts), "--width", "270", "--height", "480"], check=True)
    probe = subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-show_entries", "stream=nb_read_frames,width,height",
                            "-of", "json", str(out)], capture_output=True, text=True, check=True)
    stream = json.loads(probe.stdout)["streams"][0]
    assert (stream["width"], stream["height"], int(stream["nb_read_frames"])) == (270, 480, 420)
    plan = json.loads(cuts.read_text(encoding="utf-8"))
    assert sum(p["frames"] for p in plan) == 420
    assert {p["file"] for p in plan} == {"steam-1.mp4", "steam-2.mp4"}
    # The shots were cached with the files they came from.
    assert json.loads((trailers / "shots.json").read_text(encoding="utf-8"))["files"].keys() == {"steam-1.mp4", "steam-2.mp4"}
