# ====================================================================================
# THE DEFINITIVE LAUNCH & VERIFICATION SCRIPT (v.Final-Victory)
# This script executes the application DIRECTLY, without any unreliable batch files.
# It uses robust process creation with full output redirection to capture the crash reason.
# This is the final and only correct way to solve this problem.
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
$ScriptLogFile = Join-Path $TempPath "03-Verify-Log-$(Get-Date -f yyyyMMdd-HHmmss).txt"
Start-Transcript -Path $ScriptLogFile -Append
$ErrorActionPreference = 'Stop'

Write-Host "--- SCRIPT 3: DIRECT LAUNCH AND VERIFY STARTED ---" -ForegroundColor Cyan
$AppName         = "WebAPI"
$ExePath         = Join-Path $DeployPath "$AppName.exe"
$AppCrashLogFile = Join-Path $TempPath "App-Crash-Log.txt"

try {
    Write-Host "STEP 3.1: Verifying presence of executable at '$ExePath'..."
    if (-not (Test-Path $ExePath)) {
        throw "FATAL: Cannot find executable at '$ExePath'. The deployment step likely failed."
    }
    Write-Host "✅ Executable found."

    # --- THE FINAL, BULLETPROOF LAUNCH METHOD ---
    Write-Host "STEP 3.2: Creating ProcessStartInfo with guaranteed environment and redirection..."
    $ProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
    $ProcessInfo.FileName = $ExePath
    $ProcessInfo.WorkingDirectory = $DeployPath
    
    # This is CRITICAL. It allows redirection of output streams.
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

    Write-Host "✅ ProcessStartInfo created. The environment and output redirection are guaranteed."
    Write-Host "STEP 3.3: Launching the process directly and waiting for it to exit..."

    $process = [System.Diagnostics.Process]::Start($ProcessInfo)
    
    # Read the output streams and write them to the crash log file.
    $process.StandardOutput.ReadToEnd() | Out-File -FilePath $AppCrashLogFile -Append
    $process.StandardError.ReadToEnd() | Out-File -FilePath $AppCrashLogFile -Append
    
    # This is a BLOCKING call. The script will wait here until WebAPI.exe exits.
    $process.WaitForExit()
    
    Write-Host "✅ Application has finished executing (it likely crashed as expected)."

    # --- After the process has exited, we do our final check. ---
    Write-Host "STEP 3.4: Performing FINAL HEALTH CHECK..."
    
    # Check if the exit code was 0 (success). Any other value means a crash.
    if ($process.ExitCode -eq 0) {
        Write-Host "✅✅✅ UNEXPECTED SUCCESS: The application ran and exited cleanly (Exit Code 0). This is unusual for a service." -ForegroundColor Green
    } else {
        # This is the expected path for a crash.
        Write-Warning "Application exited with a non-zero Exit Code: $($process.ExitCode). This indicates a crash."
        # The crash log has already been written by this point.
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