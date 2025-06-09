# ====================================================================================
# THE DEFINITIVE DEPLOY, INJECT, AND LAUNCH SCRIPT (v.Victory)
# This single script handles the entire core deployment logic after cleanup.
# It unpacks, verifies, injects secrets into the JSON file, and launches the app.
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

Write-Host "--- SCRIPT 2: DEPLOY, INJECT, LAUNCH STARTED ---" -ForegroundColor Cyan
$ZipFile = Join-Path $TempPath "release.zip"
$AppName = "WebAPI"
$ExeName = "WebAPI.exe"

try {
    # --- STEP 2.1: UNPACK NEW VERSION ---
    Write-Host "[2.1] Unpacking new version from '$ZipFile' to '$DeployPath'..."
    Expand-Archive -Path $ZipFile -DestinationPath $DeployPath -Force -Verbose
    if (-not (Test-Path (Join-Path $DeployPath $ExeName))) { throw "FATAL: $ExeName not found after unpack!" }
    Write-Host "✅ Unpack and verification successful."

    # --- STEP 2.2: DIRECT INJECTION INTO appsettings.Production.json ---
    Write-Host "[2.2] Injecting secrets DIRECTLY into 'appsettings.Production.json'..."
    $appSettingsPath = Join-Path $DeployPath 'appsettings.Production.json'
    if (-not (Test-Path $appSettingsPath)) { throw "FATAL: 'appsettings.Production.json' NOT FOUND after unpack!" }
    
    # Read the content, perform all replacements, and write it back once.
    $content = Get-Content $appSettingsPath -Raw
    $content = $content -replace '#{ConnectionString}#', $ConnectionString
    $content = $content -replace '#{DatabaseProvider}#', 'SqlServer' # Based on your JSON file
    $content = $content -replace '#{TelegramBotToken}#', $TelegramBotToken
    $content = $content -replace '#{TelegramApiId}#', $TelegramApiId
    $content = $content -replace '#{TelegramApiHash}#', $TelegramApiHash
    $content = $content -replace '#{TelegramPhoneNumber}#', $TelegramPhoneNumber
    $content = $content -replace '#{CryptoPayApiToken}#', $CryptoPayApiToken
    Set-Content -Path $appSettingsPath -Value $content
    
    Write-Host "✅ Secrets injected. Verifying final content..."
    Write-Host "------------------ FINAL CONFIG FILE ------------------" -ForegroundColor Yellow
    Get-Content $appSettingsPath
    Write-Host "-----------------------------------------------------" -ForegroundColor Yellow

    # --- STEP 2.3: LAUNCH APPLICATION ---
    Write-Host "[2.3] Launching '$ExeName' with Production environment..."
    # We set the environment variable just before launch to ensure it reads the correct appsettings file.
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    Start-Process -FilePath (Join-Path $DeployPath $ExeName) -WorkingDirectory $DeployPath
    Write-Host "✅ Launch command issued."

    # --- STEP 2.4: VERIFY APPLICATION IS RUNNING ---
    Write-Host "[2.4] Waiting 10 seconds for process to stabilize..."
    Start-Sleep -Seconds 10
    $runningProcess = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        throw "FATAL: Process '$AppName' IS NOT RUNNING. It has crashed. Since config is confirmed correct, the issue is now a code-level problem (e.g., cannot connect to database with provided connection string)."
    }
    
    Write-Host "✅✅✅ SCRIPT 2 SUCCESS: Process '$AppName' is confirmed to be running!" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ SCRIPT 2 FAILED! ---"
    Write-Error $_.Exception.ToString()
    exit 1
} finally {
    Write-Host "--- SCRIPT 2 FINISHED ---"
    Stop-Transcript
}