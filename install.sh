#!/usr/bin/env bash
# =============================================================================
# ForexTradingBot — One-Command Installer (Linux / macOS)
#
#   curl -fsSL https://raw.githubusercontent.com/Opselon/ForexTradingBot/master/install.sh | bash
#
# What it does:
#   1) Clones the repository (or updates an existing clone)
#   2) Asks: Docker install (recommended) or Normal install (.NET on host)
#   3) Docker  -> builds the image, starts PostgreSQL + Redis + API, waits
#                 for /health and prints the result
#   4) Normal  -> installs the .NET 9 SDK if missing, then starts the app;
#                 on FIRST STARTUP the app itself asks which database you want:
#                 PostgreSQL / SQLite / SQL Server  (saved by the Easy Setup wizard)
#
# Everything is idempotent: re-running the script is safe.
# =============================================================================
set -Eeuo pipefail

REPO="https://github.com/Opselon/ForexTradingBot.git"
DIR="${FOREXBOT_DIR:-$HOME/ForexTradingBot}"
MIN_DOTNET_MAJOR=9
HEALTH_URL_DOCKER="http://localhost:8080/healthz"
HEALTH_URL_NATIVE="http://localhost:5000/healthz"

# ---------- pretty output ----------
if [ -t 1 ]; then
  C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
  C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_YELLOW=$'\033[33m'; C_BLUE=$'\033[34m'; C_CYAN=$'\033[36m'
else
  C_RESET=""; C_BOLD=""; C_DIM=""; C_GREEN=""; C_RED=""; C_YELLOW=""; C_BLUE=""; C_CYAN=""
fi

step() { printf '\n%s▶ %s%s\n' "$C_BOLD$C_CYAN" "$*" "$C_RESET"; }
ok()   { printf '%s  ✓ %s%s\n' "$C_GREEN" "$*" "$C_RESET"; }
warn() { printf '%s  ! %s%s\n' "$C_YELLOW" "$*" "$C_RESET"; }
err()  { printf '%s  ✗ %s%s\n' "$C_RED" "$*" "$C_RESET" >&2; }
info() { printf '%s    %s%s\n' "$C_DIM" "$*" "$C_RESET"; }

