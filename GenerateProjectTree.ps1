# ProjectStructure.ps1
# This script generates a clean, hierarchical index of a .NET Core project
# with specific file type inclusions and folder exclusions.

#region Configuration
$OutputFile = "ProjectStructure.log"
$RootPath = (Get-Location).Path # Sets the root path to where the script is run from

# Global exclusions: Items that should be completely ignored and not appear at all.
# These are typically internal or temporary files/folders that don't represent the *project structure*.
$ExcludeGlobalPathsRaw = @(
    ".git\objects",
    ".git\logs",
    ".git\refs",
    ".vs\.tmp",
    ".vs\cache",
    "TestResults",
    "publish",
    "tmp",
    "log",
    ".DS_Store",
    "thumbs.db"
)

# Do Not Recurse: Directories that should be listed, but their content should NOT be traversed.
# For 'netX.Y' folders (e.g., bin\Debug\net9.0), just list the 'net' part and PowerShell will handle the regex.
$DoNotRecurseFolderPatternsRaw = @(
    "bin\Debug\net",
    "bin\Release\net",
    "obj\Debug\net",
    "obj\Release\net",
    "node_modules",
    "target",
    "build",
    ".gradle",
    "vendor",
    "migrations",
    "env",
    "venv",
    ".venv"
)

# Specific file extensions to include in the output (case-insensitive)
$IncludeFileExtensions = @(
    ".cs",
    ".json",
    ".csproj",
    ".sln",
    ".yml",
    ".yaml",
    ".md",
    ".txt",
    ".config",
    ".xml",
    ".ps1",
    ".sh",
    ".js",
    ".ts",
    ".html",
    ".css",
    ".scss"
)

$PauseOnExit = $true # Set to $false to close the window automatically on completion/error
#endregion

#region Script Start Message
Write-Host "`n========================================="
Write-Host " Generating .NET Core Project Structure"
Write-Host "=========================================`n"
Write-Host "Root directory: `"$RootPath`""
Write-Host "Output file: `"$OutputFile`""
Write-Host "Included file types: `"$($IncludeFileExtensions -join ',')`""
Write-Host "`n"
#endregion

