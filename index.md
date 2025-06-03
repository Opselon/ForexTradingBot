@echo off
SETLOCAL ENABLEEXTENSIONS
SETLOCAL DISABLEDELAYEDEXPANSION

:: --- Configuration ---
SET "OUTPUT_FILE=ProjectStructure.log"
SET "ROOT_PATH=%CD%"
:: Add more common exclusion patterns (case-insensitive where possible)
SET "EXCLUDE_PATTERNS=\\bin\\|\\obj\\|\\.git\\|\\.vs\\|node_modules|\\__pycache__|\\target\\|\\build\\|\\.gradle\\|\\.idea\\|\\.vscode\\|\\dist\\|\\vendor\\|\\tmp\\|\\log\\|\\docs\\|\\test\\|\\tests\\|\\migrations\\|\\env\\|\\venv\\|\\.venv\\"
SET "PAUSE_ON_EXIT=true"  :: Set to 'false' to close the window automatically on completion/error

:: --- Script Start ---
echo.
echo =========================================
echo  Generating Project File Index
echo =========================================
echo.
echo Root directory: "%ROOT_PATH%"
echo Output file: "%OUTPUT_FILE%"
echo.

:: --- Delete old log if exists ---
if exist "%OUTPUT_FILE%" (
    echo Deleting existing log file: "%OUTPUT_FILE%"
    del "%OUTPUT_FILE%"
    if exist "%OUTPUT_FILE%" (
        echo ERROR: Failed to delete old log file. Check file permissions.
        set "LAST_ERRORLEVEL=1"
        goto :cleanup
    )
)

:: --- Print header to log file ---
echo Generating clean project file index... > "%OUTPUT_FILE%"
echo Root: %ROOT_PATH% >> "%OUTPUT_FILE%"
echo Exclusions: %EXCLUDE_PATTERNS% >> "%OUTPUT_FILE%"
echo ========================================= >> "%OUTPUT_FILE%"
echo. >> "%OUTPUT_FILE%"

:: --- Use PowerShell to recursively generate the tree view ---
echo Running PowerShell script...

powershell.exe -NoLogo -NoProfile -Command "& {
    param(
        [string]$RootPath,
        [string]$OutputFile,
        [string]$ExcludePatterns
    )
    try {
        # Ensure RootPath is a fully qualified, normalized path for consistent depth calculation
        $absoluteRootPath = (Get-Item -LiteralPath $RootPath).FullName
        $basePathSegments = ($absoluteRootPath -split '[\\/]').Count # Handle both \ and / for robustness

        # Get all items, filter by exclusion patterns, and then process
        Get-ChildItem -LiteralPath $RootPath -Recurse -Force | Where-Object {
            $_.FullName -notmatch $ExcludePatterns
        } | ForEach-Object {
            # Calculate current item's depth relative to the root
            $itemPathSegments = ($_.FullName -split '[\\/]').Count
            $relativeDepth = $itemPathSegments - $basePathSegments

            # Ensure non-negative relative depth for items directly under root
            if ($relativeDepth -lt 0) { $relativeDepth = 0 }

            # Indentation: 2 spaces per level
            $spaces = '  ' * $relativeDepth

            # Format output: append / for directories
            $itemName = $_.Name
            if ($_.PSIsContainer) {
                $itemName = "$itemName/"
            }
            Add-Content -Path $OutputFile -Value "$spaces$itemName"
        }
        # PowerShell successfully completed, exit code 0
        exit 0
    } catch {
        # An error occurred in PowerShell, write error message and exit with code 1
        Write-Error ('An error occurred during PowerShell execution: {0}' -f $_.Exception.Message)
        exit 1
    }
}" -RootPath "%ROOT_PATH%" -OutputFile "%OUTPUT_FILE%" -ExcludePatterns "%EXCLUDE_PATTERNS%"

:: Capture PowerShell's exit code
set "LAST_ERRORLEVEL=%ERRORLEVEL%"

:: --- Check PowerShell execution result ---
if %LAST_ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: PowerShell script failed with an error.
    echo Please check the console output above for details.
    echo.
    goto :cleanup
)

:: --- Done ---
echo.
echo Project structure successfully saved to: "%OUTPUT_FILE%"
echo.

:cleanup
if /I "%PAUSE_ON_EXIT%"=="true" (
    echo Press any key to exit...
    pause > nul
)
ENDLOCAL
exit /b %LAST_ERRORLEVEL%