#!/usr/bin/env bash
# Runs the app for one channel on the runner: bash .github/scripts/cli.sh <command> [options]
# CHANNEL (the channel id) and PREFIX (its channel.envPrefix, from tools/channels.py) come from the job. Each step gets
# its channel's secrets under fixed names, CH_<NAME>, picked with secrets[format('{0}_<NAME>', prefix)], because a
# workflow cannot name an environment variable from an expression. This passes them to the app under the channel's
# own names (CH_TELEGRAM_BOT_TOKEN becomes MINUTO_TELEGRAM_BOT_TOKEN for Minuto Util), for this command only, and adds
# --channel. Values are never printed.
set -euo pipefail
[[ "${CHANNEL:-}" =~ ^[a-z0-9-]{1,40}$ ]] || { echo "cli.sh: CHANNEL is not a channel id" >&2; exit 1; }
[[ "${PREFIX:-}" =~ ^[A-Z][A-Z0-9_]{0,30}$ ]] || { echo "cli.sh: PREFIX is not an env prefix" >&2; exit 1; }
for var in $(compgen -e); do
  case "$var" in
    CH_*)
      export "${PREFIX}_${var#CH_}=${!var}"
      unset "$var"
      ;;
  esac
done
exec dotnet src/MinutoUtil.Cli/bin/Release/net10.0/minuto-util.dll "$@" --channel "$CHANNEL"
