#!/bin/bash
#
# deploy.sh: Handles application deployment on the target server.
# Fetches latest code, prepares environment, and restarts services using Docker Compose.
#
# Requirements: git, docker, docker compose (plugin V2)
# Environment Variables expected from GitHub Actions:
# - GIT_BRANCH_NAME: The Git branch to deploy.
# - DEPLOY_IMAGE_TAG: The Docker image tag for application services.
# - REGISTRY_GHCR: The GitHub Container Registry URL (e.g., ghcr.io).
# - IMAGE_NAME_BASE: The base name of the Docker image (e.g., your-org/your-repo).
# - SECRET_DB_CONNECTION_STRING: Full connection string for the application.
# - SECRET_POSTGRES_SERVICE_PASSWORD: Password for the PostgreSQL service's primary user.
# - SECRET_TELEGRAM_BOT_TOKEN: Telegram Bot Token.
# - SECRET_TELEGRAM_API_ID: Telegram API ID.
# - SECRET_TELEGRAM_API_HASH: Telegram API Hash.
# - SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR: Channel ID for the log monitor service.
# - SECRET_WTELEGRAMBOT (Optional): Value for WTELEGRAMBOT.
#
# Note: This script will overwrite the .env.production file on each run.

# --- Strict Mode & Error Handling ---
set -euo pipefail # Fail fast on errors, unset variables, or pipe failures.
trap 'echo "ERROR: A command failed at line $LINENO. See output above for details." >&2; exit 1' ERR
trap 'echo "Deployment interrupted." >&2; exit 1' SIGINT SIGTERM

# --- Configuration & Constants ---
# Variables passed from GHA are already in the environment.
readonly ENV_FILE_PATH=".env.production"
readonly REQUIRED_COMMANDS=("git" "docker")
readonly DOCKER_COMPOSE_PATHS=(
    "/usr/libexec/docker/cli-plugins/docker-compose"
    "/usr/local/lib/docker/cli-plugins/docker-compose"
    "/usr/bin/docker-compose"
)

# --- Logging Functions ---
log_info() {
  echo "INFO: $1"
}
log_warn() {
  echo "WARNING: $1" >&2
}
log_error() {
  echo "ERROR: $1" >&2
  exit 1
}
log_success() {
  echo "SUCCESS: $1"
}
log_debug() {
  echo "DEBUG: $1"
}

# --- Helper Functions ---
check_env_var() {
  local var_name="$1"
  if [ -z "${!var_name:-}" ]; then # Check if variable is unset or empty
    log_error "Required environment variable '$var_name' is not set or is empty. This should be passed from GitHub Actions."
  fi
}

find_docker_compose() {
    log_debug "Searching for Docker Compose..."
    log_debug "Current PATH: $PATH"
    
    # First try the command directly
    if command -v docker compose &> /dev/null; then
        log_debug "Found 'docker compose' in PATH"
        return 0
    fi
    
    # Then check specific paths
    for path in "${DOCKER_COMPOSE_PATHS[@]}"; do
        if [ -f "$path" ]; then
            log_debug "Found Docker Compose at: $path"
            if [ ! -x "$path" ]; then
                log_debug "Making Docker Compose executable: $path"
                chmod +x "$path"
            fi
            export PATH="$(dirname "$path"):$PATH"
            return 0
        fi
    done
    
    # If we get here, Docker Compose wasn't found
    log_error "Docker Compose not found in PATH or standard locations"
    return 1
}

check_commands_exist() {
    log_debug "Checking required commands..."
    
    # First check for Docker Compose
    find_docker_compose
    
    # Then check other required commands
    for cmd in "${REQUIRED_COMMANDS[@]}"; do
        if ! command -v "$cmd" &> /dev/null; then
            log_error "Command '$cmd' not found. Please install it and ensure it's in the PATH."
        fi
    done
    
    log_info "All required commands found."
}

