# ====================================================================================
# THE ULTIMATE DEPLOYMENT, LOGGING, AND VERIFICATION SCRIPT
# This script is the single source of truth. It leaves no room for doubt by
# creating a detailed transcript and capturing all application output.
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

# --- CRITICAL: Start a detailed transcript of every command and its output ---
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT STARTED: Logging all actions to $ScriptLogFile ---"
Write-Host "--- Application crash logs will be captured in $AppCrashLogFile ---"

try {
    # --- Step 1: Stop any existing process ---
    Write-Host "[1/6] Stopping running process: $AppName..."
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($process) { 
        Stop-Process -Name $AppName -Force -Verbose
        Write-Host "✅ Process stopped successfully." 
    } else { 
        Write-Host "🟡 Process was not running." 
    }

    # --- Step 2: The UNDENIABLE Directory Cleanup ---
    Write-Host "[2/6] Starting FULL cleanup of deployment directory: $DeployPath (preserving 'Session')..."
    if (Test-Path $DeployPath) {
        Get-ChildItem -Path $DeployPath -Exclude 'Session' | ForEach-Object { 
            Write-Host "  - Deleting: $($_.FullName)" -ForegroundColor Yellow
            Remove-Item -Recurse -Force -Path $_.FullName -Verbose
        }
        Write-Host "✅ Directory cleanup complete."
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
        Write-Host "🟡 Deployment directory did not exist; created a new one."
    }

    # --- Step 3: Copy new files from temporary unpack location ---
    $ScriptCurrentFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
    Write-Host "[3/6] Copying new application files from '$ScriptCurrentFolder' to '$DeployPath'..."
    Copy-Item -Path "$ScriptCurrentFolder\*" -Destination $DeployPath -Recurse -Force -Verbose
    Write-Host "✅ New files copied successfully."

    # --- Step 4: Create the launcher batch file ---
    Write-Host "[4/6] Creating application launcher with crash logging: $Launcher..."
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
rem The '2>&1' is crucial. It merges the error stream into the standard output stream.
"$ExeName" > "$AppCrashLogFile" 2>&1
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Launcher created successfully."

    # --- Step 5: Execute the launcher ---
    Write-Host "[5/6] Executing launcher to start application in the background..."
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$Launcher`"" -WindowStyle Hidden
    Write-Host "✅ Launcher executed."
    
    # --- Step 6: Final verification after a short wait ---
    Write-Host "[6/6] Waiting 7 seconds and verifying process health..."
    Start-Sleep -Seconds 7
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($runningProcess) {
        Write-Host "✅ SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."
    } else {
        # This is not a fatal script error, but a deployment failure state we need to report.
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. The application has likely crashed."
    }

    Write-Host "--- 🎉 DEPLOYMENT SCRIPT PHASE FINISHED ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ DEPLOYMENT SCRIPT FAILED MID-EXECUTION! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    # This exit code will fail the GitHub Actions job.
    exit 1
} finally {
    Write-Host "[CLEANUP] Script cleanup phase started."
    # --- CRITICAL: Stop logging to save the transcript file ---
    Write-Host "--- SCRIPT FINISHED: Full transcript saved to $ScriptLogFile ---"
    Stop-Transcript
}