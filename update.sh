#!/usr/bin/env bash
# =============================================================================
# ForexTradingBot — Self-update script
#
# Shipped next to the WebAPI binary in the release bundle and invoked by
# POST /api/system/update. Downloads the newest release archive for this
# platform, swaps the app in place and restarts the process so the new
# version comes up. Safe to re-run.
#
# Layout it expects (a normal extracted bundle):
#   <install>/WebAPI          <- running binary
#   <install>/update.sh       <- this file
# =============================================================================
set -Eeuo pipefail

REPO="Opselon/ForexTradingBot"
API="https://api.github.com/repos/${REPO}/releases/latest"

# Resolve the directory this script lives in — the install root.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Pretty output only on a TTY (the API captures stdout, so keep it plain there).
if [ -t 1 ]; then
  C_RESET=$'\033[0m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_YELLOW=$'\033[33m'; C_CYAN=$'\033[36m'
else
  C_RESET=""; C_GREEN=""; C_RED=""; C_YELLOW=""; C_CYAN=""
fi
log()  { printf '%s\n' "$*"; }
ok()   { printf '%s  ✓ %s%s\n' "$C_GREEN" "$*" "$C_RESET"; }
warn() { printf '%s  ! %s%s\n' "$C_YELLOW" "$*" "$C_RESET"; }
err()  { printf '%s  ✗ %s%s\n' "$C_RED" "$*" "$C_RESET" >&2; }

# ---------- 1. Where is the running app, and how do we restart it? ----------
APP_NAME="WebAPI"
if [ ! -x "./$APP_NAME" ]; then
  err "No ./$APP_NAME executable found in $SCRIPT_DIR — nothing to update."
  err "This script only updates a bundle install (extracted release archive)."
  exit 1
fi

CURRENT_PID=""
if [ -f "$APP_NAME.pid" ]; then
  CURRENT_PID="$(cat "$APP_NAME.pid" 2>/dev/null || true)"
fi
# Fall back to pgrep if no pid file was written.
if [ -z "$CURRENT_PID" ] && command -v pgrep >/dev/null 2>&1; then
  CURRENT_PID="$(pgrep -f "$APP_NAME" | head -1 || true)"
fi

# ---------- 2. Pick the right asset for this OS/arch ----------
OS_TYPE="$(uname -s)"
ARCH="$(uname -m)"
case "$OS_TYPE" in
  Linux*)   OS="linux" ;;
  Darwin*)  OS="osx"   ;;
  MINGW*|MSYS*|CYGWIN*) OS="win" ;;
  *)        OS="$(echo "$OS_TYPE" | tr '[:upper:]' '[:lower:]')" ;;
esac
case "$ARCH" in
  x86_64|amd64)  RID="${OS}-x64"   ;;
  aarch64|arm64) RID="${OS}-arm64" ;;
  armv7l)        RID="${OS}-arm"   ;;
  *)             RID="${OS}-x64"   ;;
esac
ASSET_SUFFIX="linux-x64.tar.gz"
if [ "$OS" = "win" ]; then ASSET_SUFFIX="win-x64.zip"; fi
if [ "$ARCH" = "aarch64" ] || [ "$ARCH" = "arm64" ]; then
  ASSET_SUFFIX="${OS}-arm64.$([ "$OS" = "win" ] && echo zip || echo tar.gz)"
fi
log "Platform: $RID  (asset pattern: *$ASSET_SUFFIX)"

# ---------- 3. Query the latest release ----------
command -v curl >/dev/null 2>&1 || { err "curl is required"; exit 1; }

log "Checking the latest release from GitHub..."
RELEASE_JSON="$(curl -fsSL --max-time 30 "$API" 2>/dev/null || true)"
if [ -z "$RELEASE_JSON" ]; then
  err "Could not reach the GitHub API (offline or rate-limited?)."
  exit 1
fi

# tag_name is a single quoted field; grep + sed avoids a jq dependency.
TAG="$(printf '%s' "$RELEASE_JSON" | grep -o '"tag_name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*: *"\(.*\)"/\1/' || true)"
if [ -z "$TAG" ]; then
  err "Could not parse the latest release tag."
  exit 1
fi
log "Latest release: $TAG"

ASSET_URL="$(printf '%s' "$RELEASE_JSON" \
  | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | grep "$ASSET_SUFFIX" | head -1 \
  | sed 's/.*: *"\(.*\)"/\1/' || true)"
if [ -z "$ASSET_URL" ]; then
  err "No release asset matched *$ASSET_SUFFIX for $TAG."
  err "Download it manually: https://github.com/${REPO}/releases/tag/${TAG}"
  exit 1
fi
ok "Update asset: $(basename "$ASSET_URL")"

# ---------- 4. Download to a temp dir, verify it is non-empty ----------
WORK="$(mktemp -d "${TMPDIR:-/tmp}/ftb-update.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
ARCHIVE="$WORK/$(basename "$ASSET_URL")"

log "Downloading $ASSET_URL ..."
curl -fsSL --max-time 300 -o "$ARCHIVE" "$ASSET_URL" || {
  err "Download failed."
  exit 1
}
SIZE="$(stat -c %s "$ARCHIVE" 2>/dev/null || stat -f %z "$ARCHIVE" 2>/dev/null || echo 0)"
if [ "$SIZE" -lt 1000000 ]; then
  err "Downloaded archive is only ${SIZE} bytes — refusing to install a truncated file."
  exit 1
