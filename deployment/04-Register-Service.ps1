param(
    [string]$DeployPath,
    [string]$TempPath
)

$ScriptLogFile = Join-Path $TempPath "04-Service-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 4: REGISTER & LAUNCH WINDOWS SERVICE ---"

$ServiceName  = "ForexTradingBotAPI"
$DisplayName  = "Forex Trading Bot API Service"
$ExePath      = Join-Path $DeployPath "WebAPI.exe"

# 1. Verify EXE exists
Write-Host "Verifying presence of executable at '$ExePath'..."
if (-not (Test-Path $ExePath)) {
    throw "FATAL: Cannot find executable at '$ExePath'."
}
Write-Host "✅ Executable found."

# 2. Stop & delete any existing service
Write-Host "Stopping and removing existing service '$ServiceName' for a clean install..."
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service  -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null

    # Wait up to 60s for deletion to complete
    try {
        Write-Host "Waiting for service to be fully removed..."
        Wait-Service -Name $ServiceName -Timeout 60 -ErrorAction Stop
        throw "This should never happen—service still exists after deletion."
    } catch {
        Write-Host "✅ Service successfully removed."
    }
}

# 3. Create the new service
Write-Host "Creating a new, clean Windows Service '$ServiceName'..."
New-Service `
    -Name         $ServiceName `
    -BinaryPathName "`"$ExePath`"" `
    -DisplayName  $DisplayName `
    -StartupType  Automatic
Write-Host "✅ New service created."

# 4. Configure automatic restart on failure
Write-Host "Configuring service recovery options..."
sc.exe failure $ServiceName reset=86400 actions=restart/60000 | Out-Null
Write-Host "✅ Recovery options set."

# 5. Start and verify
Write-Host "Starting the service '$ServiceName'..."
Start-Service -Name $ServiceName

Write-Host "Waiting for service to reach 'Running' state (15s timeout)..."
if (-not (Wait-Service -Name $ServiceName -Timeout 15)) {
    throw "FATAL: Service '$ServiceName' failed to start within 15 seconds."
}

Write-Host "✅✅✅ VICTORY! The Windows Service is RUNNING! ✅✅✅" -ForegroundColor Green
Stop-Transcript
