# deployment/02-Deploy-And-Configure.ps1
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
$ScriptLogFile = Join-Path $TempPath "02-Deploy-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 2: DEPLOY AND CONFIGURE STARTED ---" -ForegroundColor Cyan
$ZipFile = Join-Path $TempPath "release.zip"
$ExeName = "WebAPI.exe"
$Launcher = Join-Path $DeployPath "start-app.bat"

Write-Host "STEP 2.1: Verifying required files..."
if (-not (Test-Path $ZipFile)) { throw "FATAL: release.zip not found in '$TempPath'!" }
if (-not (Test-Path $DeployPath)) { throw "FATAL: Deployment path '$DeployPath' not found!" }
Write-Host "✅ Required files and folders are present."

Write-Host "STEP 2.2: Unpacking new version from '$ZipFile' to '$DeployPath'..."
Expand-Archive -Path $ZipFile -DestinationPath $DeployPath -Force -Verbose
Write-Host "✅ Unpack complete."
if (-not (Test-Path (Join-Path $DeployPath $ExeName))) { throw "FATAL: $ExeName not found after unpack!" }
Write-Host "✅ Verification successful: $ExeName is present in the deployment folder."

Write-Host "STEP 2.3: Creating application launcher '$Launcher'..."
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
Set-Content -Path $Launcher -Value $BatContent -Verbose
Write-Host "✅ Launcher created."

Write-Host "--- SCRIPT 2: DEPLOYMENT AND CONFIGURATION COMPLETE ---" -ForegroundColor Green
Stop-Transcript