fi
ok "Downloaded $(( SIZE / 1024 / 1024 )) MB"

# ---------- 5. Back up the current app, extract the new one ----------
BACKUP="$SCRIPT_DIR/.backup-$(date -u +%Y%m%d-%H%M%S)"
log "Backing up the current install to $(basename "$BACKUP") ..."
mkdir -p "$BACKUP"
# Keep data/ and the .env out of the backup — they are large and must never
# be overwritten by an update.
for item in "$APP_NAME" *.dll *.json *.sh *.md .env.example docker-compose.yml Dockerfile entrypoint.sh; do
  [ -e "$item" ] && cp -a "$item" "$BACKUP/" 2>/dev/null || true
done
ok "Backup ready: $BACKUP"

log "Extracting the new bundle..."
EXTRACT="$WORK/bundle"
mkdir -p "$EXTRACT"
case "$ASSET_URL" in
  *.tar.gz) tar -xzf "$ARCHIVE" -C "$EXTRACT" ;;
  *.zip)    command -v unzip >/dev/null 2>&1 || { err "unzip is required for a .zip release"; exit 1; }
            unzip -qq -o "$ARCHIVE" -d "$EXTRACT" ;;
  *)        err "Unknown archive format: $ASSET_URL"; exit 1 ;;
esac

# The archive may unpack into ./bundle or directly into .; find the WebAPI dir.
SRC_DIR="$EXTRACT"
if [ ! -x "$SRC_DIR/$APP_NAME" ] && [ -x "$EXTRACT/bundle/$APP_NAME" ]; then
  SRC_DIR="$EXTRACT/bundle"
fi
if [ ! -x "$SRC_DIR/$APP_NAME" ]; then
  err "Extracted archive has no $APP_NAME executable — the release layout changed?"
  exit 1
fi

# ---------- 6. Swap files in place, preserving local state ----------
log "Installing the new version..."
# Data and user config survive the update.
for item in data .env appsettings.Production.json *.db; do
  [ -e "$item" ] && mkdir -p "$WORK/keep" && cp -a "$item" "$WORK/keep/" 2>/dev/null || true
done

# Remove the old binaries and copy the new ones over.
find "$SCRIPT_DIR" -maxdepth 1 -type f \( -name "$APP_NAME" -o -name '*.dll' -o -name '*.json' -o -name '*.pdb' \) -delete 2>/dev/null || true
cp -a "$SRC_DIR"/. "$SCRIPT_DIR"/
chmod +x "$SCRIPT_DIR/$APP_NAME" 2>/dev/null || true

# Restore preserved state.
if [ -d "$WORK/keep" ]; then
  cp -a "$WORK/keep"/. "$SCRIPT_DIR"/ 2>/dev/null || true
fi
ok "New version installed"

# ---------- 7. Restart ----------
restart_native() {
  if [ -n "$CURRENT_PID" ] && kill -0 "$CURRENT_PID" 2>/dev/null; then
    log "Stopping the running process (pid $CURRENT_PID)..."
    kill -TERM "$CURRENT_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$CURRENT_PID" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL "$CURRENT_PID" 2>/dev/null || true
  fi

  log "Starting the new version..."
  # Detach fully so the API request that triggered us can finish.
  if command -v setsid >/dev/null 2>&1; then
    setsid ./"$APP_NAME" > "$APP_NAME.out.log" 2>&1 < /dev/null &
  else
    nohup ./"$APP_NAME" > "$APP_NAME.out.log" 2>&1 < /dev/null &
  fi
  NEW_PID=$!
  printf '%s' "$NEW_PID" > "$APP_NAME.pid"
  ok "Started pid $NEW_PID"
}

restart_docker() {
  log "Running under Docker — restarting the container..."
  if docker compose version >/dev/null 2>&1; then
    docker compose up -d --force-recreate forex-trading-bot-app 2>/dev/null \
      || docker compose restart forex-trading-bot-app 2>/dev/null \
      || docker compose restart 2>/dev/null || true
  elif command -v docker-compose >/dev/null 2>&1; then
    docker-compose up -d --force-recreate forex-trading-bot-app 2>/dev/null \
      || docker-compose restart 2>/dev/null || true
  else
    warn "No compose client found — restart the container manually."
  fi
}

if [ -n "${FOREXBOT_IN_DOCKER:-}" ] || [ -f "/.dockerenv" ]; then
  restart_docker
else
  restart_native
fi

# Give it a moment, then confirm it is alive.
sleep 3
if [ -n "${NEW_PID:-}" ] && kill -0 "$NEW_PID" 2>/dev/null; then
  ok "Update complete — $TAG is running."
elif command -v curl >/dev/null 2>&1 && curl -fsS -m 5 http://localhost:5000/healthz >/dev/null 2>&1; then
  ok "Update complete — $TAG is healthy."
else
  warn "Update finished but the app did not confirm health within 3s."
  warn "Check $APP_NAME.out.log and the backup at $BACKUP."
fi
log "Done."
