#!/bin/bash
set -euo pipefail # Fail fast on errors, unset variables, or pipe failures

# --- Configuration ---
# These are expected to be set as environment variables when this script is called by GHA
# Example: SECRET_DB_PASSWORD, GIT_BRANCH_NAME, DEPLOY_IMAGE_TAG, REGISTRY_GHCR, IMAGE_NAME_BASE
# SECRET_LOG_MONITOR_TELEGRAM_CHANNEL_ID, SECRET_WTELEGRAMBOT (optional)

echo "--- Starting Deployment Script ---"
echo "Deployment Target Branch: $GIT_BRANCH_NAME"
echo "Deployment Docker Image: ${REGISTRY_GHCR}/${IMAGE_NAME_BASE}:${DEPLOY_IMAGE_TAG}"

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
check_command "docker compose"

# --- Prepare Environment ---
ENV_FILE_PATH=".env.production" # Docker Compose V2 automatically loads .env in the current directory first, then .env.production.
                                # Explicitly using --env-file .env.production is more robust.

echo ">>> Preparing $ENV_FILE_PATH file..."

# Always overwrite .env.production to ensure it reflects the latest GHA secrets
cat > "$ENV_FILE_PATH" << EOL
# This file is auto-generated during deployment by deploy.sh.
# It contains secrets and configurations for docker-compose.

# --- Docker Image Configuration (for docker-compose.yml image definitions) ---
REGISTRY=${REGISTRY_GHCR}
GITHUB_REPOSITORY=${IMAGE_NAME_BASE} # Corresponds to 'IMAGE_NAME' in GHA
IMAGE_TAG=${DEPLOY_IMAGE_TAG}

# --- Database Configuration (for postgres service and application) ---
DB_HOST=db
DB_PORT=5432
DB_NAME=forextrading # Default, or use a GHA secret if it needs to be dynamic
DB_USER=forexuser   # Default, or use a GHA secret if it needs to be dynamic
DB_PASSWORD=${SECRET_DB_PASSWORD}

# --- Telegram Configuration (for application services) ---
TELEGRAM_BOT_TOKEN=${SECRET_TELEGRAM_BOT_TOKEN}
TELEGRAM_API_ID=${SECRET_TELEGRAM_API_ID}
TELEGRAM_API_HASH=${SECRET_TELEGRAM_API_HASH}

# --- Telegram Configuration (for log-monitor service) ---
LOG_MONITOR_TELEGRAM_CHANNEL_ID=${SECRET_LOG_MONITOR_TELEGRAM_CHANNEL_ID}

# --- Cryptopay Configuration (for application services) ---
CRYPTOPAY_API_TOKEN=${SECRET_CRYPTOPAY_API_TOKEN}
CRYPTOPAY_API_KEY=${SECRET_CRYPTOPAY_API_KEY}
CRYPTOPAY_WEBHOOK_SECRET=${SECRET_CRYPTOPAY_WEBHOOK_SECRET}

# --- Optional WTELEGRAMBOT ---
# If SECRET_WTELEGRAMBOT is set in GHA, include it. Adjust VAR_NAME as needed by your app.
$( [ -n "${SECRET_WTELEGRAMBOT:-}" ] && echo "WTELEGRAMBOT_VAR_NAME=${SECRET_WTELEGRAMBOT}" || echo "# SECRET_WTELEGRAMBOT not set or empty" )

# --- Application Runtime Configuration (non-secret, from GHA DOCKER_BUILD_ARGS or set here) ---
# These were in your GHA DOCKER_BUILD_ARGS. If they also need to be runtime env vars, add them.
# Example:
# ASPNETCORE_ENVIRONMENT=Production
# DOTNET_RUNNING_IN_CONTAINER=true
# TELEGRAM_PANEL__USE_WEBHOOK=false
# TELEGRAM_PANEL__POLLING_INTERVAL=0
# TELEGRAM_PANEL__ADMIN_USER_IDS__0=5094837833
# TELEGRAM_PANEL__ENABLE_DEBUG_MODE=true # Set to false for production usually
# CRYPTOPAY__BASE_URL=https://testnet-pay.crypt.bot/api/
# CRYPTOPAY__IS_TESTNET=true
EOL

chmod 600 "$ENV_FILE_PATH"
echo "'$ENV_FILE_PATH' created/updated successfully."

# --- Update Source Code & Configurations (docker-compose.yml, etc.) ---
echo ">>> Updating source code (including docker-compose.yml) from Git branch: $GIT_BRANCH_NAME..."
current_branch_on_server=$(git rev-parse --abbrev-ref HEAD)
if [ "$current_branch_on_server" != "$GIT_BRANCH_NAME" ]; then
  echo "Switching to branch '$GIT_BRANCH_NAME'..."
  git fetch origin "$GIT_BRANCH_NAME"
  git checkout "$GIT_BRANCH_NAME" || { echo "ERROR: Failed to checkout branch '$GIT_BRANCH_NAME'."; exit 1; }
fi

git fetch origin
git reset --hard "origin/$GIT_BRANCH_NAME"
git pull origin "$GIT_BRANCH_NAME" # Should be redundant after reset --hard, but safe

# --- Docker Compose Operations ---
echo ">>> Pulling latest Docker images specified in docker-compose.yml (using $ENV_FILE_PATH)..."
docker compose --env-file "$ENV_FILE_PATH" pull

echo ">>> Stopping and removing existing Docker containers (using $ENV_FILE_PATH)..."
docker compose --env-file "$ENV_FILE_PATH" down --remove-orphans

echo ">>> Starting Docker services using docker-compose (with $ENV_FILE_PATH)..."
docker compose --env-file "$ENV_FILE_PATH" up -d --remove-orphans

# --- Post-Deployment ---
echo ">>> Waiting for services to stabilize..."
sleep 20

echo ">>> Current Docker container status:"
docker compose --env-file "$ENV_FILE_PATH" ps

echo ">>> Displaying recent logs for all services (last 50 lines)..."
docker compose --env-file "$ENV_FILE_PATH" logs --tail 50

echo "--- Deployment Script Finished Successfully ---"