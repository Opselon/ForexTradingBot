# ... (تمام بخش‌های قبلی jobs.test و jobs.build بدون تغییر باقی می‌مانند) ...
# ... (مطمئن شوید که job build شما outputs.primary_image_tag را به درستی تعریف می‌کند) ...

  #------------------------------------------------------------------------------------
  # JOB 3: Deploy - Deploy application to the server
  #------------------------------------------------------------------------------------
  deploy:
    name: "🚀 Deploy Application to Production"
    needs: build # Depends on successful completion of the build job
    # Run only on push to specified branches
    if: github.event_name == 'push' && (github.ref == 'refs/heads/master' || github.ref == 'refs/heads/ForexSignal-Performance')
    runs-on: ubuntu-latest
    environment: production # Use GitHub Environment "production" for secrets and deployment protection rules

    steps:
      - name: "🔑 Setup SSH Agent (for server access)"
        uses: webfactory/ssh-agent@v0.9.0
        with:
          ssh-private-key: ${{ secrets.SERVER_SSH_KEY }} # SSH private key stored as a GitHub secret

      - name: "🚀 Deploy to Server via SSH using deploy.sh"
        uses: appleboy/ssh-action@v1.0.3
        with:
          host: ${{ secrets.SERVER_HOST }}
          username: ${{ secrets.SERVER_USERNAME }}
          key: ${{ secrets.SERVER_SSH_KEY }}
          port: ${{ secrets.SERVER_PORT || 22 }} # Default SSH port is 22
          timeout: 60s # Connection timeout
          command_timeout: 20m # Max time for the entire script execution, increased for safety

          # Pass secrets and other necessary info as environment variables to deploy.sh on the server
          # The deploy.sh script will access these as $SECRET_DB_PASSWORD, $DEPLOY_IMAGE_TAG, etc.
          # نام پارامتر صحیح 'envs' است
          envs: |
            SECRET_DB_PASSWORD=${{ secrets.DB_PASSWORD }}
            SECRET_TELEGRAM_BOT_TOKEN=${{ secrets.TELEGRAM_BOT_TOKEN }}
            SECRET_TELEGRAM_API_ID=${{ secrets.TELEGRAM_API_ID }}
            SECRET_TELEGRAM_API_HASH=${{ secrets.TELEGRAM_API_HASH }}
            SECRET_LOG_MONITOR_TELEGRAM_CHANNEL_ID=${{ secrets.LOG_MONITOR_TELEGRAM_CHANNEL_ID }}
            SECRET_CRYPTOPAY_API_TOKEN=${{ secrets.CRYPTOPAY_API_TOKEN }}
            SECRET_CRYPTOPAY_API_KEY=${{ secrets.CRYPTOPAY_API_KEY }}
            SECRET_CRYPTOPAY_WEBHOOK_SECRET=${{ secrets.CRYPTOPAY_WEBHOOK_SECRET }}
            GIT_BRANCH_NAME=${{ github.ref_name }}
            # The primary_image_tag from the 'build' job output (e.g., short SHA or semver)
            DEPLOY_IMAGE_TAG=${{ needs.build.outputs.primary_image_tag }}
            REGISTRY_GHCR=${{ env.REGISTRY }} # Pass registry for deploy.sh to construct full image names
            IMAGE_NAME_BASE=${{ env.IMAGE_NAME }} # Pass base image name

          script: |
            set -euo pipefail # Exit immediately if a command exits with a non-zero status

            DEPLOY_TARGET_DIR="/var/lib/forex-trading-bot/ForexTradingBot" # Your deployment directory on the server
            DEPLOY_SCRIPT_NAME="deploy.sh" # Name of your deployment script in the repo root
            DEPLOY_SCRIPT_FULL_PATH="$DEPLOY_TARGET_DIR/$DEPLOY_SCRIPT_NAME"

            echo ">>> Navigating to deployment directory: $DEPLOY_TARGET_DIR"
            # Create directory if it doesn't exist (e.g., for first-time deployment)
            mkdir -p "$DEPLOY_TARGET_DIR"
            cd "$DEPLOY_TARGET_DIR"

            echo ">>> Updating deployment script and configurations from Git branch: $GIT_BRANCH_NAME"
            # Ensure Git is initialized if this is a fresh directory or first-time setup
            if [ ! -d ".git" ]; then
              echo "INFO: .git directory not found. Initializing Git repository in $DEPLOY_TARGET_DIR..."
              git init
              # Configure remote if it's not already set (adjust URL as needed)
              # You might need to handle authentication for git remote add if private repo
              if ! git remote -v | grep -q origin; then
                # Using format for GITHUB_SERVER_URL and GITHUB_REPOSITORY which are available to actions
                git remote add origin "${{ github.server_url }}/${{ github.repository }}.git"
              fi
            fi
            
            # Fetch the specific branch and reset hard to ensure clean state
            # Using --depth 1 for faster fetch if full history isn't needed for deploy script
            echo "Fetching from origin, branch: $GIT_BRANCH_NAME" # GIT_BRANCH_NAME should now be set
            git fetch origin "$GIT_BRANCH_NAME" --depth 1 
            echo "Resetting to origin/$GIT_BRANCH_NAME"
            git reset --hard "origin/$GIT_BRANCH_NAME"
            # Optional: Clean untracked files and directories. Use with caution.
            # git clean -fdx

            echo ">>> Verifying deployment script presence: $DEPLOY_SCRIPT_FULL_PATH"
            if [ ! -f "$DEPLOY_SCRIPT_FULL_PATH" ]; then
                echo "::error file=${{ github.workflow }}::Deploy script '$DEPLOY_SCRIPT_FULL_PATH' not found in $DEPLOY_TARGET_DIR."
                echo "Ensure '$DEPLOY_SCRIPT_NAME' is committed to the repository and pulled correctly."
                exit 1
            fi
            chmod +x "$DEPLOY_SCRIPT_FULL_PATH" # Ensure it's executable

            echo ">>> Executing deployment script: $DEPLOY_SCRIPT_FULL_PATH"
            # The script will use environment variables passed via 'envs' context of ssh-action
            "$DEPLOY_SCRIPT_FULL_PATH"