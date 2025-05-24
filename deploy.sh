# ... (name, on, env, test job, build job sections remain the same) ...
# ... (deploy job's `needs`, `if`, `runs-on`, `environment`, `steps[0]` (ssh-agent) remain same) ...

      - name: "🚀 Deploy to Server (Fetch & Run deploy.sh from Git)"
        uses: appleboy/ssh-action@v1.0.3
        with:
          host: ${{ secrets.SERVER_HOST }}
          username: ${{ secrets.SERVER_USERNAME }}
          key: ${{ secrets.SERVER_SSH_KEY }}
          port: ${{ secrets.SERVER_PORT || 22 }}
          timeout: 60s
          command_timeout: 25m
          # These envs are made available to the remote script (deploy.sh)
          # by appleboy/ssh-action. THIS IS THE PRIMARY MECHANISM.
          envs: |
            GIT_BRANCH_NAME=${{ github.ref_name }}
            DEPLOY_IMAGE_TAG=${{ needs.build.outputs.primary_image_tag }}
            REGISTRY_GHCR=${{ env.REGISTRY }}
            IMAGE_NAME_BASE=$(echo "${{ github.repository }}" | tr '[:upper:]' '[:lower:]')
            SECRET_DB_CONNECTION_STRING=${{ secrets.DB_CONNECTION_STRING }}
            SECRET_POSTGRES_SERVICE_PASSWORD=${{ secrets.POSTGRES_SERVICE_PASSWORD }}
            SECRET_TELEGRAM_BOT_TOKEN=${{ secrets.TELEGRAM_BOT_TOKEN }}
            SECRET_TELEGRAM_API_ID=${{ secrets.TELEGRAM_API_ID }}
            SECRET_TELEGRAM_API_HASH=${{ secrets.TELEGRAM_API_HASH }}
            SECRET_TELEGRAM_CHANNEL_ID_FOR_LOG_MONITOR=${{ secrets.TELEGRAM_CHANNEL_ID }}
            SECRET_WTELEGRAMBOT=${{ secrets.WTELEGRAMBOT }}
            DEBUG_DEPLOY=true
          script: |
            set -euo pipefail
            
            TARGET_BRANCH_NAME="${{ github.ref_name }}" # Use GHA context directly for safety
            DEPLOY_TARGET_DIR="/var/lib/forex-trading-bot/ForexTradingBot"
            DEPLOY_SCRIPT_NAME="deploy.sh" # Must match name in repo
            DEPLOY_SCRIPT_FULL_PATH="$DEPLOY_TARGET_DIR/$DEPLOY_SCRIPT_NAME"

            echo "--- GHA Script Block Started ---"
            echo "Deployment Target Directory: $DEPLOY_TARGET_DIR"
            echo "Target Git Branch: $TARGET_BRANCH_NAME"
            
            mkdir -p "$DEPLOY_TARGET_DIR"
            cd "$DEPLOY_TARGET_DIR"
            
            echo "Current user: $(whoami)"
            echo "Attempting to print environment variables visible to this script block:"
            echo "Value of GIT_BRANCH_NAME from envs (should be set by appleboy/ssh-action): [$GIT_BRANCH_NAME]"
            echo "Value of DEBUG_DEPLOY from envs (should be set by appleboy/ssh-action): [$DEBUG_DEPLOY]"
            printenv | grep -E 'GIT_BRANCH_NAME|DEPLOY_IMAGE_TAG|DEBUG_DEPLOY' || echo "No matching env vars found by printenv."

            # Git Operations
            if [ ! -d ".git" ]; then
              echo "Initializing Git repository for branch $TARGET_BRANCH_NAME..."
              git init -b "$TARGET_BRANCH_NAME"
              git remote add origin "https://github.com/Opselon/ForexTradingBot.git"
            else
              echo "Ensuring remote 'origin' URL is correct..."
              git remote set-url origin "https://github.com/Opselon/ForexTradingBot.git"
              echo "Pruning stale remote branches from 'origin'..."
              git remote prune origin || echo "Warning: 'git remote prune origin' continuing..."
            fi
            
            echo "Fetching branch '$TARGET_BRANCH_NAME' from origin..."
            git fetch origin "$TARGET_BRANCH_NAME" --depth 1 --prune
            
            echo "Checking out branch '$TARGET_BRANCH_NAME'..."
            git checkout "$TARGET_BRANCH_NAME"
            
            echo "Resetting local branch '$TARGET_BRANCH_NAME' to 'origin/$TARGET_BRANCH_NAME'..."
            git reset --hard "origin/$TARGET_BRANCH_NAME"
            
            echo "Cleaning the working directory..."
            git clean -fdx

            # Verify and execute deploy.sh
            if [ ! -f "$DEPLOY_SCRIPT_FULL_PATH" ]; then
                echo "::error::Deploy script '$DEPLOY_SCRIPT_FULL_PATH' not found after Git ops."
                exit 1
            fi
            chmod +x "$DEPLOY_SCRIPT_FULL_PATH"
            
            echo "Executing $DEPLOY_SCRIPT_FULL_PATH..."
            # This is where deploy.sh runs. It should inherit envs from appleboy/ssh-action.
            "$DEPLOY_SCRIPT_FULL_PATH"
            echo "--- GHA Script Block Finished ---"