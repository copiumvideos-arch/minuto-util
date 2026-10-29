"""Tests of channels.py: which channels a workflow run is for, on the real channels/ folder and on a two-channel copy."""

import json
import os
import shutil
import subprocess
import sys

import pytest

import channels

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def two_channels(tmp_path):
    """A repository root with the real Minuto Util config and a second channel at other hours."""
    shutil.copytree(os.path.join(ROOT, "channels", "minuto-util"), tmp_path / "channels" / "minuto-util")
    other = tmp_path / "channels" / "test-spoiler"
    other.mkdir(parents=True)
    (other / "config.json").write_text(json.dumps({"channel": {
        "name": "Teste", "envPrefix": "SPOILER", "tokenFolder": "%LOCALAPPDATA%/MinutoUtil/channels/test-spoiler",
        "stateFolder": "channels/test-spoiler", "releasePrefix": "run-test-spoiler-",
        "scheduleUtcHours": [14, 23], "ideasUtcHours": []}}), encoding="utf-8")
    return str(tmp_path)


def test_minuto_keeps_its_secrets_state_folder_and_releases():
    assert channels.matrix("scheduleUtcHours", "minuto-util", "") == [
        {"id": "minuto-util", "prefix": "MINUTO", "state": "state", "release": "run-"}]


def test_each_scheduled_hour_picks_its_own_channel():
    for cron in ("0 14 * * *", "0 22 * * *"):
        assert [c["id"] for c in channels.matrix("scheduleUtcHours", "", cron)] == ["minuto-util"]
    for cron in ("0 15 * * *", "0 23 * * *"):
        assert [c["id"] for c in channels.matrix("scheduleUtcHours", "", cron)] == ["profissao-spoiler"]
    assert [c["id"] for c in channels.matrix("ideasUtcHours", "", "0 9 * * *")] == ["minuto-util", "profissao-spoiler"]
    assert channels.matrix("scheduleUtcHours", "", "0 9 * * *") == []


def test_a_shared_hour_renders_both_channels_and_each_gets_its_own_names(tmp_path):
    root = two_channels(tmp_path)
    both = channels.matrix("scheduleUtcHours", "", "0 14 * * *", root)
    assert [c["id"] for c in both] == ["minuto-util", "test-spoiler"]
    spoiler = both[1]
    assert spoiler == {"id": "test-spoiler", "prefix": "SPOILER", "state": "state/channels/test-spoiler", "release": "run-test-spoiler-"}
    assert [c["id"] for c in channels.matrix("scheduleUtcHours", "", "0 23 * * *", root)] == ["test-spoiler"]
    assert [c["id"] for c in channels.matrix("scheduleUtcHours", "", "0 22 * * *", root)] == ["minuto-util"]
    assert [c["id"] for c in channels.matrix("ideasUtcHours", "", "0 9 * * *", root)] == ["minuto-util"]


@pytest.mark.parametrize("bad", ["../minuto-util", "Minuto", "minuto util", "x; rm -rf /", "nope"])
def test_bad_or_unknown_channels_are_refused(bad):
    with pytest.raises(ValueError):
        channels.matrix("scheduleUtcHours", bad, "")


@pytest.mark.parametrize("change", [{"stateFolder": "."}, {"stateFolder": "../x"}, {"stateFolder": ""},
                                    {"envPrefix": "minuto"}, {"envPrefix": ""}, {"releasePrefix": "x-"},
                                    {"releasePrefix": "run-"}])
def test_a_config_that_could_share_another_channels_files_is_refused(tmp_path, change):
    root = two_channels(tmp_path)
    path = os.path.join(root, "channels", "test-spoiler", "config.json")
    with open(path, encoding="utf-8") as f:
        config = json.load(f)
    config["channel"].update(change)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(config, f)
    with pytest.raises(ValueError):
        channels.load("test-spoiler", root)


def test_the_command_line_prints_a_matrix_and_step_outputs():
    env = {**os.environ, "CHANNEL": "", "SCHEDULE": "0 14 * * *"}
    out = subprocess.run([sys.executable, os.path.join(HERE, "channels.py"), "matrix", "scheduleUtcHours"],
                         env=env, capture_output=True, text=True, check=True).stdout
    assert json.loads(out) == [{"id": "minuto-util", "prefix": "MINUTO", "state": "state", "release": "run-"}]

    env["CHANNEL"] = "minuto-util"
    out = subprocess.run([sys.executable, os.path.join(HERE, "channels.py"), "outputs"],
                         env=env, capture_output=True, text=True, check=True).stdout
    assert out.splitlines() == ["id=minuto-util", "prefix=MINUTO", "state=state", "release=run-"]

    env["CHANNEL"] = "../etc"
    assert subprocess.run([sys.executable, os.path.join(HERE, "channels.py"), "outputs"], env=env,
                          capture_output=True, text=True).returncode == 1
