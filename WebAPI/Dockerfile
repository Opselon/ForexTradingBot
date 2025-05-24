name: Test, Build, Secure, and Deploy Application

on:
  schedule:
    - cron: '16 20 * * *' # Runs at 20:16 UTC daily
  push:
    branches: [ "master", "ForexSignal-Performance" ]
    tags: [ 'v*.*.*' ] # Publish semver tags as releases.
  pull_request:
    branches: [ "master", "ForexSignal-Performance" ]

env:
  # --- Global Environment Variables ---
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}
  DOTNET_VERSION: '9.0.x' # Define .NET version for consistency
  SOLUTION_FILE_PATH: ForexTradingBot.sln # Adjust if your .sln file is named differently or not in root
  TEST_RESULTS_DIR: ./test-results # Centralized directory for TRX test logs
  COVERAGE_REPORTS_DIR: ./coverage-reports # Centralized directory for all coverage related files
  RUNSETTINGS_FILE_PATH: .runsettings # Path to your .runsettings file

  # Non-sensitive build arguments for Docker image passed via --build-arg
  DOCKER_BUILD_ARGS: |
    ARG_ASPNETCORE_ENVIRONMENT=Production
    ARG_DOTNET_RUNNING_IN_CONTAINER=true
    ARG_TELEGRAM_PANEL_USE_WEBHOOK=false
    ARG_TELEGRAM_PANEL_POLLING_INTERVAL=0
    ARG_TELEGRAM_PANEL_ADMIN_USER_IDS_0=5094837833
    ARG_TELEGRAM_PANEL_ENABLE_DEBUG_MODE=true
    ARG_CRYPTOPAY_BASE_URL=https://testnet-pay.crypt.bot/api/
    ARG_CRYPTOPAY_IS_TESTNET=true
