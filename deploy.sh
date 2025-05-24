#!/bin/bash
#
# deploy.sh: Handles application deployment on the target server.
# Fetches latest code, prepares environment, and restarts services using Docker Compose.
# ... (rest of the comments and script header) ...

# --- Strict Mode & Error Handling ---
set -euo pipefail
trap 'echo "ERROR: A command failed at line $LINENO. Output: $(tail -n 10 /tmp/deploy_error_output 2>/dev/null || echo "No error output captured")" >&2; exit 1' ERR
trap 'echo "Deployment interrupted." >&2; exit 1' SIGINT SIGTERM

# --- Configuration & Constants ---
readonly ENV_FILE_PATH=".env.production"
# We will find docker path dynamically, so no DOCKER_PATH constant for now.

# --- Logging Functions ---
# ... (logging functions remain the same) ...
log_info() {
  echo "INFO: $1"
}
log_warn() {
  echo "WARNING: $1" >&2
}
log_error() {
  echo "ERROR: $1" >&2
  # Capture last few lines of output for better error reporting in trap
  # This might not always capture the exact error, but can be helpful
  tail -n 20 > /tmp/deploy_error_output
  exit 1
}
log_success() {
  echo "SUCCESS: $1"
}
log_debug() {
  # Enable debug logging by setting DEBUG_DEPLOY=true in GHA envs if needed
  if [[ "${DEBUG_DEPLOY:-false}" == "true" ]]; then
    echo "DEBUG: $1"
  fi
}


# --- Helper Functions ---
# ... (check_env_var remains the same) ...
check_env_var() {
  local var_name="$1"
  if [ -z "${!var_name:-}" ]; then # Check if variable is unset or empty
    log_error "Required environment variable '$var_name' is not set or is empty. This should be passed from GitHub Actions."
  fi
}

# This global variable will store the found docker command
DOCKER_CMD=""

check_commands_exist() {
    log_debug "Checking required commands..."
    log_debug "Current PATH: $PATH"
    log_debug "Current user: $(whoami)"
    
    # Find Docker executable
    if command -v docker &> /dev/null; then
        DOCKER_CMD=$(command -v docker)
        log_debug "Docker found at: $DOCKER_CMD"
    else
        # Fallback to common paths if not in PATH
        if [ -x "/usr/bin/docker" ]; then
            DOCKER_CMD="/usr/bin/docker"
            log_debug "Docker found at: /usr/bin/docker (fallback)"
        elif [ -x "/usr/local/bin/docker" ]; then
            DOCKER_CMD="/usr/local/bin/docker"
            log_debug "Docker found at: /usr/local/bin/docker (fallback)"
        else
            log_error "Docker not found. Please install Docker and ensure it's in the PATH or standard locations."
        fi
    fi
    
    # Check for Docker Compose plugin
    # Try direct 'docker compose' first, as this works in your interactive shell
    if "$DOCKER_CMD" compose version &> /dev/null; then
        log_debug "Docker Compose plugin confirmed working with '$DOCKER_CMD compose version'."
    else
        log_warn "Direct '$DOCKER_CMD compose version' failed. This might indicate a PATH or installation issue for non-interactive sessions."
        log_warn "Attempting to find docker-compose plugin location..."
        # Attempt to find the plugin explicitly if the direct command fails.
        # This is more complex and often not needed if 'docker compose' is set up correctly system-wide.
        # For now, we'll rely on the direct command and error out if it fails,
        # as it works for you interactively, suggesting the issue is environment-related for the script.
        log_error "Docker Compose plugin not found or not functioning correctly with the resolved Docker command. '$DOCKER_CMD compose version' failed. Please ensure the Docker Compose plugin (V2) is correctly installed and accessible. Your interactive shell test was 'docker compose version', so ensure the non-interactive environment for this script can also find and execute it. The user '$(whoami)' might have a different PATH or environment setup for non-interactive SSH sessions."
    fi
    log_debug "Docker Compose version: $("$DOCKER_CMD" compose version)"
    
    # Check for Git
    if ! command -v git &> /dev/null; then
        log_error "Git not found. Please install Git first."
    fi
    log_debug "Git found at: $(command -v git)"
    
    log_info "All required commands found."
}