banner() {
  printf '%s' "$C_BOLD$C_BLUE"
  cat <<'EOF'
   ______      _          ______            _       _
  |  ____|    | |        |  ____|          | |     | |
  | |__ __ _ _| |_ __ _  | |__ __ _ _ __ ___| | ___ | |_ ___ _ __
  |  __/ _` | __| __| | |  __/ _` | '__/ __| |/ _ \| __/ _ \ '__|
  | | | (_| | |_| |_| |_| | | (_| | | | (__| | (_) | ||  __/ |
  |_|  \__,_|\__|\__,_|_|  \__,_|_|  \___|_|\___/ \__\___|_|
EOF
  printf '%s   One-command installer — PostgreSQL / SQLite / SQL Server%s\n\n' "$C_RESET$C_DIM" "$C_RESET"
}

command_exists() { command -v "$1" >/dev/null 2>&1; }

# ---------- prereqs ----------
need_cmd() {
  if ! command_exists "$1"; then
    err "Required command '$1' not found."
    case "$1" in
      git)
        info "Install git first:"
        info "  Debian/Ubuntu : sudo apt-get install -y git"
        info "  Fedora        : sudo dnf install -y git"
        info "  macOS         : xcode-select --install"
        ;;
    esac
    exit 1
  fi
}

clone_or_update() {
  step "Source code"
  if [ -d "$DIR/.git" ]; then
    info "Existing clone found at $DIR — updating..."
    git -C "$DIR" fetch --all --prune >/dev/null 2>&1 || warn "fetch failed (offline?) — using local copy"
    git -C "$DIR" pull --ff-only >/dev/null 2>&1 || warn "pull failed — using local copy"
    ok "Repository updated: $DIR"
  else
    info "Cloning $REPO ..."
    git clone --depth 1 "$REPO" "$DIR"
    ok "Cloned to: $DIR"
  fi
}

# ---------- Docker path ----------

# Best-effort automatic installation of Docker Engine + Compose plugin.
# Only touches apt-based Linux. On other systems we fall back to instructions.
docker_autoinstall() {
  step "Docker environment"
  if command_exists docker && docker info >/dev/null 2>&1; then
    ok "Docker already installed and running"
    return 0
  fi

  if [ "$(uname)" != "Linux" ]; then
    err "Docker is not installed."
    info "Install Docker:"
    info "  Linux  : https://docs.docker.com/engine/install/"
    info "  macOS  : brew install --cask docker"
    info "  Windows: Docker Desktop (https://www.docker.com/products/docker-desktop)"
    exit 1
  fi

  if ! command_exists apt-get; then
    err "Docker is not installed and this Linux distro is not apt-based."
    info "Install Docker manually: https://docs.docker.com/engine/install/"
    exit 1
  fi

  warn "Docker is missing. Attempting automatic installation (requires sudo)..."

  # 'curl | bash' one-liner works on Debian/Ubuntu/RHEL/CentOS and is idempotent.
  if command_exists curl; then
    if sh -c 'curl -fsSL https://get.docker.com -o /tmp/get-docker.sh && sh /tmp/get-docker.sh'; then
      ok "Docker Engine installed"
    else
      err "Automatic Docker installation failed."
      info "Install it manually: https://docs.docker.com/engine/install/"
      exit 1
    fi
  else
    err "curl is required for the automatic Docker install."
    exit 1
  fi

  # Start the daemon now if it isn't running.
  if ! docker info >/dev/null 2>&1; then
    if command_exists systemctl; then
      sudo systemctl enable --now docker 2>/dev/null || sudo systemctl start docker 2>/dev/null || true
    elif command_exists service; then
      sudo service docker start 2>/dev/null || true
    fi
  fi

  # Add the current user to the docker group so no sudo is needed afterwards.
  if getent group docker >/dev/null 2>&1; then
    sudo usermod -aG docker "$USER" 2>/dev/null || true
    warn "Added '$USER' to the 'docker' group. You may need to log out/in once, or run: newgrp docker"
  fi

  if ! docker info >/dev/null 2>&1; then
    err "Docker daemon still not reachable after install."
    info "Start Docker, then re-run this script."
    exit 1
  fi
  ok "Docker is up"
}

docker_install() {
  docker_autoinstall

  if docker compose version >/dev/null 2>&1; then
    COMPOSE="docker compose"
  elif command_exists docker-compose; then
    COMPOSE="docker-compose"
  else
    warn "Docker Compose not found. Installing the compose plugin..."
    if command_exists apt-get; then
      sudo apt-get update -qq && sudo apt-get install -y docker-compose-plugin
    fi
    if docker compose version >/dev/null 2>&1; then
      COMPOSE="docker compose"
    else
      err "Docker Compose still not available. Install the 'docker-compose-plugin'."
      exit 1
    fi
  fi
  ok "Docker + Compose ready"

  cd "$DIR"

  step "Configuration (.env)"
  if [ ! -f .env ]; then
    cp .env.example .env
    # Generate a strong database password automatically
    if command_exists openssl; then
      DBPASS=$(openssl rand -base64 18 | tr -d '/+=' | head -c 24)
    else
      DBPASS=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n' | head -c 24)
    fi
    if grep -q '^POSTGRES_PASSWORD=' .env; then
      sed -i.bak "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${DBPASS}|" .env && rm -f .env.bak
    else
      printf '\nPOSTGRES_PASSWORD=%s\n' "$DBPASS" >> .env
    fi
    ok ".env created with an auto-generated database password"
    warn "Edit $DIR/.env later to add your TELEGRAM_BOT_TOKEN (optional — the app runs without it)"
  else
    ok ".env already exists — kept as-is"
  fi

  step "Building & starting containers (PostgreSQL, Redis, API)"
  info "First build takes a few minutes; later runs use the cache."
  $COMPOSE up -d --build

  step "Waiting for health check"
  local url="$HEALTH_URL_DOCKER"
  local tries=60
  local up=0
  for _ in $(seq 1 "$tries"); do
    if command_exists curl; then
      curl -fsS -m 3 "$url" >/dev/null 2>&1 && { up=1; break; }
    else
      wget -qO- -T 3 "$url" >/dev/null 2>&1 && { up=1; break; }
    fi
    sleep 3
  done

  echo
  if [ "$up" = "1" ]; then
    ok "API is UP and healthy → $url"

    # Headless Docker bootstrap creates the admin password inside the
    # encrypted vault and exposes it through a one-time local file.
    local bootstrap
    bootstrap="$($COMPOSE exec -T forex-trading-bot-app sh -lc 'if [ -f /app/data/vault/bootstrap/admin-password.txt ]; then cat /app/data/vault/bootstrap/admin-password.txt; rm -f /app/data/vault/bootstrap/admin-password.txt; fi' 2>/dev/null || true)"
    if [ -n "$bootstrap" ]; then
      printf '\n%sInitial admin account%s\n' "$C_BOLD" "$C_RESET"
      info "Username: admin"
      printf '    Password: %s\n' "$C_YELLOW$bootstrap$C_RESET"
      info "The bootstrap password was removed from the container after display."
    fi

    $COMPOSE ps
    cat <<EOF

${C_BOLD}Next steps${C_RESET}
  Logs      : $COMPOSE logs -f forex-trading-bot-app
  Stop      : $COMPOSE down
  Restart   : $COMPOSE up -d
  Config    : $DIR/.env
EOF
  else
    err "API did not become healthy within the timeout."
    info "Inspect with: $COMPOSE logs forex-trading-bot-app"
    exit 1
  fi
}

# ---------- Normal (native .NET) path ----------
native_install() {
  step ".NET SDK"
  local have=""
  if command_exists dotnet; then
    have=$(dotnet --list-sdks 2>/dev/null | awk -F. -v m="$MIN_DOTNET_MAJOR" '$1==m {print $1; exit}')
  fi
  if [ -z "$have" ]; then
    warn ".NET SDK $MIN_DOTNET_MAJOR not found — installing to \$HOME/.dotnet"
    local inst="$HOME/.dotnet-install.sh"
    if command_exists curl; then
      curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$inst"
    else
      wget -qO "$inst" https://dot.net/v1/dotnet-install.sh
    fi
    bash "$inst" --channel "$MIN_DOTNET_MAJOR" --install-dir "$HOME/.dotnet"
    rm -f "$inst"
    ok ".NET $MIN_DOTNET_MAJOR SDK installed"
  else
    ok ".NET SDK $MIN_DOTNET_MAJOR found"
  fi

  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$PATH"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_NOLOGO=1

  cd "$DIR"

  step "Building (Release)"
  dotnet build -c Release --nologo
  ok "Build succeeded"

  step "Starting the application"
  cat <<EOF

${C_BOLD}┌──────────────────────────────────────────────────────────┐${C_RESET}
${C_BOLD}│  First startup — choose your database                    │${C_RESET}
${C_BOLD}│                                                          │${C_RESET}
${C_BOLD}│  The app will now ask:                                   │${C_RESET}
${C_BOLD}│    1) SQLite       (zero setup, single file)             │${C_RESET}
${C_BOLD}│    2) PostgreSQL   (server, recommended for production)  │${C_RESET}
${C_BOLD}│    3) SQL Server                                 │${C_RESET}
${C_BOLD}│                                                          │${C_RESET}
${C_BOLD}│  Your choice is saved by the Easy Setup wizard          │${C_RESET}
${C_BOLD}└──────────────────────────────────────────────────────────┘${C_RESET}

EOF

  info "Starting in the foreground — press Ctrl+C to stop."
  info "Health check: $HEALTH_URL_NATIVE"
  echo
  exec dotnet run --project WebAPI -c Release
}

# ---------- main ----------
main() {
  banner
  need_cmd git

  clone_or_update

  local mode="${1:-}"
  if [ -z "$mode" ]; then
    printf '%sHow do you want to install?%s\n' "$C_BOLD" "$C_RESET"
    printf '  %s1%s) Docker        %s— PostgreSQL + Redis + API, fully automated (recommended)%s\n' "$C_GREEN" "$C_RESET" "$C_DIM" "$C_RESET"
    printf '  %s2%s) Normal        %s— .NET on this machine, first startup asks for the database%s\n' "$C_CYAN" "$C_RESET" "$C_DIM" "$C_RESET"
    printf '  %s3%s) Exit%s\n' "$C_DIM" "$C_RESET" ""
    printf 'Choice [1]: '
    read -r mode || mode=""
    mode="${mode:-1}"
  fi

  case "$mode" in
    1|d|D|docker|Docker) docker_install ;;
    2|n|N|native|Normal) native_install ;;
    *) info "Bye."; exit 0 ;;
  esac
}

main "$@"