jobs:
  #------------------------------------------------------------------------------------
  # JOB 1: Test - Run all tests and generate code coverage reports
  #------------------------------------------------------------------------------------
  test:
    name: "🧪 Run Tests & Generate Coverage"
    runs-on: ubuntu-latest
    outputs:
      coverage_percentage: ${{ steps.check_coverage.outputs.coveragePercentage || '0' }}
      test_run_successful: ${{ steps.final_test_check.outputs.success || 'false' }}

    steps:
      - name: "⬇️ Checkout Repository"
        uses: actions/checkout@v4

      - name: "⚙️ Setup .NET SDK ${{ env.DOTNET_VERSION }}"
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: "📦 Cache NuGet Packages"
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ env.DOTNET_VERSION }}-${{ hashFiles('**/Directory.Packages.props', '**/*.csproj') }}
          restore-keys: |
            ${{ runner.os }}-nuget-${{ env.DOTNET_VERSION }}-
            ${{ runner.os }}-nuget-
      - name: "🔍 List Project Structure (Debug)"
        # Run only if GITHUB_ACTOR is a specific user or on demand via workflow_dispatch input
        if: github.actor == 'your_debug_github_username' || github.event_name == 'workflow_dispatch'
        run: |
          echo "::group::Project Root Listing"
          ls -al
          echo "::endgroup::"
          echo "::group::Full Project Structure"
          tree -L 3 || ls -R . # Fallback if tree is not available
          echo "::endgroup::"
          echo "::group::Solution File Location"
          find . -name "${{ env.SOLUTION_FILE_PATH }}" -print
          echo "::endgroup::"
          echo "::group::Test Projects Found (by pattern)"
          find . -name "*.csproj" -path "*/Tests/*" -print # Adjust pattern if needed
          find . -name "*.csproj" -path "*Tests.csproj" -print # Another common pattern
          echo "::endgroup::"
          echo "::group::Runsettings File Check"
          if [ -f "${{ env.RUNSETTINGS_FILE_PATH }}" ]; then echo "Found: ${{ env.RUNSETTINGS_FILE_PATH }}"; else echo "NOT FOUND: ${{ env.RUNSETTINGS_FILE_PATH }}"; fi
          echo "::endgroup::"
      - name: "🛠️ Restore Dependencies"
        run: dotnet restore "${{ env.SOLUTION_FILE_PATH }}"

      - name: "🏗️ Build Solution (Release Configuration)"
        run: dotnet build "${{ env.SOLUTION_FILE_PATH }}" --no-restore --configuration Release

      - name: "🗂️ Create Artifact Directories"
        run: |
          mkdir -p "${{ env.TEST_RESULTS_DIR }}/application"
          mkdir -p "${{ env.TEST_RESULTS_DIR }}/integration"
          mkdir -p "${{ env.COVERAGE_REPORTS_DIR }}/raw"      # For individual cobertura files
          mkdir -p "${{ env.COVERAGE_REPORTS_DIR }}/final-report" # For combined report
      # --- Run Application Tests ---
      # Note: Assumes Tests.Application/Tests.Application.csproj is the correct path
      # And Application.Tests/Application.Tests.csproj for integration tests.
      # These should be adjusted to your actual project structure.
      - name: "🧪 Run Application Tests"
        id: app_tests_run
        env:
          # Correctly check if the runsettings file exists for conditional parameter passing
          RUNSETTINGS_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && format('--settings {0}', env.RUNSETTINGS_FILE_PATH) || '' }}
          COVERAGE_FORMAT_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && 'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura' || '' }}
          COVERAGE_FILENAME_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && format('DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.CoverageFileName=app.cobertura.xml') || '' }}
          COVERAGE_OUTPUTDIR_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && format('DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.OutputDirectory={0}/raw', env.COVERAGE_REPORTS_DIR) || '' }}
        run: |
          PROJECT_PATH="Tests.Application/Tests.Application.csproj" # ADJUST THIS PATH
          TEST_SUITE_NAME="Application"
          if [ ! -f "$PROJECT_PATH" ]; then
            echo "::warning file=${{ github.workflow }}::${TEST_SUITE_NAME} test project not found at '$PROJECT_PATH'. Skipping."
            echo "${TEST_SUITE_NAME}_TESTS_RESULT=skipped" >> $GITHUB_ENV
          else
            echo "Running ${TEST_SUITE_NAME} Tests: $PROJECT_PATH"
            if dotnet test "$PROJECT_PATH" \
              --configuration Release \
              --no-build \
              --logger "trx;LogFileName=${TEST_SUITE_NAME,,}-tests.trx" \
              --results-directory "${{ env.TEST_RESULTS_DIR }}/${TEST_SUITE_NAME,,}" \
              --collect:"XPlat Code Coverage" \
              ${RUNSETTINGS_ARG} \
              ${COVERAGE_FORMAT_ARG} \
              ${COVERAGE_FILENAME_ARG} \
              ${COVERAGE_OUTPUTDIR_ARG} \
              --blame-hang-timeout 120s; then
              echo "${TEST_SUITE_NAME}_TESTS_RESULT=success" >> $GITHUB_ENV
            else
              echo "::error file=${{ github.workflow }}::${TEST_SUITE_NAME} tests failed."
              echo "${TEST_SUITE_NAME}_TESTS_RESULT=failure" >> $GITHUB_ENV
            fi
          fi
      # --- Run Integration Tests ---
      - name: "🔩 Run Integration Tests"
        id: int_tests_run
        env: # Re-define for this step for clarity, or use global GITHUB_ENV
          RUNSETTINGS_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && format('--settings {0}', env.RUNSETTINGS_FILE_PATH) || '' }}
          COVERAGE_FORMAT_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && 'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura' || '' }}
          COVERAGE_FILENAME_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && format('DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.CoverageFileName=int.cobertura.xml') || '' }} # Different filename
          COVERAGE_OUTPUTDIR_ARG: ${{ hashFiles(env.RUNSETTINGS_FILE_PATH) != '' && format('DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.OutputDirectory={0}/raw', env.COVERAGE_REPORTS_DIR) || '' }}
        run: |
          PROJECT_PATH="Application.Tests/Application.Tests.csproj" # ADJUST THIS PATH
          TEST_SUITE_NAME="Integration"
          if [ ! -f "$PROJECT_PATH" ]; then
            echo "::warning file=${{ github.workflow }}::${TEST_SUITE_NAME} test project not found at '$PROJECT_PATH'. Skipping."
            echo "${TEST_SUITE_NAME}_TESTS_RESULT=skipped" >> $GITHUB_ENV
          else
            echo "Running ${TEST_SUITE_NAME} Tests: $PROJECT_PATH"
            if dotnet test "$PROJECT_PATH" \
              --configuration Release \
              --no-build \
              --logger "trx;LogFileName=${TEST_SUITE_NAME,,}-tests.trx" \
              --results-directory "${{ env.TEST_RESULTS_DIR }}/${TEST_SUITE_NAME,,}" \
              --collect:"XPlat Code Coverage" \
              ${RUNSETTINGS_ARG} \
              ${COVERAGE_FORMAT_ARG} \
              ${COVERAGE_FILENAME_ARG} \
              ${COVERAGE_OUTPUTDIR_ARG} \
              --blame-hang-timeout 240s; then # Longer timeout for integration tests
              echo "${TEST_SUITE_NAME}_TESTS_RESULT=success" >> $GITHUB_ENV
            else
              echo "::error file=${{ github.workflow }}::${TEST_SUITE_NAME} tests failed."
              echo "${TEST_SUITE_NAME}_TESTS_RESULT=failure" >> $GITHUB_ENV
            fi
          fi
          
      - name: "📄 Generate Combined Coverage Report"
        # Run only if .runsettings file exists, implying Cobertura output was attempted
        if: hashFiles(env.RUNSETTINGS_FILE_PATH) != ''
        run: |
          dotnet tool install --global dotnet-reportgenerator-globaltool --version 5.2.0 || true # Install specific version or use cached
          
          COBERTURA_FILES_PATTERN="${{ env.COVERAGE_REPORTS_DIR }}/raw/*.cobertura.xml"
          
          # Check if any Cobertura files were actually generated
          if ! ls $COBERTURA_FILES_PATTERN 1> /dev/null 2>&1; then
            echo "::warning file=${{ github.workflow }}::No .cobertura.xml files found in '${{ env.COVERAGE_REPORTS_DIR }}/raw'. Skipping report generation."
            echo "No Cobertura coverage data available for reporting." > "${{ env.COVERAGE_REPORTS_DIR }}/final-report/Summary.txt"
          else
            echo "Generating combined coverage report from files matching: $COBERTURA_FILES_PATTERN"
            reportgenerator \
              "-reports:$COBERTURA_FILES_PATTERN" \
              "-targetdir:${{ env.COVERAGE_REPORTS_DIR }}/final-report" \
              "-reporttypes:HtmlInline_AzurePipelines;Cobertura;TextSummary" \
              "-assemblyfilters:+${{ env.SOLUTION_FILE_PATH }};-*.Tests" # Adjust assembly filter as needed
            cat "${{ env.COVERAGE_REPORTS_DIR }}/final-report/Summary.txt" || echo "Summary.txt not found after report generation."
          fi
      - name: "📊 Check Test Coverage & Set Output"
        id: check_coverage
        run: |
          COVERAGE_PERCENTAGE=0
          SUMMARY_FILE="${{ env.COVERAGE_REPORTS_DIR }}/final-report/Summary.txt"
          if [ -f "$SUMMARY_FILE" ]; then
            # Try to extract line coverage, robustly handling variations
            LINE_COVERAGE_VALUE=$(grep -i 'Line coverage' "$SUMMARY_FILE" | sed -n 's/.*Line coverage:[^0-9]*\([0-9.]\+\)%/\1/p' | head -n 1)
            if [ -n "$LINE_COVERAGE_VALUE" ]; then
              COVERAGE_PERCENTAGE=$LINE_COVERAGE_VALUE
            else
              echo "::warning file=${{ github.workflow }}::Could not parse line coverage from $SUMMARY_FILE. Defaulting to 0."
            fi
          else
            echo "::warning file=${{ github.workflow }}::Coverage summary file $SUMMARY_FILE not found. Defaulting to 0."
          fi
          echo "Current Line Coverage: $COVERAGE_PERCENTAGE%"
          echo "coveragePercentage=$COVERAGE_PERCENTAGE" >> "$GITHUB_OUTPUT"
          
          # Optional: Fail job if coverage is below a threshold
          # MINIMUM_COVERAGE_THRESHOLD=70
          # if (( $(echo "$COVERAGE_PERCENTAGE < $MINIMUM_COVERAGE_THRESHOLD" | bc -l) )); then
          #   echo "::error file=${{ github.workflow }}::Test coverage ($COVERAGE_PERCENTAGE%) is below the required threshold of $MINIMUM_COVERAGE_THRESHOLD%."
          #   exit 1
          # fi
      - name: "📤 Upload Test & Coverage Artifacts"
        uses: actions/upload-artifact@v4
        if: always() # Always upload for debugging, even if previous steps failed
        with:
          name: test-and-coverage-artifacts-${{ github.run_id }}
          path: |
            ${{ env.TEST_RESULTS_DIR }}/**
            ${{ env.COVERAGE_REPORTS_DIR }}/**
          retention-days: 7

      - name: "🏁 Final Test Result Check"
        id: final_test_check
        run: |
          # Check results from GITHUB_ENV
          APP_RESULT="${Application_TESTS_RESULT:-skipped}" # Default to skipped if var not set
          INT_RESULT="${Integration_TESTS_RESULT:-skipped}"
          if [[ "$APP_RESULT" == "failure" || "$INT_RESULT" == "failure" ]]; then
            echo "::error file=${{ github.workflow }}::Test job failed due to test failures. AppTests: $APP_RESULT, IntTests: $INT_RESULT"
            echo "success=false" >> "$GITHUB_OUTPUT"
            exit 1
          else
            echo "Test job completed. AppTests: $APP_RESULT, IntTests: $INT_RESULT"
            echo "success=true" >> "$GITHUB_OUTPUT"
          fi
  #------------------------------------------------------------------------------------
  # JOB 2: Build - Build, sign, and push Docker image
  #------------------------------------------------------------------------------------
  build:
    name: "🏗️ Build, Sign & Push Docker Image"
    needs: test
    # Run only if tests passed AND it's a push event (not a PR)
    if: ${{ needs.test.outputs.test_run_successful == 'true' && github.event_name == 'push' }}
    runs-on: ubuntu-latest
    permissions:
      contents: read    # To checkout the repository
      packages: write  # To push to GitHub Packages (GHCR)
      id-token: write  # For OIDC token to Sigstore for image signing

    outputs:
      image_digest: ${{ steps.build-and-push.outputs.digest }}
      # 'version' output from metadata-action is usually the most specific one (e.g., semver or short SHA)
      primary_image_tag: ${{ steps.meta.outputs.version }} 

    steps:
      - name: "⬇️ Checkout Repository"
        uses: actions/checkout@v4

      - name: "🛡️ Install Cosign (for image signing)"
        # No 'if' needed here, as the job itself is conditional
        uses: sigstore/cosign-installer@v3.5.0
        with:
          cosign-release: 'v2.2.4'

      - name: "🔧 Set up Docker Buildx (for multi-platform builds if needed)"
        uses: docker/setup-buildx-action@v3.3.0

      - name: "🔑 Log in to GitHub Container Registry (GHCR)"
        uses: docker/login-action@v3.1.0
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: "ℹ️ Extract Docker Metadata (Tags and Labels)"
        id: meta
        uses: docker/metadata-action@v5.5.1
        with:
          images: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}
          tags: |
            type=schedule,pattern={{date 'YYYYMMDD-HHmmss'}} # Unique tag for scheduled builds
            type=ref,event=branch                           # Tag for branch name (e.g., master)
            type=semver,pattern={{version}}                 # Semantic versioning tags (e.g., v1.2.3)
            type=semver,pattern={{major}}.{{minor}}         # (e.g., v1.2)
            type=sha,format=short,prefix=                    # Short commit SHA (e.g., abcdef0) - no prefix for cleaner tag
            # Add 'latest' tag only for pushes to the 'master' branch
            type=raw,value=latest,enable=${{ github.ref == format('refs/heads/{0}', 'master') }}
      - name: "🏭 Build and Push Docker Image"
        id: build-and-push
        uses: docker/build-push-action@v5.3.0
        with:
          context: .
          # dockerfile: ./path/to/your/Dockerfile # Specify if not in root
          push: true # Conditional at job level
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          # Multi-stage cache for faster builds
          cache-from: |
            type=gha
            type=registry,ref=${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:buildcache 
            type=registry,ref=${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:latest # Fallback cache if available
          cache-to: type=gha,mode=max,ref=${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:buildcache # Push build cache
          build-args: ${{ env.DOCKER_BUILD_ARGS }} # Pass ARG values to Dockerfile

      - name: "✍️ Sign the Published Docker Image (Sigstore/Cosign)"
        if: steps.build-and-push.outputs.digest && github.event_name == 'push' # Ensure digest exists and it's a push event
        env:
          COSIGN_EXPERIMENTAL: "true" # For keyless signing with OIDC
          IMAGE_TAGS: ${{ steps.meta.outputs.tags }}
          IMAGE_DIGEST: ${{ steps.build-and-push.outputs.digest }} 
          IMAGE_BASE_NAME: "${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}"
        run: |
          echo "Attempting to sign image with digest: $IMAGE_DIGEST"
          echo "Base image name for signing: $IMAGE_BASE_NAME"
          echo "Available tags (will attempt to sign these if they point to the pushed digest):"
          echo "$IMAGE_TAGS"
          echo "Attempt 1: Signing the digest directly..."
          if cosign sign --yes "$IMAGE_BASE_NAME@$IMAGE_DIGEST"; then
            echo "Successfully signed image by digest: $IMAGE_BASE_NAME@$IMAGE_DIGEST"
          else
            echo "::warning::Failed to sign by digest directly. Will attempt to sign by tags."
          
            # Iterate over tags and sign each one that points to the pushed digest.
            # Cosign will resolve the tag to a digest and sign that digest.
            # If the tag already points to the signed digest, it effectively adds a signature reference for that tag.
            echo "Attempt 2: Signing each relevant tag..."
            echo "$IMAGE_TAGS" | tr ',' '\n' | while read -r tag; do
              if [ -n "$tag" ]; then
                # Construct the full image reference with the tag
                # The tag from metadata-action is already the full image name with tag
                # e.g., ghcr.io/user/repo:tag_name or ghcr.io/user/repo:sha-abcdef
                # So, no need to prepend IMAGE_BASE_NAME if tag is already full.
                # Let's check if tag already contains the registry
                if [[ "$tag" == *"$IMAGE_BASE_NAME"* ]]; then
                  IMAGE_WITH_TAG="$tag"
                else
                  IMAGE_WITH_TAG="$IMAGE_BASE_NAME:$tag" # Fallback if tag is just the tag part
                fi
                echo "Attempting to sign: $IMAGE_WITH_TAG (which should resolve to digest $IMAGE_DIGEST)"
                # We are signing the digest that the tag points to.
                # If the tag points to the same digest we pushed, this is what we want.
                if cosign sign --yes "$IMAGE_WITH_TAG"; then
                  echo "Successfully signed: $IMAGE_WITH_TAG"
                else
                  echo "::warning::Failed to sign tag: $IMAGE_WITH_TAG. It might not point to the pushed digest or another issue occurred."
                fi
              fi
            done
          fi
          echo "Image signing process completed."
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
          env: |
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
              if ! git remote -v | grep -q origin; then
                git remote add origin "${{ github.server_url }}/${{ github.repository }}.git"
              fi
            fi
            
            # Fetch the specific branch and reset hard to ensure clean state
            # Using --depth 1 for faster fetch if full history isn't needed for deploy script
            git fetch origin "$GIT_BRANCH_NAME" --depth 1 
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
            # The script will use environment variables passed via 'env' context of ssh-action