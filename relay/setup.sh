#!/usr/bin/env bash
# Setup of the relay, run by the owner in Git Bash after `npx wrangler@4 login` (free Cloudflare account):
#   bash relay/setup.sh               Minuto Útil, as first set up: deploys the Worker, gives it the shared secrets and
#                                     the minuto-util bot's, and points that bot's webhook at <worker>/telegram.
#   bash relay/setup.sh <channel id>  Adds another channel's bot (a folder in channels/): deploys the Worker, gives it
#                                     TELEGRAM_BOT_TOKEN_<ID> and TELEGRAM_WEBHOOK_SECRET_<ID> and points that bot's
#                                     webhook at <worker>/telegram/<id>. Minuto Útil's bot and secrets are not touched.
# Secrets go through wrangler's standard input (nothing printed). The values come from Windows user environment
# variables named with the channel's channel.envPrefix: <PREFIX>_TELEGRAM_BOT_TOKEN, and for minuto-util also
# MINUTO_TELEGRAM_CHAT_ID (the owner's chat, shared by every bot) and MINUTO_RELAY_GITHUB_TOKEN (a fine-grained GitHub
# token for copiumvideos-arch/minuto-util only, permission "Actions: read and write"). A channel whose owner chat is
# not the shared one sets <PREFIX>_TELEGRAM_CHAT_ID too. Safe to run again: a new webhook secret is made each time and
# given to both sides.
set -euo pipefail
cd "$(dirname "$0")"
CHANNEL="${1:-minuto-util}"
[[ "$CHANNEL" =~ ^[a-z0-9](-?[a-z0-9])*$ && ${#CHANNEL} -le 40 ]] || { echo "\"$CHANNEL\" is not a channel id" >&2; exit 1; }
CONFIG="../channels/$CHANNEL/config.json"
[ -f "$CONFIG" ] || { echo "no channel $CHANNEL ($CONFIG is missing)" >&2; exit 1; }
PREFIX=$(node -e 'const c = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).channel ?? {}; process.stdout.write(c.envPrefix ?? "")' "$CONFIG")
[[ "$PREFIX" =~ ^[A-Z][A-Z0-9_]{0,30}$ ]] || { echo "$CONFIG has no channel.envPrefix" >&2; exit 1; }

user_var() {
  local value="${!1:-}"
  if [ -z "$value" ] && command -v powershell >/dev/null; then
    value=$(powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('$1','User')" | tr -d '\r\n')
  fi
  [ -n "$value" ] || { echo "$1 is not set" >&2; exit 1; }
  printf '%s' "$value"
}

# Instead of `wrangler login`: a Cloudflare API token ("Edit Cloudflare Workers" template) in the user environment.
if [ -z "${CLOUDFLARE_API_TOKEN:-}" ] && command -v powershell >/dev/null; then
  CLOUDFLARE_API_TOKEN=$(powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('CLOUDFLARE_API_TOKEN','User')" | tr -d '\r\n')
  [ -n "$CLOUDFLARE_API_TOKEN" ] && export CLOUDFLARE_API_TOKEN || unset CLOUDFLARE_API_TOKEN
fi

BOT=$(user_var "${PREFIX}_TELEGRAM_BOT_TOKEN")
HOOK=$(openssl rand -hex 32)
if [ "$CHANNEL" = minuto-util ]; then
  CHAT=$(user_var MINUTO_TELEGRAM_CHAT_ID)
  GH_PAT=$(user_var MINUTO_RELAY_GITHUB_TOKEN)
else
  # The secret names the Worker reads for this channel (channelFor in relay/src/worker.js).
  SUFFIX=$(printf '%s' "$CHANNEL" | tr 'a-z-' 'A-Z_')
  # Only when this channel's owner chat is not the shared OWNER_CHAT_ID.
  OWN_CHAT="${PREFIX}_TELEGRAM_CHAT_ID"
  OWN_CHAT="${!OWN_CHAT:-}"
  if [ -z "$OWN_CHAT" ] && command -v powershell >/dev/null; then
    OWN_CHAT=$(powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('${PREFIX}_TELEGRAM_CHAT_ID','User')" | tr -d '\r\n')
  fi
fi

url=$(npx --yes wrangler@4 deploy 2>&1 | tee /dev/stderr | grep -oE 'https://[a-z0-9.-]+\.workers\.dev' | head -n 1)
[ -n "$url" ] || { echo "could not read the Worker address from wrangler deploy" >&2; exit 1; }

if [ "$CHANNEL" = minuto-util ]; then
  printf '%s' "$BOT" | npx --yes wrangler@4 secret put TELEGRAM_BOT_TOKEN >/dev/null
  printf '%s' "$CHAT" | npx --yes wrangler@4 secret put OWNER_CHAT_ID >/dev/null
  printf '%s' "$GH_PAT" | npx --yes wrangler@4 secret put GITHUB_TOKEN >/dev/null
  printf '%s' "$HOOK" | npx --yes wrangler@4 secret put TELEGRAM_WEBHOOK_SECRET >/dev/null
  hook_url="$url/telegram"
else
  printf '%s' "$BOT" | npx --yes wrangler@4 secret put "TELEGRAM_BOT_TOKEN_$SUFFIX" >/dev/null
  printf '%s' "$HOOK" | npx --yes wrangler@4 secret put "TELEGRAM_WEBHOOK_SECRET_$SUFFIX" >/dev/null
  if [ -n "$OWN_CHAT" ]; then printf '%s' "$OWN_CHAT" | npx --yes wrangler@4 secret put "OWNER_CHAT_ID_$SUFFIX" >/dev/null; fi
  hook_url="$url/telegram/$CHANNEL"
fi

# The bot token goes in Telegram's URL; curl reads it from a config on stdin so it is not on the command line.
answer=$(printf 'url = "https://api.telegram.org/bot%s/setWebhook"\n' "$BOT" | curl -sS -K - \
  --data-urlencode "url=$hook_url" --data-urlencode "secret_token=$HOOK" \
  --data-urlencode 'allowed_updates=["message","callback_query"]' --data-urlencode "drop_pending_updates=true")
echo "Telegram ($CHANNEL): $answer"
echo "Relay: $hook_url"
