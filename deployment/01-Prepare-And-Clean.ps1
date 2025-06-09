# deployment/01-Prepare-And-Clean.ps1
param(
    [string]$DeployPath,
    [string]$TempPath,
    [string]$SessionFolderName
)
$ScriptLogFile = Join-Path $TempPath "01-Clean-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 1: PREPARE AND CLEAN (ATOMIC) ---" -ForegroundColor Cyan
Write-Host "Verification: Script is running."
if (-not (Test-Path $DeployPath)) { Write-Host "Initial state: Deploy path '$DeployPath' does not exist. Creating it." -ForegroundColor Yellow; New-Item -Path $DeployPath -ItemType Directory -Force | Out-Null }
if (-not (Test-Path $TempPath)) { throw "FATAL: Temp path '$TempPath' not found!" }

$SessionSourcePath = Join-Path $DeployPath $SessionFolderName
$SessionBackupPath = Join-Path $TempPath "$SessionFolderName.bak"

Write-Host "STEP 1.1: Stopping any running WebAPI process..."
Get-Process -Name "WebAPI" -ErrorAction SilentlyContinue | Stop-Process -Force -Verbose
Write-Host "✅ Stop command executed."

Write-Host "STEP 1.2: Securely backing up '$SessionSourcePath' to '$SessionBackupPath'..."
if (Test-Path $SessionSourcePath) {
    if (Test-Path $SessionBackupPath) { Remove-Item $SessionBackupPath -Recurse -Force -Verbose }
    Copy-Item -Path $SessionSourcePath -Destination $SessionBackupPath -Recurse -Force -Verbose
    Write-Host "✅ Session folder backed up successfully."
} else { Write-Host "🟡 No existing Session folder found to back up." }

Write-Host "STEP 1.3: OBLITERATING old deployment directory: $DeployPath..."
if (Test-Path $DeployPath) { Remove-Item -Path $DeployPath -Recurse -Force -Verbose }
New-Item -Path $DeployPath -ItemType Directory -Force -Verbose | Out-Null
Write-Host "✅ Re-created clean deployment directory."

Write-Host "STEP 1.4: Restoring Session folder..."
if (Test-Path $SessionBackupPath) {
    Move-Item -Path $SessionBackupPath -Destination $SessionSourcePath -Force -Verbose
    Write-Host "✅ Session folder restored."
}

Write-Host "--- SCRIPT 1: PREPARATION AND CLEANUP COMPLETE ---" -ForegroundColor Green
Stop-Transcript