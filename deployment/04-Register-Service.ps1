param(
    [string]$DeployPath,
    [string]$TempPath
)

# ──────────────────────────────────────────────────────────────────────────────
# Setup Logging & Error Policy
# ──────────────────────────────────────────────────────────────────────────────
$ScriptLogFile         = Join-Path $TempPath "04-Service-Log-$(Get-Date -Format yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

# ──────────────────────────────────────────────────────────────────────────────
# Variables
# ──────────────────────────────────────────────────────────────────────────────
$ServiceName  = 'ForexTradingBotAPI'
$DisplayName  = 'Forex Trading Bot API Service'
$ExePath      = Join-Path $DeployPath 'WebAPI.exe'
$maxDeleteSec = 30
$maxStartSec  = 15

# ──────────────────────────────────────────────────────────────────────────────
function Write-Step {
    param($msg) ; Write-Host "`n=== $msg ===" -ForegroundColor Cyan
}

# ──────────────────────────────────────────────────────────────────────────────
# 1) Verify EXE
# ──────────────────────────────────────────────────────────────────────────────
Write-Step 'VERIFY EXECUTABLE'
if (-not (Test-Path $ExePath)) {
    throw "FATAL: Executable not found at: $ExePath"
}
Write-Host "✅ Found: $ExePath"

# ──────────────────────────────────────────────────────────────────────────────
# 2) Stop & Delete Existing Service
# ──────────────────────────────────────────────────────────────────────────────
Write-Step "STOP & DELETE SERVICE [$ServiceName]"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

    # Issue Delete
    sc.exe delete $ServiceName | Out-Null

    # Wait until service is gone or timeout
    try {
        Wait-Service -Name $ServiceName -Timeout $maxDeleteSec -ErrorAction Stop
        throw "FATAL: Service still exists after $maxDeleteSec seconds."
    } catch {
        # On “service not found” or timeout, catch filter:
        if ($_.Exception -is [System.Management.Automation.TimeoutException]) {
            throw $_
        }
        Write-Host "✅ Service removed within $maxDeleteSec seconds."
    }
} else {
    Write-Host "ℹ️  No existing service to remove."
}

# ──────────────────────────────────────────────────────────────────────────────
# 3) Create New Service
# ──────────────────────────────────────────────────────────────────────────────
Write-Step "CREATE SERVICE [$ServiceName]"
New-Service `
    -Name         $ServiceName `
    -BinaryPathName "`"$ExePath`"" `
    -DisplayName  $DisplayName `
    -StartupType  Automatic
Write-Host "✅ Service created."

# ──────────────────────────────────────────────────────────────────────────────
# 4) Configure Recovery
# ──────────────────────────────────────────────────────────────────────────────
Write-Step 'CONFIGURE RECOVERY OPTIONS'
sc.exe failure $ServiceName reset=86400 actions=restart/60000 | Out-Null
Write-Host "✅ Recovery configured."

# ──────────────────────────────────────────────────────────────────────────────
# 5) Start & Verify Running
# ──────────────────────────────────────────────────────────────────────────────
Write-Step 'START SERVICE'
Start-Service -Name $ServiceName

Write-Host "Waiting up to $maxStartSec seconds for status=Running..."
if (-not (Wait-Service -Name $ServiceName -Timeout $maxStartSec)) {
    $status = (Get-Service -Name $ServiceName).Status
    throw "FATAL: Service did not reach 'Running' within $maxStartSec seconds (status: $status)."
}

Write-Host "✅✅✅ SERVICE IS RUNNING ✅✅✅" -ForegroundColor Green

# ──────────────────────────────────────────────────────────────────────────────
# Done
# ──────────────────────────────────────────────────────────────────────────────
Stop-Transcript
