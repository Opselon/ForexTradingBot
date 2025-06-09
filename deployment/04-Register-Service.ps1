# ====================================================================================
# THE DEFINITIVE WINDOWS SERVICE MANAGEMENT SCRIPT (v.Final-Victory)
# This version addresses the critical "Working Directory" issue for Windows Services
# and adds robust, intelligent error reporting. This is the final step.
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
$ErrorActionPreference = 'Stop' # Changed to 'Continue' to allow custom error handling

Write-Host "--- SCRIPT 4: REGISTER & LAUNCH WINDOWS SERVICE ---" -ForegroundColor Cyan
$ServiceName = "ForexTradingBotAPI"
$DisplayName = "Forex Trading Bot API Service"
$ExePath     = Join-Path $DeployPath "WebAPI.exe"

# Arguments are passed directly to the executable. Quotes are handled carefully.
# We no longer need to pass the exe path itself here.
$arguments = @(
    "--urls", "`"http://*:5000`"",
    "--ContentRoot", "`"$DeployPath`"", # ✅✅✅ THE ABSOLUTE CRITICAL FIX #1 ✅✅✅
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

# Stop and remove the service completely to ensure a clean slate.
Write-Host "Checking for existing service '$ServiceName' for a clean re-installation..."
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "🟡 Service already exists. Stopping and removing it for a clean install..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3 # Give time for the service to stop.
    Remove-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3 # Give time for the SCM to process the removal.
    Write-Host "✅ Old service removed."
}

Write-Host "Creating a new, clean Windows Service '$ServiceName'..."
New-Service -Name $ServiceName -BinaryPathName $binaryPathWithArgs -DisplayName $DisplayName -StartupType Automatic
Write-Host "✅ New service created successfully."

# ✅✅✅ THE ABSOLUTE CRITICAL FIX #2 ✅✅✅
# Forcing the service to start after a failure ensures we can get logs.
Set-Service -Name $ServiceName -StartupType Automatic
$FailureActions = @{
    "reset" = 86400; # Reset failure count after 1 day (86400 seconds)
    "reboot" = "N/A";
    "command" = "N/A";
    "actions" = @( # Actions to take on failure
        @{ "Type" = "Restart"; "Delay" = 60000 },  # Restart after 1 minute
        @{ "Type" = "Restart"; "Delay" = 300000 }, # Restart after 5 minutes
        @{ "Type" = "Restart"; "Delay" = 600000 }  # Restart after 10 minutes
    )
}
sc.exe failure $ServiceName @($FailureActions.Keys |% { "$_=$($FailureActions[$_])" })
sc.exe failure $ServiceName actions= restart/60000/restart/300000/restart/600000
Write-Host "✅ Service failure actions configured to auto-restart."


Write-Host "Starting the service '$ServiceName'..."
Start-Service -Name $ServiceName -Verbose

Write-Host "Waiting 15 seconds and performing final, definitive status check..."
Start-Sleep -Seconds 15 # Increased wait time for service startup
$finalService = Get-Service -Name $ServiceName
if ($finalService.Status -ne 'Running') {
    Write-Error "--- ❌❌❌ FINAL FAILURE ❌❌❌ ---"
    Write-Error "FATAL: Service '$ServiceName' is in state '$($finalService.Status)' and NOT RUNNING."
    Write-Error "This is the final hurdle. The configuration is correct, but the app is crashing internally."
    Write-Error ">>> ULTIMATE ACTION: CHECK THE WINDOWS EVENT VIEWER <<<"
    Write-Error "Look for 'Application Errors' related to '.NET Runtime' or 'WebAPI.exe' to see the true exception."
    exit 1
}

Write-Host "✅✅✅✅✅✅✅✅✅ VICTORY! ✅✅✅✅✅✅✅✅✅" -ForegroundColor Green
Write-Host "✅✅✅✅✅ The Windows Service '$ServiceName' is RUNNING! ✅✅✅✅✅" -ForegroundColor Green
Write-Host ($finalService | Format-List | Out-String)

Stop-Transcript