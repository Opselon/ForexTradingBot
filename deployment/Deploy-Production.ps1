# ====================================================================================
# THE ULTIMATE DEPLOYMENT & DEBUGGING SCRIPT
# This script logs every single action to a transcript file, leaving no room for doubt.
# It is designed to be executed remotely by the GitHub Actions workflow.
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

# --- Define Core Paths & Configuration ---
$DeployPath = 'C:\Apps\ForexTradingBot'
$TempPath   = 'C:\Apps\Temp'
$LogPath    = Join-Path $TempPath "Deployment-Log-$(Get-Date -f yyyy-MM-dd_HH-mm-ss).txt"
$AppName    = "WebAPI"
$ExeName    = "WebAPI.exe"
$ZipFile    = Join-Path $TempPath "release.zip"
$Launcher   = Join-Path $DeployPath "start-app.bat"

# --- CRITICAL: Start a detailed log of everything this script does ---
Start-Transcript -Path $LogPath -Append

# Stop script immediately if any command fails.
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT STARTED: Logging all actions to $LogPath ---"

try {
    # --- Step 1: Stop any existing process ---
    Write-Host "[1/6] Stopping running process: $AppName..."
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($process) { 
        Stop-Process -Name $AppName -Force -Verbose
        Write-Host "✅ Process stopped." 
    } else { 
        Write-Host "🟡 Process was not running." 
    }

    # --- Step 2: The UNDENIABLE Directory Cleanup ---
    Write-Host "[2/6] Starting cleanup of deployment directory: $DeployPath..."
    if (Test-Path $DeployPath) {
        # This will list every single file and folder it deletes. No more guessing.
        Get-ChildItem -Path $DeployPath -Exclude 'Session' | Remove-Item -Recurse -Force -Verbose
        Write-Host "✅ Directory cleanup complete."
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
        Write-Host "🟡 Directory did not exist; created a new one."
    }

    # --- Step 3: Unpack the new release ---
    Write-Host "[3/6] Unpacking new release from: $ZipFile..."
    Expand-Archive -Path $ZipFile -DestinationPath $DeployPath -Force -Verbose
    Write-Host "✅ Archive unpacked."

    # --- Step 4: Create the launcher batch file ---
    Write-Host "[4/6] Creating application launcher: $Launcher..."
    $BatContent = @"
@echo off
set ASPNETCORE_ENVIRONMENT=Production
set ConnectionStrings__DefaultConnection=$ConnectionString
set DatabaseSettings__DatabaseProvider=SqlServer
set TelegramPanel__BotToken=$TelegramBotToken
set TelegramUserApi__ApiId=$TelegramApiId
set TelegramUserApi__ApiHash=$TelegramApiHash
set TelegramUserApi__PhoneNumber=$TelegramPhoneNumber
set CryptoPay__ApiToken=$CryptoPayApiToken
cd /d "$DeployPath"
start "" "$ExeName"
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Launcher created."

    # --- Step 5: Execute the launcher ---
    Write-Host "[5/6] Executing launcher to start application..."
    Invoke-Expression -Command "cmd.exe /c $Launcher"
    Write-Host "✅ Launcher executed."
    
    # --- Step 6: Verify process is running ---
    Write-Host "[6/6] Waiting 5 seconds and verifying process..."
    Start-Sleep -Seconds 5
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        # This is not a fatal error for the script, just a warning.
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. The application likely crashed on startup."
    } else {
        Write-Host "✅ SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."
    }

    Write-Host "--- 🎉 DEPLOYMENT SCRIPT FINISHED ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ DEPLOYMENT SCRIPT FAILED! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    # Exit with a non-zero code to fail the GitHub Actions job
    exit 1
} finally {
    # --- Final Cleanup of temporary files ---
    Write-Host "[CLEANUP] Removing temporary release archive..."
    if (Test-Path $ZipFile) { Remove-Item -Path $ZipFile -Force -ErrorAction SilentlyContinue }
    
    # --- CRITICAL: Stop logging to save the log file ---
    Write-Host "--- SCRIPT FINISHED: Log saved to $LogPath ---"
    Stop-Transcript
}