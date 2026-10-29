"""The picture track of a trailer video (media.footage "trailers"), CPU only.

Steps:
  shots   Finds the shots in every trailer (a cut is a frame difference far above its neighbours), scores each one for
          motion and remembers where the action is horizontally, so the 9:16 crop follows it. The first seconds and
          the last 15% of a trailer (logos, titles, rating cards), short and dark shots are left out. Cached in
          shots.json next to the trailers, with the size of each file it was made from.
  plan    Cuts the narration into slots of 1.3 to 2.6 seconds that start on a word (1.0 to 1.6 s in the first five
          seconds, where the viewer decides to stay), and gives each slot an unused shot, the most moving first,
          never the same trailer twice in a row.
  render  Cuts each slot from its trailer (cropped on the action, with a slow push in so no slot is static) and joins
          them into one silent video of exactly --frames frames. cuts.json lists what went where.

Usage:
  uv run trailer.py --trailers dir --timings speech.json --frames 1650 --out footage.mp4 --cuts cuts.json
                    [--fps 30] [--width 1080] [--height 1920] [--seed 7] [--ffmpeg ffmpeg]

speech.json: {"words": [{"text", "start", "end"}, ...]}, the narration's word timings in seconds.
"""

import argparse
import json
import os
import random
import subprocess
import sys
import tempfile

import cv2
import numpy as np

SHOTS_VERSION = 1


def detect_shots(path: str) -> list[dict]:
    cap = cv2.VideoCapture(path)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) / fps
    step = 2
    prev, index, diffs, columns, lumas, times = None, 0, [], [], [], []
    while cap.grab():
        if index % step == 0:
            _, frame = cap.retrieve()
            small = cv2.cvtColor(cv2.resize(frame, (192, 108)), cv2.COLOR_BGR2GRAY).astype(np.float32)
            if prev is not None:
                d = np.abs(small - prev)
                diffs.append(d.mean())
                columns.append(d.mean(axis=0))
                lumas.append(small.mean())
                times.append(index / fps)
            prev = small
        index += 1
    cap.release()
    if not diffs:
        return []
    diffs = np.array(diffs)
    cuts = [0.0]
    for i, d in enumerate(diffs):
        local = np.median(diffs[max(0, i - 15): i + 15])
        if d > 28 and d > 3 * local and times[i] - cuts[-1] > 0.4:
            cuts.append(times[i])
    cuts.append(total)
    shots = []
    for start, end in zip(cuts, cuts[1:]):
        if start < 2.0 or end > total * 0.85 or end - start < 1.0:
            continue
        inside = [i for i, t in enumerate(times) if start + 0.1 < t < end - 0.1]
        if len(inside) < 4:
            continue
        luma = float(np.mean([lumas[i] for i in inside]))
        if luma < 25:
            continue
        motion = float(np.median(diffs[inside]))
        energy = np.sum([columns[i] for i in inside], axis=0)
        center = float(np.sum(energy * np.arange(len(energy))) / (energy.sum() + 1e-6) / len(energy))
        shots.append({"file": os.path.basename(path), "start": round(start, 3), "end": round(end, 3),
                      "motion": round(motion, 2), "luma": round(luma, 1), "center": round(center, 3)})
    return shots


def all_shots(folder: str) -> list[dict]:
    """Every trailer's shots, from shots.json when it was made from the same files."""
    names = sorted(n for n in os.listdir(folder) if n.endswith(".mp4"))
    sizes = {n: os.path.getsize(os.path.join(folder, n)) for n in names}
    cache = os.path.join(folder, "shots.json")
    if os.path.exists(cache):
        with open(cache, encoding="utf-8") as f:
            saved = json.load(f)
        if saved.get("version") == SHOTS_VERSION and saved.get("files") == sizes:
            return saved["shots"]
    shots = []
    for name in names:
        found = detect_shots(os.path.join(folder, name))
        print(f"{name}: {len(found)} usable shots", file=sys.stderr)
        shots += found
    with open(cache, "w", encoding="utf-8") as f:
        json.dump({"version": SHOTS_VERSION, "files": sizes, "shots": shots}, f, indent=1)
    return shots


