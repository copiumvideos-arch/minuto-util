"""Makes the watermark from the channel emblem: the round badge cut out of its black square, small, with transparency.

The emblem (assets/brand/emblem.png) is a red ring on black with the stopwatch, hourglass and name inside. The outer
edge of the ring is found by fitting a circle to the outermost red pixel along 180 rays from the middle, so a redrawn
emblem works without new numbers. Everything outside that circle becomes transparent with an anti-aliased edge, then
the badge is scaled down with premultiplied alpha (no dark fringe). Run it again only when the emblem changes.

Usage:
  uv run watermark.py --emblem ../assets/brand/emblem.png --out ../assets/brand/watermark.png [--size 96]
"""

import argparse
import math
import sys

import numpy as np
from PIL import Image

RAYS = 180


def is_red(rgb: np.ndarray) -> np.ndarray:
    r, g, b = (rgb[..., k].astype(int) for k in range(3))
    return (r > 140) & (g < 90) & (b < 90)


def outer_circle(rgb: np.ndarray) -> tuple[float, float, float]:
    """Center and radius of the outer edge of the red ring, in pixels of the source image."""
    red = is_red(rgb)
    ys, xs = np.nonzero(red)
    if len(xs) < 100:
        raise RuntimeError("No red ring found in the emblem.")
    h, w = red.shape
    cx, cy = (xs.min() + xs.max()) / 2, (ys.min() + ys.max()) / 2
    reach = int(math.hypot(w, h) / 2)
    points = []
    for k in range(RAYS):
        angle = 2 * math.pi * k / RAYS
        for r in range(reach, 0, -1):
            x, y = int(round(cx + r * math.cos(angle))), int(round(cy + r * math.sin(angle)))
            if 0 <= x < w and 0 <= y < h and red[y, x]:
                points.append((x, y))
                break
    p = np.array(points, float)
    # Least squares circle: x^2 + y^2 = 2ax + 2by + c.
    a, b, c = np.linalg.lstsq(np.c_[2 * p, np.ones(len(p))], (p ** 2).sum(axis=1), rcond=None)[0]
    radius = math.sqrt(c + a * a + b * b)
    # The ring is hand-textured: take the 95th percentile of the edge, so its bumps stay inside the cut.
    distances = np.hypot(p[:, 0] - a, p[:, 1] - b)
    return a, b, max(radius, float(np.percentile(distances, 95)))


def make_watermark(emblem: Image.Image, size: int) -> Image.Image:
    rgb = np.asarray(emblem.convert("RGB"))
    cx, cy, radius = outer_circle(rgb)
    radius += 1.5
    half = int(math.ceil(radius)) + 2
    box = (int(cx) - half, int(cy) - half, int(cx) + half, int(cy) + half)
    crop = np.asarray(emblem.convert("RGB").crop(box)).astype(np.float32)
    yy, xx = np.mgrid[0:crop.shape[0], 0:crop.shape[1]]
    distance = np.hypot(xx + box[0] + 0.5 - cx, yy + box[1] + 0.5 - cy)
    # A two-pixel soft edge at full size, well under one pixel once scaled down.
    alpha = np.clip(radius - distance + 1, 0, 2) / 2
    rgba = np.dstack([crop, alpha * 255]).astype(np.uint8)
    badge = Image.fromarray(rgba, "RGBA").convert("RGBa")
    return badge.resize((size, size), Image.LANCZOS).convert("RGBA")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--emblem", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--size", type=int, default=96)
    args = parser.parse_args()
    if not 16 <= args.size <= 512:
        print("--size must be between 16 and 512.", file=sys.stderr)
        return 1
    make_watermark(Image.open(args.emblem), args.size).save(args.out, optimize=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
