# ====================================================================================
# THE DEFINITIVE WINDOWS SERVICE MANAGEMENT SCRIPT (v.Final-Victory-Robust)
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

Write-Host "Verifying presence of executable at '$ExePath'..."
if (-not (Test-Path $ExePath)) {
    throw "FATAL: Cannot find executable at '$ExePath'."
}
Write-Host "✅ Executable found."

Write-Host "Stopping and removing existing service '$ServiceName' for a clean install..."
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5 # Give it a moment to process the stop command
    }
    
    sc.exe delete "$ServiceName"
    
    # --- START: Robust Wait Loop (THE FIX) ---
    # This loop replaces the unreliable 'Start-Sleep'. It actively checks until
    # the service is confirmed to be gone, with a 60-second timeout.
    Write-Host "Waiting for service '$ServiceName' to be fully removed..."
    $timeout = 60 # seconds
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $timeout) {
        $serviceCheck = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $serviceCheck) {
            Write-Host "`n✅ Service fully removed."
            break # Exit the loop, the service is gone
        }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 2
    }

    if ($stopwatch.Elapsed.TotalSeconds -ge $timeout) {
        throw "FATAL: Timed out after $timeout seconds waiting for service '$ServiceName' to be deleted. It may be stuck. Please check the server."
    }
    # --- END: Robust Wait Loop ---
}

Write-Host "Creating a new, clean Windows Service '$ServiceName'..."
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