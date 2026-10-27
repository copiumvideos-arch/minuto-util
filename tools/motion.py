"""Camera moves on still images for Minuto Util, CPU only.

Effects:
  kenburns  A slow zoom with a pan across the picture. Every frame is an exact sub-pixel affine warp of the source,
            rendered at --supersample times the output size and scaled down, so slow moves do not shimmer or jitter
            (FFmpeg's zoompan rounds the crop to whole pixels).
  parallax  The 2.5D "hologram" move: Depth Anything V2 Small estimates depth, then the camera slides sideways and
            near pixels shift more than far ones. The plane at mid depth stays put, so the background drifts one way
            and the foreground the other, like a slow orbit. Holes are avoided by sampling backwards (every output pixel
            takes a source pixel) through a depth map that is widened around near objects and blurred: the stretch
            then falls on the background next to an edge, and near objects keep their shape.

Both crop the picture to fill the frame (9:16), never with bars, and write an H.264 MP4 of exactly --frames frames.

Usage:
  uv run motion.py render --image in.jpg --out shot.mp4 --effect parallax --frames 150 [--fps 30] [--width 1080]
                   [--height 1920] [--direction 1] [--zoom in] [--supersample 2] [--travel 0.045] [--kb-zoom 0.1]
                   [--depth depth.png] [--ffmpeg ffmpeg]
  uv run motion.py depth --image in.jpg --out depth.png

--depth: a 16-bit PNG depth map (white = near). It is read when the file exists, otherwise computed and saved there.
         Both effects aim the 9:16 crop at the faces in the picture, or else at the near pixels (focus_x), so a person
         at the side of a wide photo is not cut off.
"""

import argparse
import math
import os
import subprocess
import sys

import cv2
import numpy as np
from PIL import Image, ImageOps

DEPTH_MODEL = "depth-anything/Depth-Anything-V2-Small-hf"
# Larger pictures are scaled down first: no frame needs more, and memory stays low on a 7 GB runner.
MAX_SOURCE_SIDE = 7000
# The depth plane that stays still while the camera moves (0 = farthest, 1 = nearest).
FOCUS_DEPTH = 0.35


def load_rgb(path: str) -> np.ndarray:
    Image.MAX_IMAGE_PIXELS = 200_000_000
    with Image.open(path) as image:
        image = ImageOps.exif_transpose(image).convert("RGB")
        if max(image.size) > MAX_SOURCE_SIDE:
            image.thumbnail((MAX_SOURCE_SIDE, MAX_SOURCE_SIDE), Image.LANCZOS)
        return np.asarray(image)


def estimate_depth(rgb: np.ndarray) -> np.ndarray:
    """Relative depth in [0, 1], 1 = nearest, at the picture's size."""
    import torch
    from transformers import AutoImageProcessor, AutoModelForDepthEstimation

    torch.set_num_threads(max(1, os.cpu_count() or 1))
    processor = AutoImageProcessor.from_pretrained(DEPTH_MODEL)
    model = AutoModelForDepthEstimation.from_pretrained(DEPTH_MODEL)
    model.eval()
    inputs = processor(images=Image.fromarray(rgb), return_tensors="pt")
    with torch.no_grad():
        predicted = model(**inputs).predicted_depth
    depth = torch.nn.functional.interpolate(predicted.unsqueeze(1), size=rgb.shape[:2], mode="bicubic",
                                            align_corners=False).squeeze().numpy()
    return normalize_depth(depth)


def normalize_depth(depth: np.ndarray) -> np.ndarray:
    """Spreads the depth over [0, 1] by its 2nd and 98th percentiles, so a few extreme pixels do not flatten the rest."""
    low, high = np.percentile(depth, [2, 98])
    if high - low < 1e-6:
        return np.zeros_like(depth, dtype=np.float32)
    return np.clip((depth - low) / (high - low), 0, 1).astype(np.float32)


def load_or_make_depth(path: str | None, rgb: np.ndarray) -> np.ndarray:
    if path and os.path.exists(path):
        depth = cv2.imread(path, cv2.IMREAD_UNCHANGED)
        if depth is None:
            raise RuntimeError(f"Cannot read the depth map {path}.")
        if depth.ndim == 3:
            depth = depth[..., 0]
        scale = 65535.0 if depth.dtype == np.uint16 else 255.0
        depth = depth.astype(np.float32) / scale
        if depth.shape != rgb.shape[:2]:
            depth = cv2.resize(depth, (rgb.shape[1], rgb.shape[0]), interpolation=cv2.INTER_CUBIC)
        return np.clip(depth, 0, 1)
    depth = estimate_depth(rgb)
    if path:
        cv2.imwrite(path, (depth * 65535).astype(np.uint16))
    return depth


def prepare_depth(depth: np.ndarray, frame_width: float) -> np.ndarray:
    """
    Widens near objects a little and blurs, so displacement changes smoothly: near objects move as one piece and the
    stretch lands on the background beside them instead of tearing their outline.
    """
    grow = max(3, int(frame_width * 0.012)) | 1
    widened = cv2.dilate(depth, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (grow, grow)))
    sigma = max(1.0, frame_width * 0.008)
    return cv2.GaussianBlur(widened, (0, 0), sigma)