# --- Main Deployment Logic ---
main() {
  log_info "--- Starting Deployment Script ---"
  # ... (debug info and env var checks remain the same) ...
  log_debug "Script started"
  log_debug "Current directory: $(pwd)"
  log_debug "Current user: $(whoami)"
  log_debug "Effective user: $(id -u -n)" # More reliable for effective user
  log_debug "Current PATH: $PATH"
  log_debug "SHELL: $SHELL"
  log_debug "HOME: $HOME"

  # Validate required environment variables passed from GHA
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


  # DB_NAME_OVERRIDE and DB_USER_OVERRIDE are now used directly in the cat heredoc
  # No need to export DB_NAME, DB_USER, DB_PASSWORD, DB_PORT here as they are written to .env.production

  log_info "Deployment Target Branch: $GIT_BRANCH_NAME"
  log_info "Deployment Docker Image: ${REGISTRY_GHCR}/${IMAGE_NAME_BASE}:${DEPLOY_IMAGE_TAG}"

  # --- Pre-flight Checks ---
  log_info ">>> Performing pre-flight checks..."
  check_commands_exist # This will set the global DOCKER_CMD

  # --- Prepare .env.production File ---
  # ... (cat > "$ENV_FILE_PATH" heredoc remains the same as your last version) ...
  # Ensure DB_HOST is 'db' or your actual service name in docker-compose.yml
  # Your script has DB_HOST=forex-postgres, so docker-compose.yml should match.
  # If docker-compose.yml has 'db:', then DB_HOST in .env.production should be 'db'.
  # I will assume your docker-compose.yml service is named 'db' as per previous examples.
  # If it's 'forex-postgres', ensure DB_CONNECTION_STRING_APP also uses 'forex-postgres'.
  log_info ">>> Preparing $ENV_FILE_PATH file..."
  cat > "$ENV_FILE_PATH" << EOL
# This file is auto-generated by deploy.sh during deployment.
# It contains secrets and configurations for Docker Compose and applications.

# --- Docker Image Configuration (for docker-compose.yml image definitions) ---
REGISTRY=${REGISTRY_GHCR}
GITHUB_REPOSITORY=${IMAGE_NAME_BASE} # Used as image name part in docker-compose
IMAGE_TAG=${DEPLOY_IMAGE_TAG}

# --- Database Configuration ---
# For PostgreSQL Service (db service in docker-compose.yml)
DB_HOST=db                                    # <<<< ENSURE THIS MATCHES YOUR docker-compose.yml SERVICE NAME FOR POSTGRES (e.g., 'db' or 'forex-postgres')
DB_PORT=5432
DB_NAME=${DB_NAME_OVERRIDE:-forextrading}
DB_USER=${DB_USER_OVERRIDE:-forexuser}
DB_PASSWORD=${SECRET_POSTGRES_SERVICE_PASSWORD}

# For Application Services (read by your .NET applications)
DB_CONNECTION_STRING_APP=${SECRET_DB_CONNECTION_STRING} # Ensure Host in this string matches DB_HOST above (e.g., Host=db or Host=forex-postgres)

# --- Telegram Configuration (for application services) ---
TELEGRAM_BOT_TOKEN=${SECRET_TELEGRAM_BOT_TOKEN}
TELEGRAM_API_ID=${SECRET_TELEGRAM_API_ID}
TELEGRAM_API_HASH=${SECRET_TELEGRAM_API_HASH}

# --- Telegram Configuration (for log-monitor service) ---
LOG_MONITOR_TELEGRAM_CHANNEL_ID=${SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR}

# --- Optional WTELEGRAMBOT ---
$( [ -n "${SECRET_WTELEGRAMBOT:-}" ] && echo "WTELEGRAMBOT_ENV_VAR_NAME=${SECRET_WTELEGRAMBOT}" || echo "# SECRET_WTELEGRAMBOT not set or empty" )
EOL

  chmod 600 "$ENV_FILE_PATH"
  log_info "'$ENV_FILE_PATH' created/updated successfully."

  # --- Update Source Code & Configurations ---
  # ... (git operations remain the same) ...
  log_info ">>> Updating source code (including docker-compose.yml) from Git branch: $GIT_BRANCH_NAME..."
  local current_branch_on_server
  current_branch_on_server=$(git rev-parse --abbrev-ref HEAD)
  if [ "$current_branch_on_server" != "$GIT_BRANCH_NAME" ]; then
    log_info "Current branch is '$current_branch_on_server'. Switching to '$GIT_BRANCH_NAME'..."
    if git show-ref --quiet "refs/heads/$GIT_BRANCH_NAME"; then
      git checkout "$GIT_BRANCH_NAME"
    else
      log_info "Branch '$GIT_BRANCH_NAME' does not exist locally. Fetching and checking out..."
      git fetch origin "$GIT_BRANCH_NAME":"$GIT_BRANCH_NAME"
      git checkout "$GIT_BRANCH_NAME"
    fi
  fi
  log_info "Fetching latest changes from origin for branch '$GIT_BRANCH_NAME'..."
  git fetch origin "$GIT_BRANCH_NAME"
  log_info "Resetting local branch '$GIT_BRANCH_NAME' to 'origin/$GIT_BRANCH_NAME'..."
  git reset --hard "origin/$GIT_BRANCH_NAME"
  log_success "Source code updated to latest from 'origin/$GIT_BRANCH_NAME'."


  # --- Docker Compose Operations ---
  # Use the DOCKER_CMD variable found by check_commands_exist
  log_info ">>> Pulling latest Docker images..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" pull

  log_info ">>> Stopping existing containers..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" down --remove-orphans --timeout 60

  log_info ">>> Starting Docker services..."
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" up -d --remove-orphans --force-recreate --renew-anon-volumes

  log_success "Docker services started."

  # --- Post-Deployment ---
  # ... (ps and logs commands remain the same, but use $DOCKER_CMD) ...
  log_info ">>> Waiting for services to stabilize..."
  sleep 30
  log_info ">>> Current Docker container status:"
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" ps
  log_info ">>> Recent logs:"
  "$DOCKER_CMD" compose --env-file "$ENV_FILE_PATH" logs --tail 100
  log_success "--- Deployment Script Finished Successfully ---"
}

# --- Execute Main Function ---
main "$@"