# ====================================================================================
# PowerShell Deployment Script for ForexTradingBot
# This script is executed on the production server.
# It handles the entire deployment lifecycle: cleanup, unpack, configure, and launch.
# ====================================================================================

# This param block receives all secrets securely from the GitHub Actions workflow.
param(
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)

# Stop script immediately if any command fails. This is critical for reliability.
$ErrorActionPreference = 'Stop'

# --- Define Paths ---
$DeployPath = 'C:\Apps\ForexTradingBot'
$TempPath   = 'C:\Apps\Temp'
$ZipFile    = Join-Path $TempPath "release.zip"
$AppName    = "WebAPI"
$ExeName    = "WebAPI.exe"
$Launcher   = Join-Path $DeployPath "start-app.bat"

try {
    # --- Step 1: Stop any existing process ---
    Write-Host "--- [1/6] Stopping running process: $AppName ---"
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($process) { 
        Stop-Process -Name $AppName -Force
        Write-Host '✅ Process stopped successfully.' 
    } else { 
        Write-Host '🟡 Process was not running, no action needed.' 
    }

    # --- Step 2: Flawless Directory Cleanup ---
    Write-Host "--- [2/6] Cleaning deployment directory: $DeployPath (while preserving 'Session') ---"
    if (Test-Path $DeployPath) {
        # This robustly removes all files and folders except for the 'Session' directory.
        Get-ChildItem -Path $DeployPath -Exclude 'Session' | ForEach-Object { 
            Write-Host "  - Removing: $($_.FullName)"
            Remove-Item -Recurse -Force -Path $_.FullName 
        }
        Write-Host "✅ Directory cleaned successfully."
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
        Write-Host "🟡 Directory did not exist, created new one."
    }

    # --- Step 3: Unpack the new release ---
    Write-Host "--- [3/6] Unpacking new release from: $ZipFile ---"
    Expand-Archive -Path $ZipFile -DestinationPath $DeployPath -Force
    Write-Host "✅ Archive unpacked successfully."

    # --- Step 4: Create the application launcher ---
    Write-Host "--- [4/6] Creating launcher script: $Launcher ---"
    # Using a Here-String for the batch content is clean and reliable.
    $BatContent = @"
@echo off
rem This batch file sets the required environment and starts the application.
set ASPNETCORE_ENVIRONMENT=Production
set ConnectionStrings__DefaultConnection=$ConnectionString
set DatabaseSettings__DatabaseProvider=SqlServer
set TelegramPanel__BotToken=$TelegramBotToken
set TelegramUserApi__ApiId=$TelegramApiId
set TelegramUserApi__ApiHash=$TelegramApiHash
set TelegramUserApi__PhoneNumber=$TelegramPhoneNumber
set CryptoPay__ApiToken=$CryptoPayApiToken

rem Start the application in a new window and exit this script.
cd /d "$DeployPath"
start "" "$ExeName"
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Launcher created successfully."

    # --- Step 5: Execute the launcher ---
    Write-Host "--- [5/6] Executing the launcher to start the application ---"
    Invoke-Expression -Command "cmd.exe /c $Launcher"
    Write-Host "✅ Launcher execution command sent."
    
    # --- Step 6: Verify process is running ---
    Write-Host "Waiting 5 seconds for the process to initialize..."
    Start-Sleep -Seconds 5
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        throw "FATAL: Process '$AppName' is NOT running after start command. Check logs on the server."
    }
    Write-Host "✅ SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."

    Write-Host "--- 🎉 DEPLOYMENT COMPLETED SUCCESSFULLY ---" -ForegroundColor Green

} catch {
    Write-Host "--- ❌ DEPLOYMENT FAILED! ---" -ForegroundColor Red
    Write-Host "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)" -ForegroundColor Red
    # Exit with a non-zero code to fail the GitHub Actions job
    exit 1
} finally {
    # --- Final Cleanup ---
    Write-Host "--- [CLEANUP] Removing temporary files... ---"
    if (Test-Path $ZipFile) { Remove-Item -Path $ZipFile -Force -ErrorAction SilentlyContinue }
    # The launcher script is kept for debugging purposes. Remove if you prefer.
    # if (Test-Path $Launcher) { Remove-Item -Path $Launcher -Force -ErrorAction SilentlyContinue }
    Write-Host "✅ Cleanup finished."
}