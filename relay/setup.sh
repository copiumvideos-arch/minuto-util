#!/usr/bin/env bash
# One-time setup of the relay, run by the owner in Git Bash after `npx wrangler@4 login` (free Cloudflare account):
#   bash relay/setup.sh
# Deploys the Worker, gives it its secrets through wrangler's standard input (nothing printed) and points the
# Telegram bot's webhook at it. Needs these Windows user environment variables: MINUTO_TELEGRAM_BOT_TOKEN,
# MINUTO_TELEGRAM_CHAT_ID and MINUTO_RELAY_GITHUB_TOKEN (a fine-grained GitHub token for
# copiumvideos-arch/minuto-util only, permission "Actions: read and write"). Safe to run again: a new webhook secret
# is made each time and given to both sides.
set -euo pipefail
cd "$(dirname "$0")"

user_var() {
  local value="${!1:-}"
  if [ -z "$value" ] && command -v powershell >/dev/null; then
    value=$(powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('$1','User')" | tr -d '\r\n')
  fi
  [ -n "$value" ] || { echo "$1 is not set" >&2; exit 1; }
  printf '%s' "$value"
}

BOT=$(user_var MINUTO_TELEGRAM_BOT_TOKEN)
CHAT=$(user_var MINUTO_TELEGRAM_CHAT_ID)
GH_PAT=$(user_var MINUTO_RELAY_GITHUB_TOKEN)
HOOK=$(openssl rand -hex 32)

url=$(npx --yes wrangler@4 deploy 2>&1 | tee /dev/stderr | grep -oE 'https://[a-z0-9.-]+\.workers\.dev' | head -n 1)
[ -n "$url" ] || { echo "could not read the Worker address from wrangler deploy" >&2; exit 1; }

printf '%s' "$BOT" | npx --yes wrangler@4 secret put TELEGRAM_BOT_TOKEN >/dev/null
printf '%s' "$CHAT" | npx --yes wrangler@4 secret put OWNER_CHAT_ID >/dev/null
printf '%s' "$GH_PAT" | npx --yes wrangler@4 secret put GITHUB_TOKEN >/dev/null
printf '%s' "$HOOK" | npx --yes wrangler@4 secret put TELEGRAM_WEBHOOK_SECRET >/dev/null

# The bot token goes in Telegram's URL; curl reads it from a config on stdin so it is not on the command line.
answer=$(printf 'url = "https://api.telegram.org/bot%s/setWebhook"\n' "$BOT" | curl -sS -K - \
  --data-urlencode "url=$url/telegram" --data-urlencode "secret_token=$HOOK" \
  --data-urlencode 'allowed_updates=["message","callback_query"]' --data-urlencode "drop_pending_updates=true")
echo "Telegram: $answer"
echo "Relay: $url/telegram"
