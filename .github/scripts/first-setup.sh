#!/usr/bin/env bash
# One-time setup of the cloud runs, run by the owner from the repository root in Git Bash:
#   bash .github/scripts/first-setup.sh
# Creates the private repository, pushes main, creates the state branch and copies the keys and sign-ins into the
# repository's Actions secrets (cloud secrets: through gh's standard input, nothing printed). Safe to run again.
set -euo pipefail
REPO=copiumvideos-arch/minuto-util
GIT_AUTH=(-c credential.username=copiumvideos-arch -c credential.helper= -c "credential.helper=!gh auth git-credential")

gh auth switch --user copiumvideos-arch >/dev/null 2>&1 || true
if ! gh repo view "$REPO" >/dev/null 2>&1; then
  gh repo create "$REPO" --private --description "Minuto Útil: automated pt-BR history Shorts"
fi
[ "$(gh repo view "$REPO" --json visibility --jq .visibility)" = "PRIVATE" ] || { echo "$REPO is not private; stopping" >&2; exit 1; }

git remote get-url origin >/dev/null 2>&1 || git remote add origin "https://github.com/$REPO.git"
git "${GIT_AUTH[@]}" push -u origin main

if ! git ls-remote --exit-code --heads origin state >/dev/null 2>&1; then
  tmp=$(mktemp -d)
  git -C "$tmp" init -q -b state
  printf '# state\n\nWritten by the workflows: runs/<tag>.json, quota-state.json and the sealed token files.\n' > "$tmp/README.md"
  git -C "$tmp" add README.md
  git -C "$tmp" -c user.name=minuto-util-bot -c user.email=41898282+github-actions[bot]@users.noreply.github.com commit -q -m "state branch"
  git -C "$tmp" "${GIT_AUTH[@]}" push -q "https://github.com/$REPO.git" state
  rm -rf "$tmp"
fi

dotnet run --project src/MinutoUtil.Cli -- cloud secrets --repo "$REPO"
echo "Done. Branches: $(gh api "repos/$REPO/branches" --jq '[.[].name] | join(", ")')"
