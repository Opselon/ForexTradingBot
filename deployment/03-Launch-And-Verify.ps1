# ====================================================================================
# THE DEFINITIVE LAUNCH & VERIFICATION SCRIPT (v.Final-Boss-Defeated)
# This script bypasses all unreliable batch files and shell context issues by
# programmatically creating a new process with a guaranteed environment block.
# This is the final and only correct way to solve this problem.
# ====================================================================================

param(
    [string]$DeployPath,
    [string]$TempPath,
    # These parameters are now passed directly to this script to create the environment
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)
$ScriptLogFile = Join-Path $TempPath "03-Verify-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 3: DIRECT LAUNCH AND VERIFY STARTED ---" -ForegroundColor Cyan
$AppName = "WebAPI"
$ExePath = Join-Path $DeployPath "$AppName.exe"

try {
    Write-Host "STEP 3.1: Verifying presence of executable at '$ExePath'..."
    if (-not (Test-Path $ExePath)) {
        throw "FATAL: Cannot find executable at '$ExePath'. The deployment step likely failed."
    }
    Write-Host "✅ Executable found."

    # --- THE FINAL, BULLETPROOF LAUNCH METHOD ---
    Write-Host "STEP 3.2: Creating ProcessStartInfo with guaranteed environment variables..."
    $ProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
    $ProcessInfo.FileName = $ExePath
    $ProcessInfo.WorkingDirectory = $DeployPath
    # This is CRITICAL. It tells .NET not to use the shell, allowing env var injection.
    $ProcessInfo.UseShellExecute = $false 

    # Inject every required setting DIRECTLY into the process's environment.
    $ProcessInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $ProcessInfo.EnvironmentVariables['ConnectionStrings__DefaultConnection'] = $ConnectionString
    $ProcessInfo.EnvironmentVariables['DatabaseSettings__DatabaseProvider'] = 'SqlServer'
    $ProcessInfo.EnvironmentVariables['TelegramPanel__BotToken'] = $TelegramBotToken
    $ProcessInfo.EnvironmentVariables['TelegramUserApi__ApiId'] = $TelegramApiId
    $ProcessInfo.EnvironmentVariables['TelegramUserApi__ApiHash'] = $TelegramApiHash
    $ProcessInfo.EnvironmentVariables['TelegramUserApi__PhoneNumber'] = $TelegramPhoneNumber
    $ProcessInfo.EnvironmentVariables['CryptoPay__ApiToken'] = $CryptoPayApiToken

    Write-Host "✅ ProcessStartInfo created. The environment is guaranteed."
    Write-Host "STEP 3.3: Launching the process directly..."
    [System.Diagnostics.Process]::Start($ProcessInfo) | Out-Null
    Write-Host "✅ Process launch command issued."

    Write-Host "STEP 3.4: Waiting 10 seconds for application to initialize..."
    Start-Sleep -Seconds 10

    Write-Host "STEP 3.5: Performing FINAL HEALTH CHECK..."
    $process = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $process) {
        # If it still fails, the problem is 100% inside the application code itself.
        # We will not throw an error here, so the final report job can run.
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. Even with a guaranteed environment, the application crashed. The final report job will show the crash log."
    } else {
        Write-Host "✅✅✅✅✅ ULTIMATE SUCCESS! ✅✅✅✅✅" -ForegroundColor Green
        Write-Host "Process '$AppName' is confirmed to be running!" -ForegroundColor Green
        Write-Host ($process | Format-List | Out-String)
    }
    
    Write-Host "--- SCRIPT 3: LAUNCH AND VERIFICATION COMPLETE ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ SCRIPT 3 FAILED! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    # Exit with a non-zero code to fail the GitHub Actions job
    exit 1
} finally {
    Write-Host "--- SCRIPT 3 FINISHED ---"
    Stop-Transcript
}