# ====================================================================================
# THE DEFINITIVE WINDOWS SERVICE MANAGEMENT SCRIPT
# This script intelligently handles the creation, updating, and starting of
# the application as a reliable Windows Service.
# ====================================================================================
param(
    [string]$DeployPath,
    [string]$TempPath,
    # These parameters will be passed to the service itself.
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)
$ScriptLogFile = Join-Path $TempPath "04-Service-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 4: REGISTER AS WINDOWS SERVICE STARTED ---" -ForegroundColor Cyan
$ServiceName = "ForexTradingBotAPI"
$DisplayName = "Forex Trading Bot API Service"
$ExePath     = Join-Path $DeployPath "WebAPI.exe"

# We must escape the arguments properly for the service binary path.
$arguments = @(
    "--urls `"http://*:5000`"", # Listen on port 5000 on all network interfaces
    "--ConnectionStrings:DefaultConnection=`"$ConnectionString`"",
    "--DatabaseSettings:DatabaseProvider=SqlServer",
    "--TelegramPanel:BotToken=`"$TelegramBotToken`"",
    "--TelegramUserApi:ApiId=`"$TelegramApiId`"",
    "--TelegramUserApi:ApiHash=`"$TelegramApiHash`"",
    "--TelegramUserApi:PhoneNumber=`"$TelegramPhoneNumber`"",
    "--CryptoPay:ApiToken=`"$CryptoPayApiToken`""
)
$binaryPath = "`"$ExePath`" " + ($arguments -join ' ')

Write-Host "Verifying presence of executable at '$ExePath'..."
if (-not (Test-Path $ExePath)) {
    throw "FATAL: Cannot find executable at '$ExePath'. Previous steps failed."
}
Write-Host "✅ Executable found."

Write-Host "Checking for existing service '$ServiceName'..."
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($service) {
    Write-Host "🟡 Service already exists. Stopping and updating its configuration..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue -Verbose
    # Update the binary path with new potential arguments/secrets.
    Set-Service -Name $ServiceName -BinaryPathName $binaryPath
    Write-Host "✅ Service configuration updated."
} else {
    Write-Host "🟢 Service not found. Creating a new Windows Service..."
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -StartupType Automatic
    Write-Host "✅ New service created successfully."
}

Write-Host "Starting the service '$ServiceName'..."
Start-Service -Name $ServiceName -Verbose
Write-Host "✅ Start command issued."

Write-Host "Waiting 10 seconds and verifying service status..."
Start-Sleep -Seconds 10
$finalService = Get-Service -Name $ServiceName
if ($finalService.Status -ne 'Running') {
    throw "FATAL: Service '$ServiceName' is in state '$($finalService.Status)' and NOT RUNNING. Check Windows Event Viewer for errors."
}

Write-Host "✅✅✅✅✅ ULTIMATE VICTORY! Service '$ServiceName' is RUNNING! ✅✅✅✅✅" -ForegroundColor Green
Write-Host ($finalService | Format-List | Out-String)

Write-Host "--- SCRIPT 4: SERVICE REGISTRATION COMPLETE ---" -ForegroundColor Green
Stop-Transcript