#!/bin/bash
#
# deploy.sh: Handles application deployment on the target server.
# ... (کامنت‌های ابتدای اسکریپت مانند قبل) ...

# --- Strict Mode & Error Handling ---
set -euo pipefail
trap 'echo "ERROR: deploy.sh failed at line $LINENO. See output above." >&2; exit 1' ERR
# ... (trap های دیگر مانند قبل) ...

# --- Configuration & Constants ---
# ... (مانند قبل) ...
readonly ENV_FILE_PATH=".env.production"
readonly DOCKER_COMPOSE_PLUGIN_DIR="/usr/libexec/docker/cli-plugins"

# --- Logging Functions ---
# ... (مانند قبل) ...
log_info() { echo "INFO: $1"; }
log_warn() { echo "WARNING: $1" >&2; }
log_error() { echo "ERROR: $1" >&2; exit 1; }
log_success() { echo "SUCCESS: $1"; }
log_debug() {
  if [[ "${DEBUG_DEPLOY:-false}" == "true" ]]; then
    echo "DEBUG: $1"
  fi
}

# --- Helper Functions ---
check_env_var() {
  local var_name="$1"
  local var_value="${!var_name:-}" # مقدار متغیر را می‌خوانیم

  log_debug "Checking env var: '$var_name', Value: '$var_value'" # چاپ مقدار برای دیباگ

  if [ -z "$var_value" ]; then # چک می‌کنیم که آیا مقدار خالی است یا خیر
    log_error "Required GHA environment variable '$var_name' is not set or is empty."
  fi
}
# ... (DOCKER_CMD و check_commands_exist مانند قبل) ...
DOCKER_CMD=""

check_commands_exist() {
    log_debug "Checking required commands..."
    log_debug "Initial PATH: $PATH"
    log_debug "User: $(whoami), Effective User: $(id -u -n)"

    if [ -d "$DOCKER_COMPOSE_PLUGIN_DIR" ]; then
        log_debug "Adding Docker Compose plugin directory to PATH: $DOCKER_COMPOSE_PLUGIN_DIR"
        export PATH="$DOCKER_COMPOSE_PLUGIN_DIR:$PATH"
        log_debug "Updated PATH: $PATH"
    else
        log_warn "Confirmed Docker Compose plugin directory '$DOCKER_COMPOSE_PLUGIN_DIR' not found. This will likely cause issues."
    fi

    if ! command -v git &> /dev/null; then log_error "Git not found."; fi
    log_debug "Git found: $(command -v git)"

    if command -v docker &> /dev/null; then
        DOCKER_CMD=$(command -v docker)
        log_debug "Docker found in PATH: $DOCKER_CMD"
    elif [ -x "/usr/bin/docker" ]; then
        DOCKER_CMD="/usr/bin/docker"
        log_debug "Docker found at /usr/bin/docker (fallback)"
    else
        log_error "Docker executable not found."
    fi

    log_debug "Attempting to verify Docker Compose plugin with: '$DOCKER_CMD compose version'"
    if "$DOCKER_CMD" compose version &> /tmp/docker_compose_test_output; then
        log_debug "SUCCESS: '$DOCKER_CMD compose version' executed. Output: $(cat /tmp/docker_compose_test_output)"
    else
        log_warn "FAILURE: '$DOCKER_CMD compose version' failed. Output/Error was:"
        cat /tmp/docker_compose_test_output >&2
        log_error "Docker Compose plugin (V2) is not working correctly even after modifying PATH."
    fi
    log_info "All command checks passed (git, docker, docker compose plugin)."
}

