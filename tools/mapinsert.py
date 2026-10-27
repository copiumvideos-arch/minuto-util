"""The map shot: a zoom from Europe into the event's place, with a glowing pin and its name. CPU only.

Country shapes come from Natural Earth (public domain, https://www.naturalearthdata.com/), 1:10m admin 0 countries
as GeoJSON. The file is downloaded once into --data-dir and reused. It is untrusted data: size-capped, parsed as JSON
only, and every geometry checked to be a polygon of numbers before anything is drawn.

Usage:
  uv run mapinsert.py --out map.mp4 --name Normandia --lat 49.4 --lon -0.9 --frames 78 --font RobotoSerif-Black.ttf
                      [--data-dir assets/cache/naturalearth | --geojson countries.geojson] [--fps 30]
                      [--width 1080] [--height 1920] [--ffmpeg ffmpeg] [--pin-color "#DC010A"]
"""

import argparse
import json
import math
import os
import subprocess
import sys
import urllib.request

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

SOURCE_URL = ("https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/"
              "ne_10m_admin_0_countries.geojson")
FILE_NAME = "ne_10m_admin_0_countries.geojson"
MAX_BYTES = 40_000_000
USER_AGENT = "MinutoUtilBot/0.1 (faceless history Shorts prototype; public domain and CC media only)"

# Old atlas colors: pale sea, cream land, sepia borders; the pin in the channel's red (--pin-color overrides it).
SEA = (183, 192, 182)
LAND = (229, 214, 174)
BORDER = (122, 92, 58)
GRID = (160, 168, 156)
PIN = (220, 1, 10)
INK = (20, 16, 12)

# Wide view of Europe at the start, close view of the place at the end, as degrees of longitude across the frame.
START_SPAN = 34.0
END_SPAN = 5.5
EUROPE = (10.0, 50.0)
SUPERSAMPLE = 1.5


def load_countries(data_dir: str | None, geojson: str | None) -> list[np.ndarray]:
    """Every outer and inner ring as an (n, 2) array of lon, lat."""
    path = geojson or os.path.join(data_dir, FILE_NAME)
    if not os.path.exists(path):
        if geojson:
            raise RuntimeError(f"{geojson} does not exist.")
        download(path)
    if os.path.getsize(path) > MAX_BYTES:
        raise RuntimeError(f"{path} is larger than {MAX_BYTES} bytes.")
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict) or data.get("type") != "FeatureCollection" or not isinstance(data.get("features"), list):
        raise RuntimeError(f"{path} is not a GeoJSON FeatureCollection.")
    rings = []
    for feature in data["features"]:
        geometry = feature.get("geometry") if isinstance(feature, dict) else None
        if not isinstance(geometry, dict):
            continue
        kind, coordinates = geometry.get("type"), geometry.get("coordinates")
        polygons = [coordinates] if kind == "Polygon" else coordinates if kind == "MultiPolygon" else []
        for polygon in polygons or []:
            for ring in polygon:
                array = np.asarray(ring, dtype=np.float64)
                if array.ndim == 2 and array.shape[1] >= 2 and len(array) >= 3 and np.isfinite(array).all():
                    rings.append(array[:, :2])
    if not rings:
        raise RuntimeError(f"{path} holds no polygons.")
    return rings