#region Pre-processing: Delete old log and prepare header
Write-Host "Deleting existing log file: `"$OutputFile`""
if (Test-Path $OutputFile) {
    try {
        Remove-Item $OutputFile -Force -ErrorAction Stop
        Write-Host "Old log file deleted."
    } catch {
        Write-Error "ERROR: Failed to delete old log file (`"$OutputFile`"). Check file permissions: $($_.Exception.Message)"
        exit 1
    }
} else {
    Write-Host "Log file `"$OutputFile`" not found. Creating a new one."
}

# Print header to log file
Add-Content -Path $OutputFile -Value "Generating clean .NET Core project file index..."
Add-Content -Path $OutputFile -Value "Root: $RootPath"
Add-Content -Path $OutputFile -Value "Global Exclusions (raw): $($ExcludeGlobalPathsRaw -join ',')"
Add-Content -Path $OutputFile -Value "Do Not Recurse Dirs (raw patterns): $($DoNotRecurseFolderPatternsRaw -join ',')"
Add-Content -Path $OutputFile -Value "Included Extensions: $($IncludeFileExtensions -join ',')"
Add-Content -Path $OutputFile -Value "========================================="
Add-Content -Path $OutputFile -Value "`n" # Blank line
#endregion

#region Main Script Logic (PowerShell)
Write-Host "Building project tree..."

try {
    $absoluteRootPath = (Get-Item -LiteralPath $RootPath).FullName

    # --- Construct Regex Patterns from Raw Inputs ---
    # Escape patterns and join for regex matching
    $finalExcludeGlobalRegex = ($ExcludeGlobalPathsRaw | ForEach-Object {
        $escaped = [regex]::Escape($_.Trim())
        ".*\\$escaped.*" # Match if the path contains the pattern anywhere
    }) -join '|'

    $finalDoNotRecurseRegex = ($DoNotRecurseFolderPatternsRaw | ForEach-Object {
        $trimmed = $_.Trim()
        if ($trimmed -like "*net") {
            $escaped = [regex]::Escape($trimmed)
            "\\$escaped[\d.]+\.\d+$" # Matches '\bin\Debug\net9.0' at the end of a path
        } else {
            $escaped = [regex]::Escape($trimmed)
            "\\$escaped$" # Matches folder name at the end of a path
        }
    }) -join '|'

    # --- Debugging Output (Uncomment for verbose console output during execution) ---
    # Write-Host "PS Debug: Effective Exclude Regex: $finalExcludeGlobalRegex"
    # Write-Host "PS Debug: Effective DoNotRecurse Regex: $finalDoNotRecurseRegex"
    # Write-Host "PS Debug: Processing root: $absoluteRootPath"

    # --- Recursive function to process directory contents ---
    function Process-Directory ($currentPath, $currentPrefix) {
        # Write-Host "PS Debug: Entering directory: $currentPath (Prefix: '$currentPrefix')"

        $items = Get-ChildItem -LiteralPath $currentPath -Force | Where-Object {
            if ($_.FullName -match $finalExcludeGlobalRegex) {
                # Write-Host "PS Debug:   EXCLUDING (Global Match): $($_.FullName)"
                $false
            } else {
                $true
            }
        } | Sort-Object { -not $_.PSIsContainer }, Name # Sort directories first, then files, then alphabetically

        # Write-Host "PS Debug: Found $($items.Count) items to process in $currentPath after global exclusion."

        for ($i = 0; $i -lt $items.Count; $i++) {
            $item = $items[$i]
            $isLastItem = ($i -eq ($items.Count - 1))

            $lineIndicator = if ($isLastItem) {'└─── '} else {'├─── '}
            $nextPrefix = if ($isLastItem) {$currentPrefix + '    '} else {$currentPrefix + '|   '} # REPLACED '│' with '|'

            if ($item.PSIsContainer) {
                Add-Content -Path $OutputFile -Value "$currentPrefix$lineIndicator$($item.Name)/"

                $shouldRecurse = -not ($item.FullName -match $finalDoNotRecurseRegex)
                # Write-Host "PS Debug:   Directory: $($item.FullName)/ (Should Recurse: $shouldRecurse)"

                if ($shouldRecurse) {
                    Process-Directory $item.FullName $nextPrefix
                }
            } else { # It is a file
                if ($IncludeFileExtensions -contains $item.Extension.ToLower()) {
                    Add-Content -Path $OutputFile -Value "$currentPrefix$lineIndicator$($item.Name)"
                    # Write-Host "PS Debug:   File: $($item.FullName) (Included)"
                } #else {
                    # Write-Host "PS Debug:   File: $($item.FullName) (Excluded by extension)"
                #}
            }
        }
    }

    # --- Initial call to start building the tree from the root's children ---
    # Write-Host "PS Debug: Getting top-level items for $absoluteRootPath..."
    $topLevelItems = Get-ChildItem -LiteralPath $absoluteRootPath -Force | Where-Object {
        if ($_.FullName -match $finalExcludeGlobalRegex) {
            # Write-Host "PS Debug:   EXCLUDING (Global Match): $($_.FullName)"
            $false
        } else {
            $true
        }
    } | Sort-Object { -not $_.PSIsContainer }, Name

    # Write-Host "PS Debug: Found $($topLevelItems.Count) top-level items after global exclusion."
    if ($topLevelItems.Count -eq 0) {
        Add-Content -Path $OutputFile -Value "--- No items found after applying filters. Check paths and exclusion patterns. ---"
        # Write-Host "PS Debug: No items found to process for the root directory after filters."
    }

    for ($i = 0; $i -lt $topLevelItems.Count; $i++) {
        $item = $topLevelItems[$i]
        $isLastItem = ($i -eq ($topLevelItems.Count - 1))

        $lineIndicator = if ($isLastItem) {'└─── '} else {'├─── '}
        $nextPrefix = if ($isLastItem) {'    '} else {'|   '} # REPLACED '│' with '|'

        if ($item.PSIsContainer) {
            Add-Content -Path $OutputFile -Value "$lineIndicator$($item.Name)/"
            # Write-Host "PS Debug:   Top-level directory: $($item.Name)/"

            $shouldRecurse = -not ($item.FullName -match $finalDoNotRecurseRegex)
            # Write-Host "PS Debug:     (Should Recurse: $shouldRecurse)"
            if ($shouldRecurse) {
                Process-Directory $item.FullName $nextPrefix
            }
        } else { # It is a file
            if ($IncludeFileExtensions -contains $item.Extension.ToLower()) {
                Add-Content -Path $OutputFile -Value "$lineIndicator$($item.Name)"
                # Write-Host "PS Debug:   Top-level file: $($item.FullName) (Included)"
            } #else {
                # Write-Host "PS Debug:   Top-level file: $($item.FullName) (Excluded by extension)"
            #}
        }
    }
    Write-Host "`nProject structure successfully saved to: `"$OutputFile`"`n"
    exit 0 # Script completed successfully
} catch {
    Write-Error ('An error occurred during PowerShell execution: {0}' -f $_.Exception.Message)
    Write-Host "`nERROR: PowerShell script failed with an error."
    Write-Host "Please check the console output above for details and the log file for partial output."
    exit 1 # Script failed
} finally {
    # This block executes regardless of success or failure
    if ($PauseOnExit) {
        Write-Host "Press any key to exit..."
        $null = Read-Host # Waits for user input
    }
}
#endregion