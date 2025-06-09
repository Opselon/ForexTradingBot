# ====================================================================================
# THE DEFINITIVE, FLAWLESS DEPLOYMENT & VERIFICATION SCRIPT
# Version: Final-Boss-Defeated
# This script provides MAXIMUM verbosity and transparency. It is executed from a
# temporary location and manages the entire deployment lifecycle, leaving no doubt.
# ====================================================================================

# This block receives all secrets securely from the GitHub Actions workflow.
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
$ScriptLogFile   = Join-Path $TempPath "Deployment-Transcript-$(Get-Date -f yyyyMMdd-HHmmss).txt"
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt"
$AppName         = "WebAPI"
$ExeName         = "WebAPI.exe"
$Launcher        = Join-Path $DeployPath "start-app.bat"
$SessionFolderName = "Session"

# --- CRITICAL: Start a detailed transcript of every command and its output ---
Start-Transcript -Path $ScriptLogFile -Append
# This setting ensures that any command failure will immediately stop the script.
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT STARTED: Logging ALL actions and outputs to $ScriptLogFile ---"

try {
    # --- STEP 1: STOP ANY EXISTING PROCESS ---
    Write-Host "[1/6] Searching for and stopping any running instance of '$AppName'..."
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($process) { 
        Stop-Process -Name $AppName -Force -Verbose
        Write-Host "✅ SUCCESS: Process found and terminated." 
    } else { 
        Write-Host "🟡 INFO: Process was not running." 
    }

    # --- STEP 2: THE UNDENIABLE, FULLY-LOGGED DIRECTORY CLEANUP ---
    Write-Host "[2/6] Starting FULL cleanup of final deployment directory: '$DeployPath'..."
    Write-Host "    (The '$SessionFolderName' folder will be preserved)"
    if (Test-Path $DeployPath) {
        # This will list every single file and folder it finds before deleting.
        $itemsToDelete = Get-ChildItem -Path $DeployPath -Exclude $SessionFolderName
        if ($itemsToDelete) {
            Write-Host "Found the following items to delete:" -ForegroundColor Yellow
            $itemsToDelete | ForEach-Object { Write-Host "  - $($_.Name)" }
            # Now, perform the deletion with verbose output.
            $itemsToDelete | Remove-Item -Recurse -Force -Verbose
            Write-Host "✅ SUCCESS: Directory cleanup complete."
        } else {
            Write-Host "🟡 INFO: Deployment directory exists but was empty (except for potentially 'Session')."
        }
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force -Verbose | Out-Null
        Write-Host "🟡 INFO: Deployment directory did not exist; a new one was created."
    }

    # --- STEP 3: COPY NEW APPLICATION FILES (THE CRITICAL STEP) ---
    # This logic is now flawless. It copies from the temporary unpack location.
    $UnpackFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
    Write-Host "[3/6] Copying new application files from '$UnpackFolder' to '$DeployPath'..."
    # We get all items from the unpack folder, excluding the deploy script itself.
    $filesToCopy = Get-ChildItem -Path $UnpackFolder -Exclude $MyInvocation.MyCommand.Name
    if (-not $filesToCopy) {
        throw "FATAL: No files found in the unpack directory to copy!"
    }
    Write-Host "The following new files will be copied to the destination:"
    $filesToCopy | ForEach-Object { Write-Host "  + $($_.Name)" -ForegroundColor Cyan }
    Copy-Item -Path "$UnpackFolder\*" -Destination $DeployPath -Recurse -Force -Exclude $MyInvocation.MyCommand.Name -Verbose
    Write-Host "✅ SUCCESS: New application files copied."

    # --- STEP 4: CREATE THE LAUNCHER BATCH FILE ---
    Write-Host "[4/6] Creating application launcher with crash logging: '$Launcher'..."
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
echo --- Starting Executable --- >> "$AppCrashLogFile"
"$ExeName" >> "$AppCrashLogFile" 2>&1
echo --- Executable Finished --- >> "$AppCrashLogFile"
"@
    Set-Content -Path $Launcher -Value $BatContent -Verbose
    Write-Host "✅ SUCCESS: Launcher created successfully."

    # --- STEP 5: EXECUTE THE LAUNCHER ---
    Write-Host "[5/6] Executing launcher to start application..."
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$Launcher`"" -WindowStyle Hidden
    Write-Host "✅ SUCCESS: Launcher executed."
    
    # --- STEP 6: FINAL VERIFICATION ---
    Write-Host "[6/6] Waiting 7 seconds and performing final verification..."
    Start-Sleep -Seconds 7
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        # This is a critical failure state. We will fail the deployment.
        throw "FATAL: Process '$AppName' IS NOT RUNNING after 7 seconds. The application crashed on startup. Check the crash log."
    } else {
        Write-Host "✅✅✅ ULTIMATE SUCCESS: Process '$AppName' is confirmed to be running with ID $($runningProcess.Id)."
    }

    Write-Host "--- 🎉 DEPLOYMENT SCRIPT COMPLETED SUCCESSFULLY ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ A FATAL ERROR OCCURRED DURING THE DEPLOYMENT SCRIPT! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    # This exit code will fail the GitHub Actions job.
    exit 1
} finally {
    Write-Host "[FINAL CLEANUP] Script cleanup phase has started..."
    
    # --- CRITICAL: Stop logging to save the transcript file ---
    Write-Host "--- SCRIPT FINISHED: Full deployment transcript saved to '$ScriptLogFile' ---"
    Stop-Transcript
}