def download(path: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    request = urllib.request.Request(SOURCE_URL, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise RuntimeError("The Natural Earth file is larger than expected; not saved.")
    json.loads(data)  # must parse before it is kept
    temp = path + ".tmp"
    with open(temp, "wb") as f:
        f.write(data)
    os.replace(temp, path)


def ease(t: float) -> float:
    return 0.5 - 0.5 * math.cos(math.pi * min(max(t, 0.0), 1.0))


def view(t: float, lat: float, lon: float) -> tuple[float, float, float]:
    """Center lon, center lat and span at time t (0-1): the span shrinks geometrically, so the zoom feels even."""
    e = ease(t)
    span = START_SPAN * (END_SPAN / START_SPAN) ** e
    return EUROPE[0] + (lon - EUROPE[0]) * e, EUROPE[1] + (lat - EUROPE[1]) * e, span


def parse_color(text: str) -> tuple[int, int, int] | None:
    """"#DC010A" as (220, 1, 10); None when it is not a #RRGGBB color."""
    value = text.strip().removeprefix("#")
    if len(value) != 6 or any(c not in "0123456789abcdefABCDEF" for c in value):
        return None
    return int(value[0:2], 16), int(value[2:4], 16), int(value[4:6], 16)


def draw_frame(rings, bboxes, width, height, center_lon, center_lat, span, lat, lon, name, font, pin_t,
               pin=PIN) -> Image.Image:
    w, h = int(width * SUPERSAMPLE), int(height * SUPERSAMPLE)
    k = math.cos(math.radians(center_lat))
    scale = w / (span * k)
    half_lon = span / 2 * 1.05
    half_lat = h / scale / 2 * 1.05
    image = Image.new("RGB", (w, h), SEA)
    draw = ImageDraw.Draw(image)

    def project(points: np.ndarray) -> list[tuple[float, float]]:
        x = (points[:, 0] - center_lon) * k * scale + w / 2
        y = -(points[:, 1] - center_lat) * scale + h / 2
        return list(zip(x.tolist(), y.tolist()))

    # Graticule every 5 degrees, faint, like an old atlas.
    for g in range(-30, 61, 5):
        x = (g - center_lon) * k * scale + w / 2
        draw.line([(x, 0), (x, h)], fill=GRID, width=max(1, int(1.5 * SUPERSAMPLE)))
    for g in range(30, 76, 5):
        y = -(g - center_lat) * scale + h / 2
        draw.line([(0, y), (w, y)], fill=GRID, width=max(1, int(1.5 * SUPERSAMPLE)))

    visible = [r for r, (x0, y0, x1, y1) in zip(rings, bboxes)
               if x1 >= center_lon - half_lon and x0 <= center_lon + half_lon
               and y1 >= center_lat - half_lat and y0 <= center_lat + half_lat]
    for ring in visible:
        draw.polygon(project(ring), fill=LAND)
    line = max(1, int(2.2 * SUPERSAMPLE))
    for ring in visible:
        points = project(ring)
        draw.line(points + points[:1], fill=BORDER, width=line, joint="curve")

    if pin_t > 0:
        px, py = project(np.array([[lon, lat]]))[0]
        appear = min(pin_t / 0.35, 1.0)
        # A soft glow that swells once, then the pin dot and the name above it.
        glow = Image.new("L", (w, h), 0)
        radius = (60 + 35 * math.sin(math.pi * min(pin_t, 1.0))) * SUPERSAMPLE
        ImageDraw.Draw(glow).ellipse([px - radius, py - radius, px + radius, py + radius], fill=int(190 * appear))
        glow = glow.filter(ImageFilter.GaussianBlur(radius * 0.35))
        image = Image.composite(Image.new("RGB", (w, h), pin), image, glow)
        draw = ImageDraw.Draw(image)
        dot = 22 * SUPERSAMPLE * appear
        draw.ellipse([px - dot - 5 * SUPERSAMPLE, py - dot - 5 * SUPERSAMPLE, px + dot + 5 * SUPERSAMPLE,
                      py + dot + 5 * SUPERSAMPLE], fill=INK)
        draw.ellipse([px - dot, py - dot, px + dot, py + dot], fill=pin)
        if appear >= 1:
            text = name.upper()
            box = draw.textbbox((0, 0), text, font=font, stroke_width=int(7 * SUPERSAMPLE))
            tx = min(max(px - (box[2] - box[0]) / 2, 40 * SUPERSAMPLE), w - (box[2] - box[0]) - 40 * SUPERSAMPLE)
            ty = py - 70 * SUPERSAMPLE - (box[3] - box[1])
            draw.text((tx, ty), text, font=font, fill=pin, stroke_width=int(7 * SUPERSAMPLE), stroke_fill=INK)
    return image.resize((width, height), Image.LANCZOS)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--lat", type=float, required=True)
    parser.add_argument("--lon", type=float, required=True)
    parser.add_argument("--frames", type=int, required=True)
    parser.add_argument("--font", required=True)
    parser.add_argument("--data-dir")
    parser.add_argument("--geojson")
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--width", type=int, default=1080)
    parser.add_argument("--height", type=int, default=1920)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--pin-color", default="#DC010A", help="the pin, its glow and the place name, as #RRGGBB")
    args = parser.parse_args()
    if not args.data_dir and not args.geojson:
        print("Give --data-dir or --geojson.", file=sys.stderr)
        return 1
    if not (-90 <= args.lat <= 90 and -180 <= args.lon <= 180):
        print("Latitude or longitude out of range.", file=sys.stderr)
        return 1
    pin = parse_color(args.pin_color)
    if pin is None:
        print("--pin-color must be #RRGGBB.", file=sys.stderr)
        return 1

    rings = load_countries(args.data_dir, args.geojson)
    bboxes = [(r[:, 0].min(), r[:, 1].min(), r[:, 0].max(), r[:, 1].max()) for r in rings]
    font = ImageFont.truetype(args.font, int(92 * SUPERSAMPLE))
    process = subprocess.Popen(
        [args.ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
         "-s", f"{args.width}x{args.height}", "-r", str(args.fps), "-i", "-", "-c:v", "libx264", "-preset", "medium",
         "-crf", "14", "-pix_fmt", "yuv420p", args.out], stdin=subprocess.PIPE)
    try:
        for i in range(args.frames):
            t = i / max(args.frames - 1, 1)
            # The zoom takes the first 70%; the pin lands as it ends and stays.
            center_lon, center_lat, span = view(t / 0.7, args.lat, args.lon)
            pin_t = (t - 0.55) / 0.45
            frame = draw_frame(rings, bboxes, args.width, args.height, center_lon, center_lat, span, args.lat, args.lon,
                               args.name, font, pin_t, pin)
            process.stdin.write(np.asarray(frame, dtype=np.uint8).tobytes())
    finally:
        process.stdin.close()
    if process.wait() != 0:
        print("FFmpeg failed to encode the map.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
