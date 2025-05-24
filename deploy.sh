#!/bin/bash
#
# deploy.sh: Handles application deployment on the target server.
# ... (comments remain the same) ...

# --- Strict Mode & Error Handling ---
set -euo pipefail
trap 'echo "ERROR: deploy.sh failed at line $LINENO. See output above." >&2; exit 1' ERR
trap 'echo "Deployment interrupted." >&2; exit 1' SIGINT SIGTERM

# --- Configuration & Constants ---
readonly ENV_FILE_PATH=".env.production"
readonly DOCKER_COMPOSE_PLUGIN_DIR="/usr/libexec/docker/cli-plugins" # Adjust if your system is different

# --- Logging Functions ---
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
  local var_value="${!var_name:-}"
  log_debug "Checking env var: '$var_name', Value present: $([ -n "$var_value" ] && echo "yes" || echo "no")"
  if [ -z "$var_value" ]; then
    log_error "Required GHA environment variable '$var_name' is not set or is empty."
  fi
}

DOCKER_CMD=""
check_commands_exist() {
    log_debug "Checking required commands..."
    log_debug "Initial PATH: $PATH"
    log_debug "User: $(whoami), Effective User: $(id -u -n)"

    # Attempt to add Docker Compose plugin dir to PATH if it exists
    # This is a common location, but might vary.
    if [ -d "$DOCKER_COMPOSE_PLUGIN_DIR" ] && [[ ":$PATH:" != *":$DOCKER_COMPOSE_PLUGIN_DIR:"* ]]; then
        log_debug "Adding Docker Compose plugin directory to PATH: $DOCKER_COMPOSE_PLUGIN_DIR"
        export PATH="$DOCKER_COMPOSE_PLUGIN_DIR:$PATH"
        log_debug "Updated PATH: $PATH"
    elif [ ! -d "$DOCKER_COMPOSE_PLUGIN_DIR" ]; then # CORRECTED: Added 'then'
        log_debug "Docker Compose plugin directory '$DOCKER_COMPOSE_PLUGIN_DIR' not found. Assuming 'docker compose' is in standard PATH."
    fi # CORRECTED: This 'fi' correctly closes the if/elif block. The extra 'fi' is removed.

    if ! command -v git &> /dev/null; then log_error "Git not found."; fi
    log_debug "Git found: $(command -v git)"

    if command -v docker &> /dev/null; then
        DOCKER_CMD=$(command -v docker)
        log_debug "Docker found in PATH: $DOCKER_CMD"
    elif [ -x "/usr/bin/docker" ]; then # Fallback for some systems
        DOCKER_CMD="/usr/bin/docker"
        log_debug "Docker found at /usr/bin/docker (fallback)"
    else
        log_error "Docker executable not found in PATH or at /usr/bin/docker."
    fi

    log_debug "Attempting to verify Docker Compose plugin with: '$DOCKER_CMD compose version'"
    local compose_test_output_file
    compose_test_output_file=$(mktemp)
    # shellcheck disable=SC2064
    trap "rm -f '$compose_test_output_file'" EXIT CLEANUP ERR SIGINT SIGTERM

    if "$DOCKER_CMD" compose version &> "$compose_test_output_file"; then
        log_debug "SUCCESS: '$DOCKER_CMD compose version' executed. Output: $(cat "$compose_test_output_file")"
    else
        log_warn "FAILURE: '$DOCKER_CMD compose version' failed. Output/Error was:"
        cat "$compose_test_output_file" >&2
        log_error "Docker Compose plugin (V2) is not working correctly. Ensure it's installed and configured for the user '$(whoami)'."
    fi
    log_info "All command checks passed (git, docker, docker compose plugin)."
}
# --- Main Deployment Logic ---
main() {
  log_info "--- Starting Deployment Script (deploy.sh) ---"
  
  log_debug "--- Environment Variables Received by deploy.sh ---"
  log_debug "GIT_BRANCH_NAME: '${GIT_BRANCH_NAME:-UNSET}'"
  log_debug "DEPLOY_IMAGE_TAG: '${DEPLOY_IMAGE_TAG:-UNSET}'"
  # ... (other debug logs for env vars as before) ...
  log_debug "SECRET_POSTGRES_SERVICE_PASSWORD present: $([ -n "${SECRET_POSTGRES_SERVICE_PASSWORD:-}" ] && echo "yes" || echo "no")"
  log_debug "-----------------------------------------------------"

  check_env_var "GIT_BRANCH_NAME"
  check_env_var "DEPLOY_IMAGE_TAG"
  check_env_var "REGISTRY_GHCR"
  check_env_var "IMAGE_NAME_BASE"
  check_env_var "SECRET_DB_CONNECTION_STRING"
  check_env_var "SECRET_POSTGRES_SERVICE_PASSWORD"
  check_env_var "SECRET_TELEGRAM_BOT_TOKEN"
  check_env_var "SECRET_TELEGRAM_API_ID"
  check_env_var "SECRET_TELEGRAM_API_HASH"
  check_env_var "SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR"

  log_info "Deployment Target Branch: $GIT_BRANCH_NAME"
  log_info "Deployment Docker Image: ${REGISTRY_GHCR}/${IMAGE_NAME_BASE}:${DEPLOY_IMAGE_TAG}"

  log_info ">>> Performing pre-flight checks..."
  check_commands_exist

  # --- Update Source Code & Configurations (docker-compose.yml, etc.) ---
  # This section now comes BEFORE creating .env.production
  log_info ">>> Updating source code from Git branch: $GIT_BRANCH_NAME..."
  local current_branch_on_server
  current_branch_on_server=$(git rev-parse --abbrev-ref HEAD)
  if [ "$current_branch_on_server" != "$GIT_BRANCH_NAME" ]; then
    log_info "Current branch is '$current_branch_on_server'. Switching to '$GIT_BRANCH_NAME'..."
    if git show-ref --quiet "refs/heads/$GIT_BRANCH_NAME"; then
      git checkout "$GIT_BRANCH_NAME"
    else
      log_info "Branch '$GIT_BRANCH_NAME' does not exist locally. Fetching and checking out..."
      git fetch origin "$GIT_BRANCH_NAME":"$GIT_BRANCH_NAME" # Fetch specific branch and create local tracking
      git checkout "$GIT_BRANCH_NAME"
    fi
  fi

  log_info "Fetching latest changes from origin for branch '$GIT_BRANCH_NAME'..."
  git fetch origin "$GIT_BRANCH_NAME" --prune # Keep --prune, remove --depth 1 if full history needed for other reasons
  log_info "Resetting local branch '$GIT_BRANCH_NAME' to 'origin/$GIT_BRANCH_NAME' and cleaning workspace..."
  git clean -fdx # Remove untracked files (like an old .env.production if it existed from a failed run)
  git reset --hard "origin/$GIT_BRANCH_NAME"
  log_success "Source code updated and workspace cleaned."

  # --- Prepare .env.production File ---
  # This section now comes AFTER git clean and git reset
  log_info ">>> Preparing $ENV_FILE_PATH file..."
  cat > "$ENV_FILE_PATH" << EOL
# Auto-generated by deploy.sh from GHA secrets
REGISTRY=${REGISTRY_GHCR}
GITHUB_REPOSITORY=${IMAGE_NAME_BASE}
IMAGE_TAG=${DEPLOY_IMAGE_TAG}

# For PostgreSQL Service (db)
DB_HOST=db # Use service name for inter-container communication
DB_PORT=5432
DB_NAME=${DB_NAME_OVERRIDE:-forextrading}
DB_USER=${DB_USER_OVERRIDE:-forexuser}
DB_PASSWORD=${SECRET_POSTGRES_SERVICE_PASSWORD}

# For Application Services
# Ensure SECRET_DB_CONNECTION_STRING uses Host=db;...
DB_CONNECTION_STRING_APP=${SECRET_DB_CONNECTION_STRING}

TELEGRAM_BOT_TOKEN=${SECRET_TELEGRAM_BOT_TOKEN}
TELEGRAM_API_ID=${SECRET_TELEGRAM_API_ID}
TELEGRAM_API_HASH=${SECRET_TELEGRAM_API_HASH}
LOG_MONITOR_TELEGRAM_CHANNEL_ID=${SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR}
$( [ -n "${SECRET_WTELEGRAMBOT:-}" ] && echo "WTELEGRAMBOT_ENV_VAR_NAME=${SECRET_WTELEGRAMBOT}" || echo "# SECRET_WTELEGRAMBOT not set or empty" )
EOL
  chmod 600 "$ENV_FILE_PATH"
  log_info "'$ENV_FILE_PATH' created successfully AFTER Git operations."

  # --- Docker Compose Operations ---
  log_info ">>> Docker Compose operations using '$DOCKER_CMD compose'..."
  log_info "Pulling images..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" pull
  log_info "Stopping and removing old containers..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" down --remove-orphans --timeout 60
  log_info "Starting new containers..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" up -d --remove-orphans --force-recreate --renew-anon-volumes
  log_success "Docker services operations completed."

  # --- Post-Deployment ---
  log_info ">>> Waiting 30s for services to stabilize..."
  sleep 30
  log_info ">>> Current Docker container status:"
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" ps
  log_info ">>> Recent logs (last 100 lines):"
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" logs --tail 100
  log_success "--- Deployment Script Finished Successfully ---"
}

# --- Execute Main Function ---
main "$@"