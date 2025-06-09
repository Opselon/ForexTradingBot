# ====================================================================================
# THE FINAL, FLAWLESS DEPLOYMENT SCRIPT (v.2024-FinalBoss)
# This script is executed from a temporary unpack location and manages the
# entire lifecycle, from cleanup to launch, with extensive logging.
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
$TempPath        = 'C:\Apps\Temp'
$ScriptLogFile   = Join-Path $TempPath "Deployment-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt"
$AppName         = "WebAPI"
$ExeName         = "WebAPI.exe"
$Launcher        = Join-Path $DeployPath "start-app.bat"

# --- CRITICAL: Start a detailed transcript of everything this script does ---
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT STARTED: Logging all actions to $ScriptLogFile ---"

try {
    # --- Step 1: Stop any existing process ---
    Write-Host "[1/6] Stopping any running instance of '$AppName'..."
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($process) { 
        Stop-Process -Name $AppName -Force -Verbose
        Write-Host "✅ Process stopped." 
    } else { 
        Write-Host "🟡 Process was not running." 
    }

    # --- Step 2: Full and Verbose Cleanup of the Final Destination ---
    Write-Host "[2/6] Starting full cleanup of final deployment directory: $DeployPath..."
    if (Test-Path $DeployPath) {
        Get-ChildItem -Path $DeployPath -Exclude 'Session' | ForEach-Object { 
            Write-Host "  - Deleting: $($_.FullName)" -ForegroundColor Yellow
            Remove-Item -Recurse -Force -Path $_.FullName -Verbose
        }
        Write-Host "✅ Directory cleanup complete (preserved 'Session' folder)."
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
        Write-Host "🟡 Deployment directory did not exist; created a new one."
    }

    # --- Step 3: Copy New Files from Temporary Unpack Folder ---
    $UnpackFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
    Write-Host "[3/6] Copying new application files from '$UnpackFolder' to '$DeployPath'..."
    # We get all items from the unpack folder, but we EXCLUDE the script itself from being copied.
    Get-ChildItem -Path $UnpackFolder -Exclude $MyInvocation.MyCommand.Name | ForEach-Object {
        Write-Host "  - Copying: $($_.Name)" -ForegroundColor Cyan
        Copy-Item -Path $_.FullName -Destination $DeployPath -Recurse -Force -Verbose
    }
    Write-Host "✅ New files copied successfully."

    # --- Step 4: Create the Launcher Batch File ---
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
"$ExeName" > "$AppCrashLogFile" 2>&1
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Launcher created successfully."

    # --- Step 5: Execute the Launcher ---
    Write-Host "[5/6] Executing launcher to start the application..."
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$Launcher`"" -WindowStyle Hidden
    Write-Host "✅ Launcher executed."
    
    # --- Step 6: Verify Process is Running ---
    Write-Host "[6/6] Waiting 7 seconds and verifying process health..."
    Start-Sleep -Seconds 7
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. The application has likely crashed."
    } else {
        Write-Host "✅ SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."
    }

    Write-Host "--- 🎉 DEPLOYMENT SCRIPT PHASE FINISHED ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ DEPLOYMENT SCRIPT FAILED! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    exit 1
} finally {
    Write-Host "[CLEANUP] Script cleanup phase started."
    Write-Host "--- SCRIPT FINISHED: Full transcript saved to $ScriptLogFile ---"
    Stop-Transcript
}