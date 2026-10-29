"""Which channels a GitHub Actions run is for, read from channels/<id>/config.json (standard library only, so the
workflows run it with the runner's python3 before anything is installed).

    python3 tools/channels.py matrix <field>   prints a JSON list for a job matrix, one entry per channel:
        {"id", "prefix", "state", "release"}. A run started by hand (CHANNEL set, from the workflow's channel input)
        is for that one channel. A scheduled run (SCHEDULE set to the cron line that fired, like "0 14 * * *") is for
        every channel whose <field> (scheduleUtcHours or ideasUtcHours) has that hour; none gives [].
    python3 tools/channels.py outputs           prints the same entry for CHANNEL as name=value lines for GITHUB_OUTPUT.

"state" is the channel's folder in the state branch checkout (./state): "state" for Minuto Util, whose files were
there before channels, "state/channels/<id>" for the others. "release" starts the GitHub release name of each run.
"""

import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ID = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")
PREFIX = re.compile(r"^[A-Z][A-Z0-9_]{0,30}$")
CRON = re.compile(r"^(\d{1,2}) (\d{1,2}) \* \* \*$")
LEGACY = "minuto-util"


def load(channel_id: str, root: str = ROOT) -> dict:
    """The channel's entry; refuses ids and configs that could reach outside its folders or another channel's secrets."""
    if not ID.match(channel_id or ""):
        raise ValueError(f"not a channel id: {channel_id[:40]!r}")
    path = os.path.join(root, "channels", channel_id, "config.json")
    if not os.path.isfile(path):
        raise ValueError(f"no channel {channel_id} (no channels/{channel_id}/config.json)")
    with open(path, encoding="utf-8") as f:
        channel = json.load(f).get("channel", {})
    prefix = channel.get("envPrefix", "")
    folder = channel.get("stateFolder", "")
    release = channel.get("releasePrefix", "")
    if not PREFIX.match(prefix):
        raise ValueError(f"channels/{channel_id}/config.json: bad channel.envPrefix")
    # The same rule as Channel.Resolve in the app: the root of the state branch is Minuto Util's (its files were there
    # before channels), every other channel has channels/<id>, so no two channels can share a file.
    if folder != (f"channels/{channel_id}" if channel_id != LEGACY else "."):
        raise ValueError(f"channels/{channel_id}/config.json: channel.stateFolder must be channels/{channel_id}")
    if release != ("run-" if channel_id == LEGACY else f"run-{channel_id}-"):
        raise ValueError(f"channels/{channel_id}/config.json: channel.releasePrefix must be run-{channel_id}-")
    return {
        "id": channel_id,
        "prefix": prefix,
        "state": "state" if folder == "." else f"state/{folder}",
        "release": release,
        "hours": {k: channel.get(k, []) for k in ("scheduleUtcHours", "ideasUtcHours")},
    }


def all_channels(root: str = ROOT) -> list:
    folder = os.path.join(root, "channels")
    ids = sorted(d for d in os.listdir(folder) if ID.match(d) and os.path.isfile(os.path.join(folder, d, "config.json")))
    return [load(i, root) for i in ids]


def entry(channel: dict) -> dict:
    return {k: channel[k] for k in ("id", "prefix", "state", "release")}


def matrix(field: str, channel_id: str, schedule: str, root: str = ROOT) -> list:
    if field not in ("scheduleUtcHours", "ideasUtcHours"):
        raise ValueError(f"unknown field {field}")
    if channel_id:
        return [entry(load(channel_id, root))]
    match = CRON.match(schedule.strip())
    if not match:
        raise ValueError(f"neither a channel nor a daily cron line: {schedule[:40]!r}")
    hour = int(match.group(2))
    return [entry(c) for c in all_channels(root) if hour in c["hours"][field]]


def main(argv: list) -> int:
    try:
        if len(argv) == 2 and argv[0] == "matrix":
            print(json.dumps(matrix(argv[1], os.environ.get("CHANNEL", ""), os.environ.get("SCHEDULE", ""))))
            return 0
        if len(argv) == 1 and argv[0] == "outputs":
            for key, value in entry(load(os.environ.get("CHANNEL", ""))).items():
                print(f"{key}={value}")
            return 0
    except ValueError as error:
        print(error, file=sys.stderr)
        return 1
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
