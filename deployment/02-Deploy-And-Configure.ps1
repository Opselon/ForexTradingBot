# ====================================================================================
# THE DEFINITIVE DEPLOY, INJECT, AND LAUNCH SCRIPT (v.Victory-Diagnostic)
# This script's SOLE purpose is to launch the application in a way that
# GUARANTEES all console output and crash exceptions are captured to a log file.
# ====================================================================================

param(
    [string]$DeployPath,
    [string]$TempPath,
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)
$ScriptLogFile = Join-Path $TempPath "02-Deploy-And-Launch-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 2: DEPLOY, INJECT, LAUNCH & CAPTURE STARTED ---" -ForegroundColor Cyan
$ZipFile         = Join-Path $TempPath "release.zip"
$AppName         = "WebAPI"
$ExeName         = "WebAPI.exe"
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt" # The application's "Black Box"

try {
    # --- STEP 2.1: UNPACK & VERIFY --- (Unchanged, it works)
    Write-Host "[2.1] Unpacking and verifying new version..."
    Expand-Archive -Path $ZipFile -DestinationPath $DeployPath -Force -Verbose
    if (-not (Test-Path (Join-Path $DeployPath $ExeName))) { throw "FATAL: $ExeName not found after unpack!" }
    Write-Host "✅ Unpack and verification successful."

    # --- STEP 2.2: INJECT SECRETS --- (Unchanged, it works)
    Write-Host "[2.2] Injecting secrets into 'appsettings.Production.json'..."
    $appSettingsPath = Join-Path $DeployPath 'appsettings.Production.json'
    if (-not (Test-Path $appSettingsPath)) { throw "FATAL: 'appsettings.Production.json' NOT FOUND after unpack!" }
    $content = Get-Content $appSettingsPath -Raw
    $content = $content -replace '#{ConnectionString}#', $ConnectionString
    $content = $content -replace '#{DatabaseProvider}#', 'SqlServer'
    $content = $content -replace '#{TelegramBotToken}#', $TelegramBotToken
    $content = $content -replace '#{TelegramApiId}#', $TelegramApiId
    $content = $content -replace '#{TelegramApiHash}#', $TelegramApiHash
    $content = $content -replace '#{TelegramPhoneNumber}#', $TelegramPhoneNumber
    $content = $content -replace '#{CryptoPayApiToken}#', $CryptoPayApiToken
    Set-Content -Path $appSettingsPath -Value $content
    Write-Host "✅ Secrets injected."
    
    # ✅✅✅✅✅ THE FINAL, CRITICAL CHANGE IS HERE ✅✅✅✅✅
    # --- STEP 2.3: LAUNCH WITH FULL OUTPUT REDIRECTION ---
    Write-Host "[2.3] Launching '$ExeName' with Production environment and CAPTURING ALL OUTPUT..."
    
    # We create a simple launcher batch file whose ONLY job is to redirect output.
    # This is the most reliable way to capture everything.
    $Launcher = Join-Path $DeployPath "start-and-log.bat"
    $BatContent = @"
@echo off
rem This batch file ensures the correct environment is set AND redirects all output to the crash log.
set ASPNETCORE_ENVIRONMENT=Production
cd /d "$DeployPath"
rem The '2>&1' is crucial. It merges the error stream into the standard output stream.
echo --- Application starting at %date% %time%... --- > "$AppCrashLogFile"
"$ExeName" >> "$AppCrashLogFile" 2>&1
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Diagnostic launcher created successfully."

    # Execute the launcher in a way that doesn't block the PowerShell script.
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$Launcher`"" -WindowStyle Hidden
    Write-Host "✅ Launch command issued."

    # --- STEP 2.4: VERIFY AND REPORT ---
    Write-Host "[2.4] Waiting 10 seconds for process to stabilize..."
    Start-Sleep -Seconds 10
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if ($runningProcess) {
        Write-Host "✅✅✅✅✅ SCRIPT 2 SUCCESS! Process '$AppName' is confirmed to be running!" -ForegroundColor Green
    } else {
        # This is not a fatal script error. We expect a crash and need the log.
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING as expected."
        Write-Warning "The reason for the crash will be reported by the next job."
    }

} catch {
    Write-Error "--- ❌ SCRIPT 2 FAILED! ---"
    Write-Error $_.Exception.ToString()
    exit 1
} finally {
    Write-Host "--- SCRIPT 2 FINISHED ---"
    Stop-Transcript
}