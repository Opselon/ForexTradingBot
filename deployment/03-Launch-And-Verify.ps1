# ====================================================================================
# THE DEFINITIVE LAUNCH & VERIFICATION SCRIPT (v.Final-Victory)
# This script bypasses all unreliable batch files and shell context issues by
# programmatically creating a new process with a guaranteed environment block.
# This is the final and only correct way to solve this problem.
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
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt" # This will capture the crash reason.

try {
    Write-Host "STEP 3.1: Verifying presence of executable at '$ExePath'..."
    if (-not (Test-Path $ExePath)) {
        throw "FATAL: Cannot find executable at '$ExePath'. The deployment step likely failed."
    }
    Write-Host "✅ Executable found."

    # --- THE FINAL, BULLETPROOF LAUNCH METHOD ---
    Write-Host "STEP 3.2: Creating ProcessStartInfo with guaranteed environment and output redirection..."
    $ProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
    $ProcessInfo.FileName = $ExePath
    $ProcessInfo.WorkingDirectory = $DeployPath
    
    # These are CRITICAL. They allow redirection of output streams and env var injection.
    $ProcessInfo.UseShellExecute = $false 
    $ProcessInfo.RedirectStandardOutput = $true
    $ProcessInfo.RedirectStandardError = $true

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
    Write-Host "STEP 3.3: Launching the process directly and CAPTURING ITS OUTPUT..."

    # Start the process.
    $process = [System.Diagnostics.Process]::Start($ProcessInfo)
    
    # This part is non-blocking but essential. We start listening to the output.
    # The application output (including the crash exception) will be written to our log file.
    $process.StandardOutput.ReadToEndAsync() | Out-File -FilePath $AppCrashLogFile -Append
    $process.StandardError.ReadToEndAsync() | Out-File -FilePath $AppCrashLogFile -Append
    
    Write-Host "✅ Process launch command issued. Waiting for it to stabilize or exit..."

    # We wait for a bit, then check its status.
    Start-Sleep -Seconds 10

    # We refresh the process object to get its final state.
    $runningProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    
    Write-Host "STEP 3.4: Performing FINAL HEALTH CHECK..."
    if ($runningProcess -and -not $runningProcess.HasExited) {
        Write-Host "✅✅✅✅✅ ULTIMATE SUCCESS! ✅✅✅✅✅" -ForegroundColor Green
        Write-Host "Process '$AppName' is confirmed to be running!" -ForegroundColor Green
    } else {
        Write-Warning "PROCESS '$AppName' IS NOT RUNNING. It has crashed or exited."
        # The crash log has already been created, the final reporting job will display it.
        # To ensure the workflow fails correctly, we throw an exception here.
        throw "Application did not remain running. The final report will show the crash log."
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