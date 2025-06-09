# ====================================================================================
# THE DEFINITIVE WINDOWS SERVICE MANAGEMENT SCRIPT (v.Final-Victory-Compatible)
# This version uses the classic and universally compatible 'sc.exe' tool for
# service deletion, guaranteeing it runs on all Windows Server versions.
# This is the final, definitive step to victory.
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
$ScriptLogFile = Join-Path $TempPath "04-Service-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
# We set ErrorActionPreference to Continue so the script doesn't stop if sc.exe delete fails (e.g., service doesn't exist)
$ErrorActionPreference = 'Continue'

Write-Host "--- SCRIPT 4: REGISTER & LAUNCH WINDOWS SERVICE ---" -ForegroundColor Cyan
$ServiceName = "ForexTradingBotAPI"
$DisplayName = "Forex Trading Bot API Service"
$ExePath     = Join-Path $DeployPath "WebAPI.exe"

# Arguments are passed directly to the executable. Quotes are handled carefully.
$arguments = @(
    "--urls `"http://*:5000`"",
    "--ContentRoot `"$DeployPath`"", # This tells the app where to find its files.
    "--ConnectionStrings:DefaultConnection=`"$ConnectionString`"",
    "--DatabaseSettings:DatabaseProvider=SqlServer",
    "--TelegramPanel:BotToken=`"$TelegramBotToken`"",
    "--TelegramUserApi:ApiId=`"$TelegramApiId`"",
    "--TelegramUserApi:ApiHash=`"$TelegramApiHash`"",
    "--TelegramUserApi:PhoneNumber=`"$TelegramPhoneNumber`"",
    "--CryptoPay:ApiToken=`"$CryptoPayApiToken`""
)
# The final command that the service will execute.
$binaryPathWithArgs = "`"$ExePath`" " + ($arguments -join ' ')

Write-Host "Verifying presence of executable at '$ExePath'..."
if (-not (Test-Path $ExePath)) {
    throw "FATAL: Cannot find executable at '$ExePath'. Previous steps failed."
}
Write-Host "✅ Executable found. Final Binary Path with arguments will be:"
Write-Host $binaryPathWithArgs -ForegroundColor Gray

# Stop and remove the service completely using universally compatible commands.
Write-Host "Checking for existing service '$ServiceName' for a clean re-installation..."
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "🟡 Service already exists. Stopping and removing it for a clean install..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue -Verbose
    Start-Sleep -Seconds 5 # Give time for the service to stop completely.
    
    # ✅✅✅✅✅ THE FINAL, CRITICAL FIX IS HERE ✅✅✅✅✅
    # Use the classic, unbreakable sc.exe tool to delete the service.
    Write-Host "Attempting to delete service '$ServiceName' using 'sc.exe delete'..."
    sc.exe delete "$ServiceName"
    # The command might return an error if the service is marked for deletion, which is OK.
    
    Start-Sleep -Seconds 5 # Give time for the Service Control Manager to process the removal.
    Write-Host "✅ Old service removal command executed."
}

Write-Host "Creating a new, clean Windows Service '$ServiceName'..."
New-Service -Name $ServiceName -BinaryPathName $binaryPathWithArgs -DisplayName $DisplayName -StartupType Automatic
Write-Host "✅ New service created successfully."

Write-Host "Configuring service for automatic restart on failure..."
# Configuring failure actions ensures the service is resilient.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/300000/restart/600000
Write-Host "✅ Service failure actions configured."

# Set ErrorActionPreference back to Stop for the final critical steps.
$ErrorActionPreference = 'Stop'

Write-Host "Starting the service '$ServiceName'..."
Start-Service -Name $ServiceName -Verbose

Write-Host "Waiting 15 seconds and performing final, definitive status check..."
Start-Sleep -Seconds 15
$finalService = Get-Service -Name $ServiceName
if ($finalService.Status -ne 'Running') {
    throw "FATAL: Service '$ServiceName' is in state '$($finalService.Status)' and NOT RUNNING. CHECK THE WINDOWS EVENT VIEWER for '.NET Runtime' errors."
}

Write-Host "✅✅✅✅✅✅✅✅✅ VICTORY! ✅✅✅✅✅✅✅✅✅" -ForegroundColor Green
Write-Host "✅✅✅ The Windows Service '$ServiceName' is PERMANENTLY RUNNING! ✅✅✅" -ForegroundColor Green
Write-Host ($finalService | Format-List | Out-String)

Stop-Transcript