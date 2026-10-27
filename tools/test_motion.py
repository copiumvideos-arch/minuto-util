"""End-to-end tests of motion.py on synthetic pictures: real OpenCV and FFmpeg, a generated depth map instead of the model."""

import os
import subprocess
import sys

import cv2
import numpy as np
import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
WIDTH, HEIGHT, FPS = 360, 640, 30


def synthetic_scene(path_image: str, path_depth: str) -> None:
    """A textured far wall with a bright near box in front of it; the depth map says the box is near."""
    rng = np.random.default_rng(1)
    h, w = 900, 1200
    far = (rng.random((h // 20, w // 20)) * 120 + 40).astype(np.uint8)
    far = cv2.resize(far, (w, h), interpolation=cv2.INTER_NEAREST)
    image = cv2.merge([far, far, far])
    cv2.rectangle(image, (500, 380), (700, 620), (255, 255, 255), -1)
    cv2.rectangle(image, (540, 420), (660, 580), (0, 0, 255), -1)
    depth = np.full((h, w), 0.0, np.float32)
    depth[380:620, 500:700] = 1.0
    cv2.imwrite(path_image, image)
    cv2.imwrite(path_depth, (depth * 65535).astype(np.uint16))


def render(tmp_path, effect: str, frames: int, extra: list[str] | None = None) -> np.ndarray:
    image, depth, out = tmp_path / "scene.png", tmp_path / "scene.depth.png", tmp_path / f"{effect}.mp4"
    synthetic_scene(str(image), str(depth))
    subprocess.run([sys.executable, os.path.join(HERE, "motion.py"), "render", "--image", str(image), "--out", str(out),
                    "--effect", effect, "--frames", str(frames), "--width", str(WIDTH), "--height", str(HEIGHT),
                    "--fps", str(FPS), "--depth", str(depth), "--supersample", "1.5", "--travel", "0.08", *(extra or [])],
                   check=True)
    capture = cv2.VideoCapture(str(out))
    result = []
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        result.append(frame)
    return np.stack(result)


def shift_of(a: np.ndarray, b: np.ndarray, region: tuple[slice, slice]) -> float:
    """Horizontal shift of a region between two frames, by phase correlation."""
    ga = cv2.cvtColor(a[region], cv2.COLOR_BGR2GRAY).astype(np.float32)
    gb = cv2.cvtColor(b[region], cv2.COLOR_BGR2GRAY).astype(np.float32)
    (dx, _), _ = cv2.phaseCorrelate(ga, gb)
    return dx


def test_parallax_moves_near_pixels_more_than_far_ones_without_holes(tmp_path):
    frames = render(tmp_path, "parallax", 45)
    assert frames.shape == (45, HEIGHT, WIDTH, 3)
    first, last = frames[0], frames[-1]
    # Locate the red box (the near object) in the first frame.
    red = (first[..., 2] > 180) & (first[..., 1] < 80)
    ys, xs = np.nonzero(red)
    assert len(xs) > 100
    background = (slice(10, 120), slice(10, WIDTH - 10))
    # The flat-colored box is tracked by its centroid (phase correlation needs texture); the wall by correlation.
    red_last = (last[..., 2] > 180) & (last[..., 1] < 80)
    near = float(np.nonzero(red_last)[1].mean() - xs.mean())
    far = shift_of(first, last, background)
    assert abs(near - far) > 4, (near, far)
    # The near box and the far wall move in opposite directions around the still mid-depth plane.
    assert near * far < 0, (near, far)
    # Nothing black leaks in at the edges (backward sampling leaves no holes).
    assert (frames.max(axis=3) < 8).mean() < 0.001


def test_ken_burns_moves_smoothly(tmp_path):
    frames = render(tmp_path, "kenburns", 40, ["--zoom", "in"])
    assert len(frames) == 40
    region = (slice(HEIGHT // 4, 3 * HEIGHT // 4), slice(WIDTH // 4, 3 * WIDTH // 4))
    steps = [shift_of(frames[i], frames[i + 1], region) for i in range(10, 30)]
    # A steady glide: every frame moves about as much as its neighbours, no jumps or stalls.
    assert max(abs(s) for s in steps) < 3
    assert np.std(np.diff(steps)) < 0.5, steps


def test_fills_a_portrait_frame_from_a_landscape_picture_without_bars(tmp_path):
    frames = render(tmp_path, "kenburns", 5)
    edges = np.concatenate([frames[:, :, :4].reshape(-1, 3), frames[:, :, -4:].reshape(-1, 3),
                            frames[:, :4].reshape(-1, 3), frames[:, -4:].reshape(-1, 3)])
    assert (edges.max(axis=1) < 8).mean() < 0.01



def test_crop_aims_at_the_near_subject():
    from motion import focus_x

    depth = np.zeros((100, 400), np.float32)
    depth[30:90, 300:360] = 1.0  # a person near the camera, on the right
    assert 0.55 < focus_x(depth) < 0.7
    assert focus_x(np.zeros((10, 10), np.float32)) == 0.5


if __name__ == "__main__":
    sys.exit(pytest.main([__file__, "-q"]))