def cover_rect(width: int, height: int, aspect: float) -> tuple[float, float]:
    """Width and height of the largest rectangle of the frame's aspect that fits the picture."""
    if width / height > aspect:
        return height * aspect, float(height)
    return float(width), width / aspect


def ease(t: float) -> float:
    """Mostly linear with soft ends, so a move never seems to stop and start at the cross-fades."""
    return 0.6 * t + 0.4 * (0.5 - 0.5 * math.cos(math.pi * t))


# Fastest pan, in visible widths per second: slow enough to read the picture, fast enough to feel alive.
PAN_SPEED = 0.045


def face_focus(rgb: np.ndarray) -> float | None:
    """
    Horizontal center of the faces in the picture as a share of the width, weighted by face size, or None without
    faces. OpenCV's bundled frontal-face cascade: no download, milliseconds on the CPU, fine on old photos.
    """
    if not hasattr(cv2, "CascadeClassifier"):
        # Some opencv-python-headless builds ship without the object detection module.
        return None
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    scale = 1000 / max(gray.shape)
    if scale < 1:
        gray = cv2.resize(gray, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    cascade = cv2.CascadeClassifier(os.path.join(cv2.data.haarcascades, "haarcascade_frontalface_default.xml"))
    min_side = max(24, int(min(gray.shape) * 0.04))
    faces = cascade.detectMultiScale(cv2.equalizeHist(gray), scaleFactor=1.1, minNeighbors=6, minSize=(min_side, min_side))
    if len(faces) == 0:
        return None
    areas = np.array([w * h for _, _, w, h in faces], np.float64)
    centers = np.array([x + w / 2 for x, _, w, _ in faces], np.float64) / gray.shape[1]
    return float((areas * centers).sum() / areas.sum())


def focus_x(depth: np.ndarray | None, rgb: np.ndarray | None = None) -> float:
    """
    Where the subject probably is, horizontally, as a share of the width. Faces first (pulled 25% toward the middle);
    without faces, the centroid of near pixels (people and vehicles are usually nearest), pulled 60% toward the middle,
    since the nearest thing can also be the back of a helmet in the foreground.
    """
    if rgb is not None and (faces := face_focus(rgb)) is not None:
        return 0.75 * faces + 0.25 * 0.5
    if depth is None:
        return 0.5
    weights = np.clip(depth - np.percentile(depth, 50), 0, None) ** 2
    columns = weights.sum(axis=0)
    if columns.sum() <= 1e-6:
        return 0.5
    centroid = float((columns * np.arange(len(columns))).sum() / columns.sum()) / len(columns)
    return 0.4 * centroid + 0.6 * 0.5


def camera_path(width: int, height: int, aspect: float, frames: int, fps: int, zoom_from: float, zoom_to: float,
                direction: int, max_pan: float, focus: float = 0.5):
    """
    Per frame: (center x, center y, visible width) in picture pixels. The view zooms from zoom_from to zoom_to and pans
    horizontally by up to max_pan of the visible width (and no faster than PAN_SPEED), never past the picture's edges.
    """
    base_w, base_h = cover_rect(width, height, aspect)
    tightest = base_w / max(zoom_from, zoom_to)
    # Room to pan at the widest view of the move, so the clamp below never stops the pan halfway.
    room = max(0.0, (width - base_w / min(zoom_from, zoom_to)) / 2)
    pan = min(max_pan, PAN_SPEED * frames / fps)
    half_pan = min(room, pan * tightest / 2)
    path = []
    for i in range(frames):
        t = ease(i / max(frames - 1, 1))
        zoom = zoom_from + (zoom_to - zoom_from) * t
        view_w, view_h = base_w / zoom, base_h / zoom
        cx = width * focus + direction * half_pan * (2 * t - 1)
        cx = min(max(cx, view_w / 2), width - view_w / 2)
        cy = min(max(height / 2, view_h / 2), height - view_h / 2)
        path.append((cx, cy, view_w))
    return path


class Encoder:
    """Pipes RGB frames into FFmpeg: H.264, near lossless, since the final render encodes again."""

    def __init__(self, ffmpeg: str, out: str, width: int, height: int, fps: int):
        self.process = subprocess.Popen(
            [ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
             "-s", f"{width}x{height}", "-r", str(fps), "-i", "-", "-c:v", "libx264", "-preset", "medium",
             "-crf", "12", "-pix_fmt", "yuv420p", "-r", str(fps), out],
            stdin=subprocess.PIPE)

    def write(self, frame: np.ndarray) -> None:
        self.process.stdin.write(np.ascontiguousarray(frame).tobytes())

    def close(self) -> None:
        self.process.stdin.close()
        if self.process.wait() != 0:
            raise RuntimeError("FFmpeg failed to encode the shot.")


def scaled_source(rgb: np.ndarray, view_width: float, render_width: int) -> tuple[np.ndarray, float]:
    """
    The picture resized so its tightest view is about render_width pixels wide: sampling then runs near 1:1 and
    neither aliases (a big photo shrunk by warping alone shimmers) nor blurs more than it must.
    """
    factor = render_width / view_width
    if abs(factor - 1) < 0.02:
        return rgb, 1.0
    size = (max(1, round(rgb.shape[1] * factor)), max(1, round(rgb.shape[0] * factor)))
    interpolation = cv2.INTER_AREA if factor < 1 else cv2.INTER_LANCZOS4
    return cv2.resize(rgb, size, interpolation=interpolation), factor


def render(args) -> None:
    rgb = load_rgb(args.image)
    aspect = args.width / args.height
    ss = max(1.0, args.supersample)
    render_w, render_h = int(round(args.width * ss)), int(round(args.height * ss))

    if args.effect == "kenburns":
        zoom_from, zoom_to = (1.0, 1.0 + args.kb_zoom) if args.zoom == "in" else (1.0 + args.kb_zoom, 1.0)
        max_pan = 0.25
    else:
        # The parallax needs room at the edges for the displaced pixels, hence the view starts slightly zoomed in.
        zoom_from, zoom_to = 1.06, 1.10
        max_pan = 0.06
    # Both effects use the depth map to aim the crop at the subject; only the parallax moves pixels by it.
    depth_full = load_or_make_depth(args.depth, rgb) if args.depth or args.effect == "parallax" else None
    focus = focus_x(depth_full, rgb)
    path = camera_path(rgb.shape[1], rgb.shape[0], aspect, args.frames, args.fps, zoom_from, zoom_to, args.direction,
                       max_pan, focus)
    tightest = min(view_w for _, _, view_w in path)
    source, factor = scaled_source(rgb, tightest, render_w)

    depth = None
    if args.effect == "parallax":
        depth = cv2.resize(depth_full, (source.shape[1], source.shape[0]), interpolation=cv2.INTER_LINEAR)
        depth = prepare_depth(depth, tightest * factor)

    encoder = Encoder(args.ffmpeg, args.out, args.width, args.height, args.fps)
    u = (np.arange(render_w, dtype=np.float32) + 0.5) / render_w - 0.5
    v = (np.arange(render_h, dtype=np.float32) + 0.5) / render_h - 0.5
    grid_u, grid_v = np.meshgrid(u, v)
    try:
        for i, (cx, cy, view_w) in enumerate(path):
            cx, cy, view_w = cx * factor, cy * factor, view_w * factor
            view_h = view_w / aspect
            map_x = cx + grid_u * view_w
            map_y = cy + grid_v * view_h
            if depth is not None:
                t = ease(i / max(args.frames - 1, 1))
                # Camera offset in picture pixels: -travel/2 to +travel/2 of the view for the nearest pixels.
                shift = args.direction * args.travel * view_w * (t - 0.5)
                lift = 0.25 * shift
                base_x, base_y = map_x, map_y
                # Backward warp by fixed-point iteration: each output pixel looks up the depth where it will sample
                # from, so a near object's pixels move together.
                for _ in range(3):
                    d = cv2.remap(depth, map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE) - FOCUS_DEPTH
                    map_x = base_x + shift * d
                    map_y = base_y + lift * d
            frame = cv2.remap(source, map_x.astype(np.float32), map_y.astype(np.float32), cv2.INTER_CUBIC,
                              borderMode=cv2.BORDER_REFLECT_101)
            if ss > 1:
                frame = cv2.resize(frame, (args.width, args.height), interpolation=cv2.INTER_AREA)
            encoder.write(frame)
    finally:
        encoder.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    r = commands.add_parser("render")
    r.add_argument("--image", required=True)
    r.add_argument("--out", required=True)
    r.add_argument("--effect", choices=["kenburns", "parallax"], required=True)
    r.add_argument("--frames", type=int, required=True)
    r.add_argument("--fps", type=int, default=30)
    r.add_argument("--width", type=int, default=1080)
    r.add_argument("--height", type=int, default=1920)
    r.add_argument("--direction", type=int, choices=[-1, 1], default=1)
    r.add_argument("--zoom", choices=["in", "out"], default="in")
    r.add_argument("--supersample", type=float, default=2.0)
    r.add_argument("--travel", type=float, default=0.045)
    r.add_argument("--kb-zoom", type=float, default=0.10)
    r.add_argument("--depth")
    r.add_argument("--ffmpeg", default="ffmpeg")
    d = commands.add_parser("depth")
    d.add_argument("--image", required=True)
    d.add_argument("--out", required=True)
    args = parser.parse_args()

    if args.command == "depth":
        rgb = load_rgb(args.image)
        cv2.imwrite(args.out, (estimate_depth(rgb) * 65535).astype(np.uint16))
        return 0
    if args.frames < 1:
        print("--frames must be at least 1.", file=sys.stderr)
        return 1
    render(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
