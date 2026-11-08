"""The picture track of a trailer video (media.footage "trailers"), CPU only.

Steps:
  shots   Finds the shots in every trailer (a cut is a frame difference far above its neighbours, so cuts between
          dark cinematic shots count too), scores each one for motion and remembers where the action is horizontally,
          so the 9:16 crop follows it. The first seconds and the last 15% of a trailer (logos, titles, rating cards),
          short and dark shots are left out. A tiny copy of each shot's middle frame tells shots that show the same
          picture (a trailer cutting back to a scene) apart from new ones. Cached in shots.json next to the trailers,
          with the size of each file it was made from.
  plan    Cuts the narration into slots of 1.3 to 2.6 seconds that start on a word (1.0 to 1.6 s in the first five
          seconds, where the viewer decides to stay), and gives each slot a scene not seen yet, the most moving first,
          never the same trailer twice in a row. A long moving shot is cut into pieces of about three seconds. When
          the only unseen scenes are shorter than the slot, the slot is shortened to fit one instead of showing a scene
          again. With too little footage for the video (one short trailer), a shot is held up to four seconds, and a
          shot comes back only when every scene has been seen, the one seen longest ago first, showing another part
          of it when it is long enough.
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

SHOTS_VERSION = 3

# A frame difference CUT_RATIO times the median of its neighbours, and above CUT_MIN, is a cut. CUT_MIN is low so cuts
# between dark shots are found; a flash taken for a cut only splits a shot in two.
CUT_RATIO = 3
CUT_MIN = 12
# Shorter shots are never used.
MIN_SHOT = 1.0
# A shot of two pieces or more is cut into pieces of about this many seconds, each a different moment of it, when it
# moves at least PIECE_MIN_MOTION (the median frame difference); the pieces of a still shot would look the same.
PIECE = 3.0
PIECE_MIN_MOTION = 8
# A slot shortened to fit an unseen shot leaves at least this much for the next slot.
MIN_REST = 0.8
# Slots in the first FAST_SECONDS stay short (the hook). After that, when the unseen footage is shorter than the time
# left, one shot may be held over several slots, up to MAX_STRETCH seconds, so fewer shots are seen twice.
FAST_SECONDS = 5.0
MAX_STRETCH = 4.0
# Each shot keeps its middle frame this small (width, height) to find shots that show the same picture: correlation
# at least LOOK_ALIKE, between frames with some contrast (pixel spread at least LOOK_MIN_STD).
LOOK_SIZE = (16, 9)
LOOK_ALIKE = 0.9
LOOK_MIN_STD = 12


def detect_shots(path: str) -> list[dict]:
    cap = cv2.VideoCapture(path)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) / fps
    step = 2
    prev, index, diffs, columns, lumas, times, tinies = None, 0, [], [], [], [], []
    while cap.grab():
        if index % step == 0:
            _, frame = cap.retrieve()
            color = cv2.resize(frame, (192, 108))
            small = cv2.cvtColor(color, cv2.COLOR_BGR2GRAY).astype(np.float32)
            if prev is not None:
                d = np.abs(small - prev)
                diffs.append(d.mean())
                columns.append(d.mean(axis=0))
                lumas.append(small.mean())
                times.append(index / fps)
                tinies.append(cv2.resize(color, LOOK_SIZE, interpolation=cv2.INTER_AREA))
            prev = small
        index += 1
    cap.release()
    if not diffs:
        return []
    diffs = np.array(diffs)
    cuts = [0.0]
    for i, d in enumerate(diffs):
        local = np.median(diffs[max(0, i - 15): i + 15])
        if d > CUT_MIN and d > CUT_RATIO * local and times[i] - cuts[-1] > 0.4:
            cuts.append(times[i])
    cuts.append(total)
    shots = []
    for start, end in zip(cuts, cuts[1:]):
        if start < 2.0 or end > total * 0.85 or end - start < MIN_SHOT:
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
        look = tinies[inside[len(inside) // 2]].flatten().astype(int).tolist()
        shots.append({"file": os.path.basename(path), "start": round(start, 3), "end": round(end, 3),
                      "motion": round(motion, 2), "luma": round(luma, 1), "center": round(center, 3), "look": look})
    return shots


def alike(a: list[int], b: list[int]) -> bool:
    """Whether two shots' middle frames (LOOK_SIZE, colour) show the same picture: the same place from the same angle,
    like a trailer cutting away from a scene and back to it. A nearly flat frame (a dark or one-colour shot) is alike
    to nothing, since its correlation is noise."""
    x, y = np.asarray(a, np.float32), np.asarray(b, np.float32)
    if x.shape != y.shape or x.std() < LOOK_MIN_STD or y.std() < LOOK_MIN_STD:
        return False
    x, y = x - x.mean(), y - y.mean()
    return float((x @ y) / (np.linalg.norm(x) * np.linalg.norm(y))) >= LOOK_ALIKE


def scenes(shots: list[dict]) -> list[int]:
    """A scene number per shot: shots that look alike (directly or through another shot) share one."""
    parent = list(range(len(shots)))

    def root(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(len(shots)):
        for j in range(i + 1, len(shots)):
            if "look" in shots[i] and "look" in shots[j] and alike(shots[i]["look"], shots[j]["look"]):
                parent[root(j)] = root(i)
    return [root(i) for i in range(len(shots))]


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


def pieces(shots: list[dict]) -> list[dict]:
    """Every shot, a long moving one cut into pieces of about PIECE seconds (a still shot cut in two would show the
    same picture twice). Each piece keeps its scene: its shot, joined with every shot that looks the same, so two
    moments of one scene are told apart from two different scenes."""
    out = []
    for number, shot in zip(scenes(shots), shots):
        length = shot["end"] - shot["start"]
        count = max(1, int(length // PIECE)) if shot["motion"] >= PIECE_MIN_MOTION else 1
        step = length / count
        for k in range(count):
            out.append({**shot, "start": round(shot["start"] + k * step, 3),
                        "end": round(shot["start"] + (k + 1) * step, 3), "scene": number})
    return out


def window(shot: dict, need: float, used: list[tuple[float, float]]) -> float:
    """Where in the shot a slot of `need` seconds starts: the middle the first time; when the shot comes back, the part
    that overlaps least with what was shown before (the other end of a long shot shows other frames)."""
    first, last = shot["start"], max(shot["start"], shot["end"] - need)
    middle = max(first, min((shot["start"] + shot["end"]) / 2 - need / 2, last))
    if not used:
        return middle

    def overlap(s: float) -> float:
        return sum(max(0.0, min(s + need, b) - max(s, a)) for a, b in used)

    return min((first, last, middle), key=overlap)


def assign(slots: list[tuple[float, float]], shots: list[dict]) -> list[dict]:
    """Gives every slot a piece of a shot, preferring a shot not seen yet, then an unseen moment of a seen shot, then
    the piece seen longest ago. Among equals: not the shot of the slot before, another trailer than the slot before,
    the most moving. When no unseen shot is long enough for a slot but a shorter one is, the slot is cut to that
    shot's length and the rest becomes a slot of its own (or, under MIN_REST, joins the next slot), so nothing
    repeats while unseen footage is left."""
    pool = sorted(pieces(shots), key=lambda s: -s["motion"])
    uses, seen_at = [0] * len(pool), [-1] * len(pool)
    windows: dict[int, list[tuple[float, float]]] = {}
    scenes_seen: set[int] = set()
    plan, queue = [], list(slots)
    last_scene, last_file = None, None

    def length(i: int) -> float:
        return pool[i]["end"] - pool[i]["start"]

    def rank(i: int) -> tuple:
        p = pool[i]
        return (p["scene"] in scenes_seen, uses[i], seen_at[i], p["scene"] == last_scene, p["file"] == last_file)

    while queue:
        start, end = queue.pop(0)
        need = end - start
        # Within a frame: seconds added from rounded times are never exact.
        fits = [i for i in range(len(pool)) if length(i) >= need - 0.03]
        pick = min(fits, key=rank) if fits else None
        if pick is None or pool[pick]["scene"] in scenes_seen:
            # Shorten the slot to an unseen shot rather than repeat one; with nothing long enough, to the longest shot.
            # A rest too short for a slot of its own goes to the next slot, so the last slot needs a real rest.
            def leaves_room(i: int) -> bool:
                rest = need - length(i)
                return rest >= MIN_REST or (rest > 0.03 and bool(queue))

            shorter = [i for i in range(len(pool)) if pool[i]["scene"] not in scenes_seen and leaves_room(i)]
            if not shorter and pick is None:
                shorter = [i for i in range(len(pool)) if leaves_room(i)]
            if shorter:
                pick = min(shorter, key=lambda i: (rank(i), -length(i)))
                cut = round(start + length(pick), 3)
                if end - cut >= MIN_REST:
                    queue.insert(0, (cut, end))
                else:
                    queue[0] = (cut, queue[0][1])
                end, need = cut, cut - start
        if pick is None:
            raise SystemExit(f"No shot in the trailers is {need:.2f} s long.")
        shot = pool[pick]
        if start >= FAST_SECONDS:
            # Too little unseen footage for the time left: hold this shot over the next slots (up to MAX_STRETCH), so
            # fewer shots are shown again and those that are come back fewer times.
            unseen = sum(length(i) for i in range(len(pool)) if pool[i]["scene"] not in scenes_seen | {shot["scene"]})
            while queue and unseen < queue[-1][1] - end and queue[0][1] - start <= min(length(pick), MAX_STRETCH):
                end = queue.pop(0)[1]
            need = end - start
        uses[pick] += 1
        seen_at[pick] = len(plan)
        scenes_seen.add(shot["scene"])
        last_scene, last_file = shot["scene"], shot["file"]
        source = window(shot, need, windows.setdefault(pick, []))
        windows[pick].append((source, source + need))
        item = {k: v for k, v in shot.items() if k not in ("scene", "look")}
        plan.append({**item, "slot": [round(start, 3), round(end, 3)], "from": round(source, 3)})
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
