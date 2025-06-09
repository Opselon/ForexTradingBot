# ====================================================================================
# FINAL DIAGNOSTIC SCRIPT
# This version's primary goal is to capture the application's console output
# to a log file, which will reveal the true reason for the startup crash.
# ====================================================================================

param(
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)

$ErrorActionPreference = 'Stop'

# --- Define Paths ---
$DeployPath = 'C:\Apps\ForexTradingBot'
$TempPath   = 'C:\Apps\Temp'
$ZipFile    = Join-Path $TempPath "release.zip"
$AppName    = "WebAPI"
$ExeName    = "WebAPI.exe"
$Launcher   = Join-Path $DeployPath "start-app.bat"
$StartupLog = Join-Path $TempPath "startup_log.txt" # The "Black Box" log file

try {
    # --- Step 1: Stop and Clean ---
    Write-Host "--- [1/5] Stopping and Cleaning ---"
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $DeployPath) {
        Get-ChildItem -Path $DeployPath -Exclude 'Session' | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
    }
    # Clean up old startup log
    if (Test-Path $StartupLog) { Remove-Item $StartupLog -Force }
    Write-Host "✅ Stop and Clean complete."

    # --- Step 2: Unpack ---
    Write-Host "--- [2/5] Unpacking new release ---"
    Expand-Archive -Path $ZipFile -DestinationPath $DeployPath -Force
    Write-Host "✅ Unpack complete."

    # --- Step 3: Create Launcher with LOGGING ---
    Write-Host "--- [3/5] Creating application launcher with logging ---"
    # This batch file now redirects all output (stdout and stderr) to our log file.
    $BatContent = @"
@echo off
set ASPNETCORE_ENVIRONMENT=Production
set ConnectionStrings__DefaultConnection=$ConnectionString
set DatabaseSettings__DatabaseProvider=SqlServer
set TelegramPanel__BotToken=$TelegramBotToken
set TelegramUserApi__ApiId=$TelegramApiId
set TelegramUserApi__ApiHash=$TelegramApiHash
set TelegramUserApi__PhoneNumber=$PhoneNumber
set CryptoPay__ApiToken=$CryptoPayApiToken

cd /d "$DeployPath"
echo Starting $ExeName at %date% %time%... >> "$StartupLog"
"$ExeName" >> "$StartupLog" 2>&1
"@
    Set-Content -Path $Launcher -Value $BatContent
    Write-Host "✅ Launcher created."

    # --- Step 4: Execute the launcher ---
    Write-Host "--- [4/5] Executing launcher ---"
    Start-Process -FilePath $Launcher
    Write-Host "✅ Launcher executed."
    
    # --- Step 5: Verify (and report) ---
    Write-Host "--- [5/5] Waiting 5 seconds and checking status ---"
    Start-Sleep -Seconds 5
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        Write-Host "--- ❌ PROCESS IS NOT RUNNING! ---" -ForegroundColor Red
        Write-Host "This is expected if the app crashed. Checking the startup log..."
        if (Test-Path $StartupLog) {
            Write-Host "--- Startup Log Content (`$StartupLog`) ---"
            Get-Content $StartupLog
            Write-Host "--- End of Log ---"
        } else {
            Write-Host "FATAL: Startup log file was not even created."
        }
        # We will not fail the workflow, so we can analyze the log.
    } else {
        Write-Host "--- ✅ SUCCESS: Process '$AppName' is confirmed to be running! ---" -ForegroundColor Green
    }

} catch {
    Write-Host "--- ❌ DEPLOYMENT SCRIPT FAILED! ---" -ForegroundColor Red
    Write-Host "Error: $($_.Exception.ToString())" -ForegroundColor Red
    exit 1
} finally {
    if (Test-Path $ZipFile) { Remove-Item -Path $ZipFile -Force -ErrorAction SilentlyContinue }
}