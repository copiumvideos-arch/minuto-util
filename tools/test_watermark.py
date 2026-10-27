"""Tests of watermark.py on a drawn emblem: a red ring on black, slightly off center, like the real one."""

import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))


def drawn_emblem(path: str) -> None:
    image = Image.new("RGB", (600, 600), (5, 4, 3))
    draw = ImageDraw.Draw(image)
    draw.ellipse([60, 50, 540, 530], outline=(220, 1, 10), width=14)
    draw.rectangle([250, 240, 350, 340], fill=(247, 226, 199))
    image.save(path)


def test_cuts_the_badge_out_with_a_soft_edge(tmp_path):
    emblem, out = tmp_path / "emblem.png", tmp_path / "watermark.png"
    drawn_emblem(str(emblem))
    subprocess.run([sys.executable, os.path.join(HERE, "watermark.py"), "--emblem", str(emblem), "--out", str(out),
                    "--size", "96"], check=True)
    image = Image.open(out)
    assert image.size == (96, 96) and image.mode == "RGBA"
    pixels = np.asarray(image).astype(int)
    alpha = pixels[..., 3]
    # Corners transparent, middle opaque cream, the ring opaque red right at the edge of the cut.
    assert alpha[0, 0] == 0 and alpha[0, -1] == 0 and alpha[-1, 0] == 0 and alpha[-1, -1] == 0
    assert alpha[48, 48] == 255 and pixels[48, 48, 0] > 200
    ring = pixels[48, 2]
    assert ring[3] > 240 and ring[0] > 150 and ring[1] < 60
    # Anti-aliased: some partly transparent pixels along the edge, and nothing visible outside the circle (Lanczos may leave a trace).
    assert ((alpha > 0) & (alpha < 255)).sum() > 40
    yy, xx = np.mgrid[0:96, 0:96]
    outside = np.hypot(xx + 0.5 - 48, yy + 0.5 - 48) > 49.5
    assert alpha[outside].max() < 10


def test_refuses_an_image_without_a_red_ring(tmp_path):
    plain, out = tmp_path / "plain.png", tmp_path / "watermark.png"
    Image.new("RGB", (200, 200), (0, 0, 0)).save(plain)
    result = subprocess.run([sys.executable, os.path.join(HERE, "watermark.py"), "--emblem", str(plain), "--out", str(out)],
                            capture_output=True, text=True)
    assert result.returncode != 0
    assert not out.exists()
