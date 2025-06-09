# ====================================================================================
# THE ULTIMATE DEPLOYMENT & DEBUGGING SCRIPT (v2)
# This script is designed to be executed from a temporary location after being unpacked.
# It logs every single action to a transcript file, leaving no room for doubt.
# ====================================================================================
param(
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)

# --- Define Core Paths & Configuration ---
$DeployPath      = 'C:\Apps\ForexTradingBot'
$TempUnpackPath  = 'C:\Apps\Temp\unpack'
$LogPath         = "C:\Apps\Temp\Deployment-Log-$(Get-Date -f yyyy-MM-dd_HH-mm-ss).txt"
$AppName         = "WebAPI"
$ExeName         = "WebAPI.exe"
$Launcher        = Join-Path $DeployPath "start-app.bat"

# --- CRITICAL: Start a detailed log of everything this script does ---
Start-Transcript -Path $LogPath -Append
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
    Write-Host "[2/6] Starting cleanup of FINAL deployment directory: $DeployPath..."
    if (Test-Path $DeployPath) {
        Get-ChildItem -Path $DeployPath -Exclude 'Session' | Remove-Item -Recurse -Force -Verbose
        Write-Host "✅ Final directory cleanup complete."
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
        Write-Host "🟡 Final directory did not exist; created a new one."
    }

    # --- Step 3: Copy new files from temporary location to final destination ---
    Write-Host "[3/6] Copying new application files from $TempUnpackPath to $DeployPath..."
    # Copy all items from the temporary unpack folder to the final destination
    Copy-Item -Path "$TempUnpackPath\*" -Destination $DeployPath -Recurse -Force -Verbose
    Write-Host "✅ New files copied."

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
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. The application likely crashed on startup."
    } else {
        Write-Host "✅ SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."
    }

    Write-Host "--- 🎉 DEPLOYMENT SCRIPT FINISHED ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ DEPLOYMENT SCRIPT FAILED! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    exit 1
} finally {
    Write-Host "[CLEANUP] Removing temporary unpack folder..."
    if (Test-Path $TempUnpackPath) { Remove-Item -Path $TempUnpackPath -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "--- SCRIPT FINISHED: Log saved to $LogPath ---"
    Stop-Transcript
}