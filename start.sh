#!/bin/bash
set -euo pipefail # Fail fast on errors, unset variables, or pipe failures

# --- Configuration ---
# These are expected to be set as environment variables when this script is called
# Example: SECRET_DB_PASSWORD, GIT_BRANCH_NAME, DEPLOY_IMAGE_TAG
# Default values can be provided for local testing if needed
# e.g., GIT_BRANCH_NAME_DEFAULT="master"
# CURRENT_GIT_BRANCH="${GIT_BRANCH_NAME:-$GIT_BRANCH_NAME_DEFAULT}"
# CURRENT_DEPLOY_TAG="${DEPLOY_IMAGE_TAG:-latest}" # Use 'latest' if DEPLOY_IMAGE_TAG is not set

echo "--- Starting Deployment Script ---"
echo "Deployment Target Branch: $GIT_BRANCH_NAME"
echo "Deployment Docker Image Tag: $DEPLOY_IMAGE_TAG" # Ensure this is passed from GHA

# --- Helper Functions ---
check_command() {
  if ! command -v "$1" &> /dev/null; then
    echo "ERROR: Command '$1' not found. Please install it and ensure it's in the PATH."
    exit 1
  fi
}

# --- Pre-flight Checks ---
echo ">>> Performing pre-flight checks..."
check_command "git"
check_command "docker"
check_command "docker compose" # Ensures 'docker compose' (plugin V2) is available

# --- Prepare Environment ---
ENV_FILE_PATH=".env.production"

echo ">>> Preparing .env.production file at $ENV_FILE_PATH..."
if [ ! -f "$ENV_FILE_PATH" ]; then
  echo "WARNING: '$ENV_FILE_PATH' not found. Creating it with secrets from environment variables."
  
  # Create .env.production file with secrets
  # These secrets (e.g., $SECRET_DB_PASSWORD) are passed as env vars by GitHub Actions
  cat > "$ENV_FILE_PATH" << EOL
# This file is auto-generated during deployment if it doesn't exist.
# For production, it's recommended to manage this file securely outside of the script.

# --- Database Configuration ---
# Populated from GitHub Secrets via environment variables
DB_HOST=db
DB_PORT=5432
DB_NAME=forextrading
DB_USER=forexuser
DB_PASSWORD=${SECRET_DB_PASSWORD}

# --- Telegram Configuration ---
TELEGRAM_BOT_TOKEN=${SECRET_TELEGRAM_BOT_TOKEN}
TELEGRAM_API_ID=${SECRET_TELEGRAM_API_ID}
TELEGRAM_API_HASH=${SECRET_TELEGRAM_API_HASH}
TELEGRAM_CHANNEL_ID=${SECRET_LOG_MONITOR_TELEGRAM_CHANNEL_ID} # For log_monitor service

# --- Cryptopay Configuration ---
CRYPTOPAY_API_TOKEN=${SECRET_CRYPTOPAY_API_TOKEN}
CRYPTOPAY_API_KEY=${SECRET_CRYPTOPAY_API_KEY}
CRYPTOPAY_WEBHOOK_SECRET=${SECRET_CRYPTOPAY_WEBHOOK_SECRET}

# --- Application Runtime Configuration (non-secret, can be here or in Dockerfile/docker-compose.yml) ---
# These are often set via ARG/ENV in Dockerfile or directly in docker-compose.yml
# ASPNETCORE_ENVIRONMENT=Production
# DOTNET_RUNNING_IN_CONTAINER=true
# TELEGRAM_PANEL__USE_WEBHOOK=false
# TELEGRAM_PANEL__POLLING_INTERVAL=0
# TELEGRAM_PANEL__ADMIN_USER_IDS__0=5094837833
# TELEGRAM_PANEL__ENABLE_DEBUG_MODE=true
# CRYPTOPAY__BASE_URL=https://testnet-pay.crypt.bot/api/
# CRYPTOPAY__IS_TESTNET=true

# --- Docker Image Tag for Services (Important for updating services) ---
# This tells docker-compose which image version to use for your application services.
# It should match the tag pushed to the registry by the CI build job.
WEBAPI_IMAGE_TAG=${DEPLOY_IMAGE_TAG}
BACKGROUNDTASKS_IMAGE_TAG=${DEPLOY_IMAGE_TAG}
TELEGRAMPANEL_IMAGE_TAG=${DEPLOY_IMAGE_TAG}
# LOGMONITOR_IMAGE_TAG=${DEPLOY_IMAGE_TAG} # If you have a separate log monitor image
EOL

  chmod 600 "$ENV_FILE_PATH"
  echo "'$ENV_FILE_PATH' created successfully."
