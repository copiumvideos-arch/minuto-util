"""Tests of mapinsert.py on a small hand-made GeoJSON: real drawing and FFmpeg, no download."""

import json
import os
import subprocess
import sys

import cv2
import numpy as np
import pytest

from mapinsert import PIN, load_countries, parse_color

HERE = os.path.dirname(os.path.abspath(__file__))
FONT = os.path.join(HERE, "..", "assets", "fonts", "RobotoSerif-Black.ttf")

SQUARES = {"type": "FeatureCollection", "features": [
    {"type": "Feature", "properties": {}, "geometry": {"type": "Polygon",
                                                       "coordinates": [[[-5, 43], [8, 43], [8, 51], [-5, 51], [-5, 43]]]}},
    {"type": "Feature", "properties": {}, "geometry": {"type": "MultiPolygon",
                                                       "coordinates": [[[[-6, 50.5], [2, 50.5], [2, 58], [-6, 58], [-6, 50.5]]]]}},
]}


def test_zooms_in_and_pins_the_place(tmp_path):
    data = tmp_path / "countries.geojson"
    data.write_text(json.dumps(SQUARES), encoding="utf-8")
    out = tmp_path / "map.mp4"
    subprocess.run([sys.executable, os.path.join(HERE, "mapinsert.py"), "--out", str(out), "--name", "Normandia",
                    "--lat", "49.4", "--lon", "-0.9", "--frames", "30", "--font", FONT, "--geojson", str(data),
                    "--width", "360", "--height", "640"], check=True)
    capture = cv2.VideoCapture(str(out))
    frames = []
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        frames.append(frame)
    assert len(frames) == 30
    # BGR frames: the red pin and name appear only at the end.
    pin = np.array([PIN[2], PIN[1], PIN[0]])

    def pin_share(frame):
        return (np.abs(frame.astype(int) - pin).sum(axis=2) < 60).mean()

    assert pin_share(frames[0]) < 0.001
    assert pin_share(frames[-1]) > 0.003
    # The pin sits near the middle at the end: the zoom ends centered on the place.
    mask = np.abs(frames[-1].astype(int) - pin).sum(axis=2) < 60
    ys, xs = np.nonzero(mask)
    assert abs(xs.mean() - 180) < 60


def test_refuses_files_that_are_not_geojson_polygons(tmp_path):
    bad = tmp_path / "bad.geojson"
    bad.write_text(json.dumps({"type": "FeatureCollection", "features": [{"geometry": {"type": "Point", "coordinates": [1, 2]}}]}),
                   encoding="utf-8")
    with pytest.raises(RuntimeError):
        load_countries(None, str(bad))
    with pytest.raises(json.JSONDecodeError):
        (tmp_path / "html.geojson").write_text("<html>not json</html>", encoding="utf-8")
        load_countries(None, str(tmp_path / "html.geojson"))


def test_pin_color_is_a_hex_color():
    assert parse_color("#DC010A") == (220, 1, 10)
    assert parse_color("f7e2c7") == (247, 226, 199)
    assert parse_color("#FFD70") is None
    assert parse_color("red; rm") is None