def plan_slots(starts: list[float], duration: float, rng: random.Random) -> list[tuple[float, float]]:
    """Cut points on word starts, every 1.3 to 2.6 s (1.0 to 1.6 s during the first five seconds)."""
    cuts, last = [0.0], 0.0
    for t in starts:
        low, high = (1.0, 1.6) if last < 5 else (1.3, 2.6)
        if t - last >= low and (t - last >= high * 0.8 or rng.random() < 0.35):
            cuts.append(t)
            last = t
    # The last word can be well before the end (the tail); no slot runs past 2.6 s.
    while duration - cuts[-1] > 2.6:
        cuts.append(cuts[-1] + 2.0)
    cuts.append(duration)
    if len(cuts) > 2 and cuts[-1] - cuts[-2] < 0.8:
        cuts.pop(-2)
    return list(zip(cuts, cuts[1:]))


def assign(slots: list[tuple[float, float]], shots: list[dict]) -> list[dict]:
    """The most moving unused shot that is long enough, from another trailer than the slot before when possible. When
    every shot is used, the least used ones come back (a long video from short trailers)."""
    pool = sorted(shots, key=lambda s: -s["motion"])
    uses, plan, last_file = [0] * len(pool), [], None
    for start, end in slots:
        need = end - start
        fits = [i for i, s in enumerate(pool) if s["end"] - s["start"] >= need]
        if not fits:
            raise SystemExit(f"No shot in the trailers is {need:.2f} s long.")
        least = min(uses[i] for i in fits)
        fresh = [i for i in fits if uses[i] == least]
        pick = next((i for i in fresh if pool[i]["file"] != last_file), fresh[0])
        uses[pick] += 1
        shot = pool[pick]
        last_file = shot["file"]
        middle = (shot["start"] + shot["end"]) / 2
        source = max(shot["start"], min(middle - need / 2, shot["end"] - need))
        plan.append({**shot, "slot": [round(start, 3), round(end, 3)], "from": round(source, 3)})
    return plan


def render_slot(item: dict, trailers: str, frames: int, out: str, a) -> None:
    w, h, fps = a.width, a.height, a.fps
    scaled_w = int(round(h * 16 / 9 / 2)) * 2
    x = int(min(max(item["center"] * scaled_w - w / 2, 0), scaled_w - w))
    vf = (f"scale=-2:{h},crop={w}:{h}:{x}:0,fps={fps},"
          f"zoompan=z='1.0+0.06*on/{frames}':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s={w}x{h}:fps={fps},"
          f"setsar=1,format=yuv420p")
    run([a.ffmpeg, "-y", "-v", "error", "-ss", str(item["from"]), "-t", f"{frames / fps + 0.5:.3f}",
         "-i", os.path.join(trailers, item["file"]), "-an", "-vf", vf, "-frames:v", str(frames),
         "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", out])


def run(args: list[str]) -> None:
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(result.stderr[-2000:])


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--trailers", required=True)
    p.add_argument("--timings", required=True)
    p.add_argument("--frames", type=int, required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--cuts", required=True)
    p.add_argument("--fps", type=int, default=30)
    p.add_argument("--width", type=int, default=1080)
    p.add_argument("--height", type=int, default=1920)
    p.add_argument("--seed", type=int, default=7)
    p.add_argument("--ffmpeg", default="ffmpeg")
    a = p.parse_args()

    shots = all_shots(a.trailers)
    if not shots:
        print("No usable shots in the trailers.", file=sys.stderr)
        return 1
    with open(a.timings, encoding="utf-8") as f:
        words = json.load(f)["words"]
    duration = a.frames / a.fps
    plan = assign(plan_slots([w["start"] for w in words], duration, random.Random(a.seed)), shots)

    # Whole frames per slot, summing to exactly --frames.
    edges = [round(item["slot"][0] * a.fps) for item in plan] + [a.frames]
    with tempfile.TemporaryDirectory(dir=os.path.dirname(os.path.abspath(a.out))) as work:
        listing = []
        for i, item in enumerate(plan):
            item["frames"] = edges[i + 1] - edges[i]
            out = os.path.join(work, f"slot-{i:03d}.mp4")
            render_slot(item, a.trailers, item["frames"], out, a)
            listing.append(f"file '{out.replace(os.sep, '/')}'")
        with open(os.path.join(work, "slots.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(listing) + "\n")
        run([a.ffmpeg, "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", os.path.join(work, "slots.txt"),
             "-c", "copy", a.out])
    with open(a.cuts, "w", encoding="utf-8") as f:
        json.dump(plan, f, indent=1)
    print(f"{len(plan)} cuts over {duration:.2f} s", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
