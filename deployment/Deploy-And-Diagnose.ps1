# ====================================================================================
# THE ULTIMATE DEPLOYMENT & DIAGNOSTIC SCRIPT
# This script logs every single action to a transcript file and captures all
# application output to a separate crash log, leaving no room for doubt.
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
$DeployPath      = 'C:\Apps\ForexTradingBot'
$TempPath        = 'C:\Apps\Temp'
$ScriptLogFile   = Join-Path $TempPath "Deployment-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt" # The application's "Black Box"
$AppName         = "WebAPI"
$ExeName         = "WebAPI.exe"
$Launcher        = Join-Path $DeployPath "start-app.bat"

# --- CRITICAL: Start a detailed transcript of everything this script does ---
Start-Transcript -Path $ScriptLogFile -Append

# Stop script immediately if any command fails. This is critical for reliability.
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT STARTED: Logging all script actions to $ScriptLogFile"
Write-Host "--- Application crash logs will be captured in $AppCrashLogFile"

try {
    # --- Step 1: Stop any existing process ---
    Write-Host "[1/7] Stopping any running instance of '$AppName'..."
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($process) { 
        Stop-Process -Name $AppName -Force -Verbose
        Write-Host "✅ Process stopped successfully." 
    } else { 
        Write-Host "🟡 Process was not running." 
    }

    # --- Step 2: The UNDENIABLE Directory Cleanup ---
    Write-Host "[2/7] Starting full cleanup of deployment directory: $DeployPath..."
    Write-Host "    (The 'Session' folder will be preserved)"
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

    # --- Step 3: Copy new files from the temporary unpack location ---
    # This script assumes it's running from a temporary unpack directory.
    $ScriptCurrentFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
    Write-Host "[3/7] Copying new application files from '$ScriptCurrentFolder' to '$DeployPath'..."
    Copy-Item -Path "$ScriptCurrentFolder\*" -Destination $DeployPath -Recurse -Force -Verbose
    Write-Host "✅ New files copied successfully."

    # --- Step 4: Create the launcher batch file with full logging ---
    Write-Host "[4/7] Creating application launcher with crash logging: $Launcher..."
    # This batch file redirects all standard output (stdout) and error output (stderr) to our log file.
    $BatContent = @"
@echo off
rem This batch file sets the required environment and starts the application, capturing all output.
echo Launching at %date% %time%... > "$AppCrashLogFile"
set ASPNETCORE_ENVIRONMENT=Production >> "$AppCrashLogFile"
set ConnectionStrings__DefaultConnection=$ConnectionString >> "$AppCrashLogFile"
set DatabaseSettings__DatabaseProvider=SqlServer >> "$AppCrashLogFile"
set TelegramPanel__BotToken=$TelegramBotToken >> "$AppCrashLogFile"
set TelegramUserApi__ApiId=$TelegramApiId >> "$AppCrashLogFile"
set TelegramUserApi__ApiHash=$TelegramApiHash >> "$AppCrashLogFile"
set TelegramUserApi__PhoneNumber=$TelegramPhoneNumber >> "$AppCrashLogFile"
set CryptoPay__ApiToken=$CryptoPayApiToken >> "$AppCrashLogFile"

cd /d "$DeployPath"
rem The '2>&1' is crucial. It merges the error stream into the standard output stream.
"$ExeName" >> "$AppCrashLogFile" 2>&1
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Launcher created successfully."

    # --- Step 5: Execute the launcher ---
    Write-Host "[5/7] Executing launcher to start application..."
    # We start it as a background job so this script can continue and report status.
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$Launcher`"" -WindowStyle Hidden
    Write-Host "✅ Launcher executed in the background."
    
    # --- Step 6: Verify process is running ---
    Write-Host "[6/7] Waiting 7 seconds and verifying process..."
    Start-Sleep -Seconds 7 # A bit longer to allow for startup
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. The application has likely crashed."
        Write-Warning "The reason for the crash should be in the log file."
    } else {
        Write-Host "✅ SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."
    }

    # --- Step 7: Report findings from the crash log ---
    if (Test-Path $AppCrashLog) {
        $logContent = Get-Content $AppCrashLog
        if ($logContent) {
            Write-Host "--- Found content in App-Crash-Log.txt. This may contain the error. ---" -ForegroundColor Cyan
        } else {
            Write-Host "--- App-Crash-Log.txt was created but is empty. ---" -ForegroundColor Yellow
        }
    }

    Write-Host "--- 🎉 DEPLOYMENT SCRIPT FINISHED ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ DEPLOYMENT SCRIPT FAILED! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    exit 1
} finally {
    Write-Host "[CLEANUP] Script cleanup phase started."
    # The launcher script is kept for manual debugging on the server.
    
    # --- CRITICAL: Stop logging to save the transcript file ---
    Write-Host "--- SCRIPT FINISHED: Full transcript saved to $ScriptLogFile ---"
    Stop-Transcript
}