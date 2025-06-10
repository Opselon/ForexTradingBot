# ====================================================================================
# THE DEFINITIVE WINDOWS SERVICE MANAGEMENT SCRIPT (v.Final-Victory-Simplified)
# This version is simpler because the .NET app is now service-aware.
# It only needs to create the service pointing to the EXE.
# ====================================================================================
param(
    [string]$DeployPath,
    [string]$TempPath
)
$ScriptLogFile = Join-Path $TempPath "04-Service-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 4: REGISTER & LAUNCH WINDOWS SERVICE ---" -ForegroundColor Cyan
$ServiceName = "ForexTradingBotAPI"
$DisplayName = "Forex Trading Bot API Service"
$ExePath     = Join-Path $DeployPath "WebAPI.exe"
# The service no longer needs command-line arguments because it will read them
# from appsettings.Production.json, which is loaded due to ASPNETCORE_ENVIRONMENT.

Write-Host "Verifying presence of executable at '$ExePath'..."
if (-not (Test-Path $ExePath)) {
    throw "FATAL: Cannot find executable at '$ExePath'."
}
Write-Host "✅ Executable found."

Write-Host "Stopping and removing existing service '$ServiceName' for a clean install..."
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 10
    sc.exe delete "$ServiceName"
    Start-Sleep -Seconds 10
}

Write-Host "Creating a new, clean Windows Service '$ServiceName'..."
# The BinaryPathName is now just the path to the executable. No arguments needed.
New-Service -Name $ServiceName -BinaryPathName "`"$ExePath`"" -DisplayName $DisplayName -StartupType Automatic
Write-Host "✅ New service created successfully."

Write-Host "Configuring service for automatic restart on failure..."
sc.exe failure $ServiceName reset= 86400 actions= restart/60000
Write-Host "✅ Service failure actions configured."

Write-Host "Starting the service '$ServiceName'..."
Start-Service -Name $ServiceName -Verbose

Write-Host "Waiting 15 seconds and performing final status check..."
Start-Sleep -Seconds 15
$finalService = Get-Service -Name $ServiceName
if ($finalService.Status -ne 'Running') {
    throw "FATAL: Service '$ServiceName' is in state '$($finalService.Status)'. Check Windows Event Viewer."
}

Write-Host "✅✅✅✅✅✅✅✅✅ VICTORY! The Windows Service is RUNNING! ✅✅✅✅✅✅✅✅✅" -ForegroundColor Green
Stop-Transcript