# deployment/03-Launch-And-Verify.ps1
param(
    [string]$DeployPath,
    [string]$TempPath
)
$ScriptLogFile = Join-Path $TempPath "03-Verify-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 3: LAUNCH AND VERIFY STARTED ---" -ForegroundColor Cyan
$AppName = "WebAPI"
$Launcher = Join-Path $DeployPath "start-app.bat"
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt"

# Redefine the launcher to capture crash logs for this final check.
# The previous launcher was for normal operation. This one is for diagnostics.
$DiagnosticLauncher = Join-Path $DeployPath "start-diagnostic.bat"
$DiagnosticBatContent = (Get-Content $Launcher) -replace "start `"`" `"$AppName.exe`"", "`"$AppName.exe`" > `"$AppCrashLogFile`" 2>&1"
Set-Content -Path $DiagnosticLauncher -Value $DiagnosticBatContent -Verbose

Write-Host "STEP 3.1: Executing DIAGNOSTIC launcher '$DiagnosticLauncher'..."
Invoke-Expression -Command "cmd.exe /c $DiagnosticLauncher"
Write-Host "✅ Diagnostic Launcher executed."

Write-Host "STEP 3.2: Waiting 10 seconds for application to initialize..."
Start-Sleep -Seconds 10

Write-Host "STEP 3.3: Performing FINAL HEALTH CHECK..."
$process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
if ($process) {
    Write-Host "✅✅✅ ULTIMATE SUCCESS: Process '$AppName' is confirmed to be running!" -ForegroundColor Green
    Write-Host ($process | Format-List | Out-String)
} else {
    throw "FATAL: Process '$AppName' IS NOT RUNNING. Application has crashed. The crash log should contain the reason."
}

Write-Host "--- SCRIPT 3: LAUNCH AND VERIFICATION COMPLETE ---" -ForegroundColor Green
Stop-Transcript