else
  echo "'$ENV_FILE_PATH' already exists. Will be used by docker-compose."
  # Optionally, you could update specific variables in an existing .env file
  # if needed, but simply creating if not exists is safer for this script.
  # For updating, tools like `sed` or `awk` would be needed, increasing complexity.
fi

# --- Update Source Code & Configurations (docker-compose.yml, etc.) ---
echo ">>> Updating source code and configuration files from Git branch: $GIT_BRANCH_NAME..."
# Ensure we are in the correct branch.
# The 'cd $TARGET_DIR' is done by the calling GHA script.
current_branch_on_server=$(git rev-parse --abbrev-ref HEAD)
if [ "$current_branch_on_server" != "$GIT_BRANCH_NAME" ]; then
  echo "Switching to branch '$GIT_BRANCH_NAME'..."
  git fetch origin "$GIT_BRANCH_NAME"
  git checkout "$GIT_BRANCH_NAME" || { echo "ERROR: Failed to checkout branch '$GIT_BRANCH_NAME'."; exit 1; }
fi

# Stash any local changes, pull, then try to reapply stash
# This is safer than `git reset --hard` if there are intentional local modifications.
# However, for a CI/CD managed server, `reset --hard` is often preferred.
# git stash push -u # Stash uncommitted changes and untracked files
# git pull origin "$GIT_BRANCH_NAME" --rebase
# git stash pop || echo "No stash to pop or conflicts encountered."
# For simplicity and typical CI/CD:
git fetch origin # Fetch all remote branches
git reset --hard "origin/$GIT_BRANCH_NAME" # Reset local to match remote branch exactly
git pull origin "$GIT_BRANCH_NAME" # Ensure it's up-to-date (should be redundant after reset --hard)


# --- Docker Compose Operations ---
# The docker-compose.yml should be written to use environment variables from .env.production
# for image tags and sensitive configurations.
# Example service definition in docker-compose.yml:
# services:
#   webapi:
#     image: ghcr.io/your_repo/your_image_name:${WEBAPI_IMAGE_TAG}
#     env_file:
#       - .env.production
#     secrets: # If using Docker Swarm secrets
#       - db_password_secret
# ...

# If NOT using Docker Swarm secrets, and relying purely on .env.production for env vars:
# The application code reads environment variables directly (e.g., for DB_PASSWORD).
# Dockerfile should NOT embed these secrets.

# Pull the specific Docker images defined in docker-compose.yml (which uses $DEPLOY_IMAGE_TAG)
echo ">>> Pulling latest Docker images referenced in docker-compose.yml..."
docker compose --env-file "$ENV_FILE_PATH" pull

echo ">>> Stopping and removing existing Docker containers..."
docker compose --env-file "$ENV_FILE_PATH" down --remove-orphans

# If using Docker Swarm secrets (this section needs Docker in Swarm mode):
# echo ">>> Managing Docker Swarm secrets..."
# docker secret rm db_password telegram_bot_token ... || true # Remove old secrets if they exist
# echo "$SECRET_DB_PASSWORD" | docker secret create db_password -
# echo "$SECRET_TELEGRAM_BOT_TOKEN" | docker secret create telegram_bot_token -
# ... add other secrets ...
# Make sure your docker-compose.yml's services are configured to use these swarm secrets.

echo ">>> Starting Docker services using docker-compose..."
docker compose --env-file "$ENV_FILE_PATH" up -d --remove-orphans

# --- Post-Deployment ---
echo ">>> Waiting for services to stabilize..."
sleep 20 # Increased wait time

echo ">>> Current Docker container status:"
docker compose --env-file "$ENV_FILE_PATH" ps

echo ">>> Displaying recent logs for all services (last 50 lines)..."
docker compose --env-file "$ENV_FILE_PATH" logs --tail 50

# Example: Check health of a specific service if it has a healthcheck endpoint
# echo ">>> Checking WebAPI health..."
# if docker compose exec webapi curl -f http://localhost/health; then
#   echo "WebAPI is healthy."
# else
#   echo "WARNING: WebAPI health check failed or endpoint not available."
# fi

echo "--- Deployment Script Finished Successfully ---"