# --- Main Deployment Logic ---
main() {
  log_info "--- Starting Deployment Script ---"

  # Debug information at the start
  log_debug "Script started"
  log_debug "Current directory: $(pwd)"
  log_debug "Current user: $(whoami)"
  log_debug "Current PATH: $PATH"

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

  # Set default values for database configuration if not provided
  export DB_NAME=${DB_NAME:-forextrading}
  export DB_USER=${DB_USER:-forexuser}
  export DB_PASSWORD=${SECRET_POSTGRES_SERVICE_PASSWORD}
  export DB_PORT=${DB_PORT:-5432}

  log_info "Deployment Target Branch: $GIT_BRANCH_NAME"
  log_info "Deployment Docker Image: ${REGISTRY_GHCR}/${IMAGE_NAME_BASE}:${DEPLOY_IMAGE_TAG}"

  # --- Pre-flight Checks ---
  log_info ">>> Performing pre-flight checks..."
  check_commands_exist

  # --- Prepare .env.production File ---
  log_info ">>> Preparing $ENV_FILE_PATH file..."
  # Always overwrite .env.production to ensure it reflects the latest GHA secrets
  # Note: Ensure your docker-compose.yml is designed to use these .env variables.
  cat > "$ENV_FILE_PATH" << EOL
# This file is auto-generated by deploy.sh during deployment.
# It contains secrets and configurations for Docker Compose and applications.

# --- Docker Image Configuration (for docker-compose.yml image definitions) ---
REGISTRY=${REGISTRY_GHCR}
GITHUB_REPOSITORY=${IMAGE_NAME_BASE} # Used as image name part in docker-compose
IMAGE_TAG=${DEPLOY_IMAGE_TAG}

# --- Database Configuration ---
# For PostgreSQL Service (db service in docker-compose.yml)
DB_HOST=forex-postgres                                    # Service name for internal Docker networking
DB_PORT=5432                                  # Standard PostgreSQL port
DB_NAME=${DB_NAME_OVERRIDE:-forextrading}     # Default, can be overridden by DB_NAME_OVERRIDE from GHA envs
DB_USER=${DB_USER_OVERRIDE:-forexuser}        # Default, can be overridden by DB_USER_OVERRIDE from GHA envs
DB_PASSWORD=${SECRET_POSTGRES_SERVICE_PASSWORD} # Password for the PostgreSQL service's primary user

# For Application Services (read by your .NET applications)
# This is the full connection string your app uses.
DB_CONNECTION_STRING_APP=${SECRET_DB_CONNECTION_STRING}

# --- Telegram Configuration (for application services) ---
TELEGRAM_BOT_TOKEN=${SECRET_TELEGRAM_BOT_TOKEN}
TELEGRAM_API_ID=${SECRET_TELEGRAM_API_ID}
TELEGRAM_API_HASH=${SECRET_TELEGRAM_API_HASH}

# --- Telegram Configuration (for log-monitor service) ---
# Ensure your log-monitor application reads TELEGRAM_CHANNEL_ID
LOG_MONITOR_TELEGRAM_CHANNEL_ID=${SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR}

# --- Optional WTELEGRAMBOT ---
# If SECRET_WTELEGRAMBOT is set in GHA, include it.
# The application needs to be configured to read WTELEGRAMBOT_ENV_VAR_NAME.
$( [ -n "${SECRET_WTELEGRAMBOT:-}" ] && echo "WTELEGRAMBOT_ENV_VAR_NAME=${SECRET_WTELEGRAMBOT}" || echo "# SECRET_WTELEGRAMBOT not set or empty" )

# --- Application Runtime Configuration (Examples, non-secret) ---
# These can be set here if they are runtime configurations not baked into the image.
# If they were ARG_ in Dockerfile, they are build-time. If also needed at runtime, define them here.
# ASPNETCORE_ENVIRONMENT=Production # Often set directly in docker-compose.yml or app defaults to Production
# TELEGRAM_PANEL__ADMIN_USER_IDS__0=5094837833 # Example admin ID
EOL

  # Secure the .env file
  chmod 600 "$ENV_FILE_PATH"
  log_info "'$ENV_FILE_PATH' created/updated successfully."

  # --- Update Source Code & Configurations (docker-compose.yml, etc.) ---
  log_info ">>> Updating source code (including docker-compose.yml) from Git branch: $GIT_BRANCH_NAME..."
  
  # Check current branch and switch if necessary
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
  git fetch origin "$GIT_BRANCH_NAME"
  log_info "Resetting local branch '$GIT_BRANCH_NAME' to 'origin/$GIT_BRANCH_NAME'..."
  git reset --hard "origin/$GIT_BRANCH_NAME"
  # git pull origin "$GIT_BRANCH_NAME" # This should be redundant after reset --hard

  log_success "Source code updated to latest from 'origin/$GIT_BRANCH_NAME'."

  # --- Docker Compose Operations ---
  log_info ">>> Pulling latest Docker images..."
  docker compose --env-file "$ENV_FILE_PATH" pull

  log_info ">>> Stopping existing containers..."
  docker compose --env-file "$ENV_FILE_PATH" down --remove-orphans --timeout 60

  log_info ">>> Starting Docker services..."
  docker compose --env-file "$ENV_FILE_PATH" up -d --remove-orphans --force-recreate --renew-anon-volumes

  log_success "Docker services started."

  # --- Post-Deployment ---
  log_info ">>> Waiting for services to stabilize..."
  sleep 30

  log_info ">>> Current Docker container status:"
  docker compose --env-file "$ENV_FILE_PATH" ps

  log_info ">>> Recent logs:"
  docker compose --env-file "$ENV_FILE_PATH" logs --tail 100

  log_success "--- Deployment Script Finished Successfully ---"
}

# --- Execute Main Function ---
main "$@"