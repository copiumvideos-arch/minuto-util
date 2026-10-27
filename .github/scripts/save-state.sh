#!/usr/bin/env bash
# Commits the state branch checked out in ./state (runs/*.json, quota-state.json, the sealed token files) and pushes
# it. Each run writes its own runs/<tag>.json, so a push that races another job only needs a rebase.
set -euo pipefail
cd state
git config user.name "minuto-util-bot"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git add -A
if git diff --cached --quiet; then exit 0; fi
git commit -q -m "$1"
for attempt in 1 2 3 4 5; do
  if git push -q origin HEAD:state; then exit 0; fi
  sleep $((attempt * 5))
  git pull -q --rebase origin state
done
echo "could not push the state branch" >&2
exit 1