# --- Main Deployment Logic ---
main() {
  log_info "--- Starting Deployment Script (deploy.sh) ---"
  
  # دیباگ کردن متغیرهای محیطی که از GHA انتظار داریم
  log_debug "--- Environment Variables Received by deploy.sh ---"
  log_debug "GIT_BRANCH_NAME: '${GIT_BRANCH_NAME:-UNSET}'"
  log_debug "DEPLOY_IMAGE_TAG: '${DEPLOY_IMAGE_TAG:-UNSET}'"
  log_debug "REGISTRY_GHCR: '${REGISTRY_GHCR:-UNSET}'"
  log_debug "IMAGE_NAME_BASE: '${IMAGE_NAME_BASE:-UNSET}'"
  log_debug "SECRET_DB_CONNECTION_STRING: '${SECRET_DB_CONNECTION_STRING:-UNSET_OR_EMPTY}' (Value itself is secret)"
  log_debug "SECRET_POSTGRES_SERVICE_PASSWORD: '${SECRET_POSTGRES_SERVICE_PASSWORD:-UNSET_OR_EMPTY}' (Value is secret)"
  log_debug "SECRET_TELEGRAM_BOT_TOKEN: '${SECRET_TELEGRAM_BOT_TOKEN:-UNSET_OR_EMPTY}' (Value is secret)"
  log_debug "SECRET_TELEGRAM_API_ID: '${SECRET_TELEGRAM_API_ID:-UNSET_OR_EMPTY}' (Value is secret)"
  log_debug "SECRET_TELEGRAM_API_HASH: '${SECRET_TELEGRAM_API_HASH:-UNSET_OR_EMPTY}' (Value is secret)"
  log_debug "SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR: '${SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR:-UNSET}'"
  log_debug "SECRET_WTELEGRAMBOT: '${SECRET_WTELEGRAMBOT:-UNSET_OR_EMPTY}' (Value is secret)"
  log_debug "DEBUG_DEPLOY: '${DEBUG_DEPLOY:-false}'"
  log_debug "DB_NAME_OVERRIDE: '${DB_NAME_OVERRIDE:-UNSET}'"
  log_debug "DB_USER_OVERRIDE: '${DB_USER_OVERRIDE:-UNSET}'"
  log_debug "-----------------------------------------------------"

  # Validate required environment variables passed from GHA
  check_env_var "GIT_BRANCH_NAME"
  check_env_var "DEPLOY_IMAGE_TAG"
  check_env_var "REGISTRY_GHCR"
  check_env_var "IMAGE_NAME_BASE"
  check_env_var "SECRET_DB_CONNECTION_STRING"
  check_env_var "SECRET_POSTGRES_SERVICE_PASSWORD"
  check_env_var "SECRET_TELEGRAM_BOT_TOKEN"
  check_env_var "SECRET_TELEGRAM_API_ID" # اضافه شد
  check_env_var "SECRET_TELEGRAM_API_HASH" # اضافه شد
  check_env_var "SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR"
  # SECRET_WTELEGRAMBOT is optional, so no check_env_var for it unless required

  log_info "Deployment Target Branch: $GIT_BRANCH_NAME"
  log_info "Deployment Docker Image: ${REGISTRY_GHCR}/${IMAGE_NAME_BASE}:${DEPLOY_IMAGE_TAG}"

  # ... (بقیه اسکریپت deploy.sh مانند قبل، با اطمینان از صحت DB_HOST در بخش .env.production) ...
  # مثال برای DB_HOST در .env.production:
  # DB_HOST=forex-postgres # اگر container_name شما این است
  # یا
  # DB_HOST=db # اگر نام سرویس شما db است و از container_name برای اتصال داخلی استفاده نمی‌کنید

  log_info ">>> Performing pre-flight checks..."
  check_commands_exist

  log_info ">>> Preparing $ENV_FILE_PATH file..."
  cat > "$ENV_FILE_PATH" << EOL
# Auto-generated by deploy.sh from GHA secrets
REGISTRY=${REGISTRY_GHCR}
GITHUB_REPOSITORY=${IMAGE_NAME_BASE}
IMAGE_TAG=${DEPLOY_IMAGE_TAG}
DB_HOST=forex-postgres # مطابق با container_name در docker-compose.yml شما
DB_PORT=5432
DB_NAME=${DB_NAME_OVERRIDE:-forextrading}
DB_USER=${DB_USER_OVERRIDE:-forexuser}
DB_PASSWORD=${SECRET_POSTGRES_SERVICE_PASSWORD}
DB_CONNECTION_STRING_APP=${SECRET_DB_CONNECTION_STRING} # باید شامل Host=forex-postgres;... باشد
TELEGRAM_BOT_TOKEN=${SECRET_TELEGRAM_BOT_TOKEN}
TELEGRAM_API_ID=${SECRET_TELEGRAM_API_ID}
TELEGRAM_API_HASH=${SECRET_TELEGRAM_API_HASH}
LOG_MONITOR_TELEGRAM_CHANNEL_ID=${SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR}
$( [ -n "${SECRET_WTELEGRAMBOT:-}" ] && echo "WTELEGRAMBOT_ENV_VAR_NAME=${SECRET_WTELEGRAMBOT}" || echo "# SECRET_WTELEGRAMBOT not set or empty" )
EOL
  chmod 600 "$ENV_FILE_PATH"
  log_info "'$ENV_FILE_PATH' created/updated successfully."

  log_info ">>> Updating source code from Git branch: $GIT_BRANCH_NAME..."
  if [ "$(git rev-parse --abbrev-ref HEAD)" != "$GIT_BRANCH_NAME" ]; then
    log_info "Switching to branch '$GIT_BRANCH_NAME'..."
    if git show-ref --quiet "refs/heads/$GIT_BRANCH_NAME"; then
      git checkout "$GIT_BRANCH_NAME"
    else
      git fetch origin "$GIT_BRANCH_NAME":"$GIT_BRANCH_NAME"
      git checkout "$GIT_BRANCH_NAME"
    fi
  fi
  git fetch origin "$GIT_BRANCH_NAME" --prune
  log_info "Resetting local branch '$GIT_BRANCH_NAME' to 'origin/$GIT_BRANCH_NAME'..."
  git clean -fdx
  git reset --hard "origin/$GIT_BRANCH_NAME"
  log_success "Source code updated."

  log_info ">>> Docker Compose operations using '$DOCKER_CMD compose'..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" pull
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" down --remove-orphans --timeout 60
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" up -d --remove-orphans --force-recreate --renew-anon-volumes
  log_success "Docker services operations completed."

  log_info ">>> Waiting 30s for services to stabilize..."
  sleep 30
  log_info ">>> Current Docker container status:"
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" ps
  log_info ">>> Recent logs (last 100 lines):"
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" logs --tail 100
  log_success "--- Deployment Script Finished Successfully ---"
}

main "$@"