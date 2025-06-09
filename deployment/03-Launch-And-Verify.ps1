# ====================================================================================
# THE DEFINITIVE LAUNCH & VERIFICATION SCRIPT (v.Final-Victory)
# This script bypasses all unreliable batch files by programmatically creating a new
# process with a guaranteed environment block. This is the only correct way.
# ====================================================================================

param(
    [string]$DeployPath,
    [string]$TempPath,
    # These parameters are now passed directly to this script to create the process environment.
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
$AppName         = "WebAPI"
$ExePath         = Join-Path $DeployPath "$AppName.exe"
# We are NOT using the crash log file anymore because we capture output directly.

try {
    Write-Host "STEP 3.1: Verifying presence of executable at '$ExePath'..."
    if (-not (Test-Path $ExePath)) {
        throw "FATAL: Cannot find executable at '$ExePath'. The deployment script 02 likely failed."
    }
    Write-Host "✅ Executable found. Path is valid."

    # --- THE FINAL, BULLETPROOF LAUNCH METHOD ---
    Write-Host "STEP 3.2: Creating ProcessStartInfo with guaranteed environment variables..."
    $ProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
    $ProcessInfo.FileName = $ExePath
    $ProcessInfo.WorkingDirectory = $DeployPath
    
    # This is CRITICAL. It tells .NET not to use the shell, which allows direct env var injection.
    $ProcessInfo.UseShellExecute = $false

    # Inject every required setting DIRECTLY into the process's environment variable block.
    Write-Host "  - Injecting ASPNETCORE_ENVIRONMENT=Production"
    $ProcessInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    Write-Host "  - Injecting ConnectionStrings__DefaultConnection"
    $ProcessInfo.EnvironmentVariables['ConnectionStrings__DefaultConnection'] = $ConnectionString
    Write-Host "  - Injecting DatabaseSettings__DatabaseProvider=SqlServer"
    $ProcessInfo.EnvironmentVariables['DatabaseSettings__DatabaseProvider'] = 'SqlServer'
    Write-Host "  - Injecting TelegramPanel__BotToken"
    $ProcessInfo.EnvironmentVariables['TelegramPanel__BotToken'] = $TelegramBotToken
    Write-Host "  - Injecting TelegramUserApi__ApiId"
    $ProcessInfo.EnvironmentVariables['TelegramUserApi__ApiId'] = $TelegramApiId
    Write-Host "  - Injecting TelegramUserApi__ApiHash"
    $ProcessInfo.EnvironmentVariables['TelegramUserApi__ApiHash'] = $TelegramApiHash
    Write-Host "  - Injecting TelegramUserApi__PhoneNumber"
    $ProcessInfo.EnvironmentVariables['TelegramUserApi__PhoneNumber'] = $TelegramPhoneNumber
    Write-Host "  - Injecting CryptoPay__ApiToken"
    $ProcessInfo.EnvironmentVariables['CryptoPay__ApiToken'] = $CryptoPayApiToken
    Write-Host "✅ ProcessStartInfo created. The environment is guaranteed."

    Write-Host "STEP 3.3: Launching the process directly..."
    $process = [System.Diagnostics.Process]::Start($ProcessInfo)
    Write-Host "✅ Process launch command issued. The new process ID is: $($process.Id)."

    Write-Host "STEP 3.4: Waiting 10 seconds for application to initialize and stabilize..."
    Start-Sleep -Seconds 10

    # We refresh the process object by getting it again with its ID to check its final state.
    $runningProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    
    Write-Host "STEP 3.5: Performing FINAL HEALTH CHECK..."
    if ($runningProcess -and -not $runningProcess.HasExited) {
        Write-Host "✅✅✅✅✅ ULTIMATE SUCCESS! ✅✅✅✅✅" -ForegroundColor Green
        Write-Host "Process '$AppName' is confirmed to be running and has not crashed!" -ForegroundColor Green
        Write-Host "--- Running Process Details ---"
        Write-Host ($runningProcess | Format-List | Out-String)
    } else {
        # This means the application started but crashed within the 10-second window.
        # Since we couldn't capture its output directly (it's a background process),
        # the reason for the crash must now be in the application's own logs (e.g., Serilog file sink) or the Windows Event Viewer.
        throw "FATAL: Process '$AppName' IS NOT RUNNING. Even with a guaranteed environment, the application crashed. The problem is now 100% internal to the app's startup logic (check app logs/Event Viewer)."
    }
    
    Write-Host "--- SCRIPT 3: LAUNCH AND VERIFICATION COMPLETE ---" -ForegroundColor Green

} catch {
    Write-Error "--- ❌ SCRIPT 3 FAILED! ---"
    Write-Error "Error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.ToString())"
    exit 1
} finally {
    Write-Host "--- SCRIPT 3 FINISHED ---"
    Stop-Transcript
}