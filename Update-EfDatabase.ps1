<#
.SYNOPSIS
    Advanced and automated script for managing Entity Framework Core migrations.
    Provides robust control over adding, removing, and applying migrations,
    including dangerous operations for development workflows.
.DESCRIPTION
    This script automates common Entity Framework Core migration tasks.
    It supports:
    - Cleaning and restoring .NET projects.
    - Updating the global dotnet-ef tool.
    - Dangerously dropping and recreating the database (for development purposes, with confirmation).
    - Dangerously recreating the entire Migrations snapshot (useful for a fresh start in development).
    - Adding a new migration with a specified name.
    - Applying pending migrations to the database.
    - Detailed logging of all commands and their outputs.

    NOTE: Operations involving database drops or migration folder deletions are inherently destructive.
    Use with extreme caution, especially outside of controlled development environments.

.PARAMETER MigrationName
    (Required) The name for the new migration. Example: "AddUsersTable".

.PARAMETER InfrastructureProject
    (Required) The path to the project containing your DbContext and Migrations folder.
    This project is typically your Infrastructure or Data layer. Example: "Infrastructure".

.PARAMETER StartupProject
    (Required) The path to the startup project that contains your DbContext configuration
    and is typically runnable (e.g., your Web API or Console app project). Example: "WebAPI".

.PARAMETER ApplyToDatabase
    (Optional) Specifies whether to apply the migration(s) to the database immediately after adding.
    If omitted, only the migration files are generated.

.PARAMETER DropAndRecreateDatabase
    (Optional) EXTREMELY DANGEROUS. If present, the script will drop the *entire* database
    and recreate it. This means ALL DATA will be lost. Use ONLY in development environments.
    Requires interactive confirmation unless -ForceConfirmDrop is also used.

.PARAMETER ForceConfirmDrop
    (Optional) DANGEROUS. Use in conjunction with -DropAndRecreateDatabase to bypass
    the interactive confirmation prompt for dropping the database.
    Use only when you are certain of the consequences (e.g., in automated CI/CD pipelines).

.PARAMETER RemoveLastMigration
    (Optional) If present, the script will remove the last applied migration before
    adding a new one. Useful for cleaning up migration history in development.

.PARAMETER CleanAndRestoreBuild
    (Optional) If present, the script will execute 'dotnet clean' and 'dotnet restore'
    for both the Infrastructure and Startup projects before other operations.
    Helps in resolving potential build issues.

.PARAMETER ForceRecreateAllMigrations
    (Optional) DANGEROUS. If present, the script will delete the entire 'Migrations' folder
    within the Infrastructure project. This effectively resets your local migration history.
    This should generally be followed by -ApplyToDatabase to re-create the database schema from scratch.
    Requires interactive confirmation unless -ForceConfirmAllMigrations is also used.

.PARAMETER ForceConfirmAllMigrations
    (Optional) DANGEROUS. Use in conjunction with -ForceRecreateAllMigrations to bypass
    the interactive confirmation prompt for deleting the Migrations folder.
    Use only when you are certain of the consequences (e.g., in automated CI/CD pipelines).

.PARAMETER UpdateGlobalEfTools
    (Optional) If present, the script will attempt to update the global 'dotnet-ef' tool
    to its latest version. Requires internet connectivity.

.PARAMETER LogTranscriptPath
    (Optional) The full path to a file where the script's execution transcript (full output)
    will be saved. Defaults to 'logs\EfMigration-YYYYMMDD-HHMMSS.log' relative to the script's location.

.EXAMPLE
    # 1. Add a new migration and apply it to the database:
    .\Update-EfDatabase.ps1 -MigrationName "InitialSchema" -InfrastructureProject "MyProject.Data" -StartupProject "MyProject.Api" -ApplyToDatabase

.EXAMPLE
    # 2. Add a new migration without applying it to the database:
    .\Update-EfDatabase.ps1 -MigrationName "AddProducts" -InfrastructureProject "MyProject.Data" -StartupProject "MyProject.Api"

.EXAMPLE
    # 3. DANGEROUS: Fully reset database (development environment scenario):
    #    Cleans, restores, drops DB, recreates ALL migrations, then updates DB.
    #    (Note: -Confirm:$false suppresses overall ShouldProcess prompts)
    .\Update-EfDatabase.ps1 `
        -MigrationName "FreshStart" `
        -InfrastructureProject "Infrastructure" `
        -StartupProject "WebAPI" `
        -CleanAndRestoreBuild `
        -DropAndRecreateDatabase -ForceConfirmDrop `
        -ForceRecreateAllMigrations -ForceConfirmAllMigrations `
        -ApplyToDatabase `
        -LogTranscriptPath "C:\temp\ef_full_reset.log" `
        -Confirm:$false

.EXAMPLE
    # 4. Remove the last migration and add a new one (development fix):
    .\Update-EfDatabase.ps1 `
        -MigrationName "CorrectedFeature" `
        -InfrastructureProject "MyProject.Data" `
        -StartupProject "MyProject.Api" `
        -RemoveLastMigration `
        -ApplyToDatabase

.EXAMPLE
    # 5. Just update global EF tools:
    .\Update-EfDatabase.ps1 -UpdateGlobalEfTools -MigrationName "dummy" -InfrastructureProject "." -StartupProject "." # Migration/project params required, but won't be used if only UpdateGlobalEfTools
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = "AddAndApply")]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Name for the new migration.")]
    [string]$MigrationName,

    [Parameter(Mandatory = $true, HelpMessage = "Path to the project containing the DbContext and Migrations folder.")]
    [string]$InfrastructureProject,

    [Parameter(Mandatory = $true, HelpMessage = "Path to the startup project.")]
    [string]$StartupProject,

    [Parameter(HelpMessage = "If specified, applies the migration to the database.")]
    [switch]$ApplyToDatabase,

    [Parameter(HelpMessage = "DANGEROUS: Drops and recreates the database. Use with -ForceConfirmDrop for no prompt.")]
    [switch]$DropAndRecreateDatabase,

    [Parameter(HelpMessage = "DANGEROUS: Confirms database drop for -DropAndRecreateDatabase without a prompt.")]
    [switch]$ForceConfirmDrop,

    [Parameter(HelpMessage = "If specified, removes the last migration.")]
    [switch]$RemoveLastMigration,

    [Parameter(HelpMessage = "If specified, cleans and restores projects.")]
    [switch]$CleanAndRestoreBuild,

    [Parameter(HelpMessage = "DANGEROUS: Deletes Migrations folder. Use with -ForceConfirmAllMigrations for no prompt.")]
    [switch]$ForceRecreateAllMigrations,

    [Parameter(HelpMessage = "DANGEROUS: Confirms Migrations folder deletion for -ForceRecreateAllMigrations without a prompt.")]
    [switch]$ForceConfirmAllMigrations,

    [Parameter(HelpMessage = "If specified, updates the global dotnet-ef tool.")]
    [switch]$UpdateGlobalEfTools,

    [Parameter(HelpMessage = "Path to save transcript. Defaults to 'logs\EfMigration-YYYYMMDD-HHMMSS.log'.")]
    [string]$LogTranscriptPath = (Join-Path -Path $PSScriptRoot -ChildPath "logs\EfMigration-$((Get-Date).ToString('yyyyMMdd-HHmmss')).log")
)

# --- Global Variables & Setup ---
$ErrorActionPreference = "Stop" # Stop on any error
$ScriptStartTime = Get-Date

$Global:AbsoluteInfrastructurePath = $null
$Global:AbsoluteStartupPath = $null

$LogDir = Split-Path -Path $LogTranscriptPath -Parent
if (-not (Test-Path $LogDir -PathType Container)) {
    Write-Verbose "Creating log directory: $LogDir"
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

Start-Transcript -Path $LogTranscriptPath -Append -Force
Write-Output "======================================================================"
Write-Output "EF Core Migration Management Script Started at $ScriptStartTime"
Write-Output "Script Location: $($PSScriptRoot)"
Write-Output "Parameters received:"
Write-Output "  -MigrationName: $MigrationName"
Write-Output "  -InfrastructureProject: $InfrastructureProject"
Write-Output "  -StartupProject: $StartupProject"
Write-Output "  -ApplyToDatabase: $($ApplyToDatabase.IsPresent)"
Write-Output "  -DropAndRecreateDatabase: $($DropAndRecreateDatabase.IsPresent)"
Write-Output "  -ForceConfirmDrop: $($ForceConfirmDrop.IsPresent)"
Write-Output "  -RemoveLastMigration: $($RemoveLastMigration.IsPresent)"
Write-Output "  -CleanAndRestoreBuild: $($CleanAndRestoreBuild.IsPresent)"
Write-Output "  -ForceRecreateAllMigrations: $($ForceRecreateAllMigrations.IsPresent)"
Write-Output "  -ForceConfirmAllMigrations: $($ForceConfirmAllMigrations.IsPresent)"
Write-Output "  -UpdateGlobalEfTools: $($UpdateGlobalEfTools.IsPresent)"
Write-Output "  -LogTranscriptPath: $LogTranscriptPath"
Write-Output "======================================================================"

# --- Helper Functions ---

function Resolve-ProjectPaths {
    Write-Verbose "Attempting to resolve absolute paths for projects."
    try {
        $Global:AbsoluteInfrastructurePath = (Resolve-Path -LiteralPath $InfrastructureProject -ErrorAction Stop).Path
        $Global:AbsoluteStartupPath = (Resolve-Path -LiteralPath $StartupProject -ErrorAction Stop).Path
        Write-Output "Resolved Infrastructure Project: $AbsoluteInfrastructurePath"
        Write-Output "Resolved Startup Project: $AbsoluteStartupPath"
    } catch {
        Write-Error "CRITICAL: Error resolving project paths. Ensure paths are correct. Infra='$InfrastructureProject', Startup='$StartupProject'. Error: $($_.Exception.Message)";
        throw "PathResolutionFailed"
    }
    Write-Verbose "Project paths successfully resolved."
}

# Invokes dotnet CLI commands and captures output
function Invoke-DotNetCommand {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$ActionDescription
    )
    $args = @($Command, $ProjectPath) # Arguments for dotnet.exe
    $filePath = "dotnet"

    Write-Output "Executing: $filePath $($args -join ' ')" # Log full command

    try {
        # Using a ProcessStartInfo object for fine-grained control
        $processInfo = New-Object System.Diagnostics.ProcessStartInfo
        $processInfo.FileName = $filePath
        # ArgumentList is safer for complex arguments or those with spaces/quotes
        $processInfo.Arguments = "$Command `"$ProjectPath`"" # String-formatted arguments
        # If the argument handling with Process.Arguments causes issues, use Process.ArgumentList instead like this:
        # $processInfo.ArgumentList.Add($Command)
        # $processInfo.ArgumentList.Add($ProjectPath)

        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $processInfo.UseShellExecute = $false # Essential for redirection
        $processInfo.CreateNoWindow = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $processInfo

        $process.Start() | Out-Null # Start the process without creating a new window

        # Read the output streams
        $stdoutContent = $process.StandardOutput.ReadToEnd()
        $stderrContent = $process.StandardError.ReadToEnd()
        $process.WaitForExit() # Wait for the process to complete

        $exitCode = $process.ExitCode

        if ($exitCode -ne 0) {
            Write-Error "Failed to $ActionDescription for project '$ProjectPath'. Exit Code: $exitCode."
            if (-not [string]::IsNullOrEmpty($stdoutContent.Trim())) { Write-Error "STDOUT:`n$stdoutContent" }
            if (-not [string]::IsNullOrEmpty($stderrContent.Trim())) { Write-Error "STDERR:`n$stderrContent" }
            throw "DotNetCommandFailed"
        } else {
            Write-Output "$ActionDescription for '$ProjectPath' completed successfully."
            if (-not [string]::IsNullOrEmpty($stdoutContent.Trim())) { Write-Verbose "STDOUT:`n$stdoutContent" }
            if (-not [string]::IsNullOrEmpty($stderrContent.Trim())) { Write-Warning "STDERR (but exit code 0):`n$stderrContent" } # Warnings are possible with 0 exit code
        }
    } catch {
        Write-Error "Error executing dotnet command: '$Command' on '$ProjectPath'. PowerShell Error: $($_.Exception.Message)";
        throw "DotNetCommandFailed"
    }
}

# Invokes dotnet ef CLI commands and captures output
function Invoke-DotNetEfCliCommand {
    param (
        [Parameter(Mandatory = $true)]
        [string]$EfArguments,

        [Parameter(Mandatory = $true)]
        [string]$SuccessMessage,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessagePrefix
    )
    # The full string passed as arguments to 'dotnet ef'
    $fullArgumentsString = "ef $EfArguments --project `"$AbsoluteInfrastructurePath`" --startup-project `"$AbsoluteStartupPath`" --verbose"
    $filePath = "dotnet"

    Write-Output "Executing: $filePath $fullArgumentsString" # Log full command

    try {
        $processInfo = New-Object System.Diagnostics.ProcessStartInfo
        $processInfo.FileName = $filePath
        $processInfo.Arguments = $fullArgumentsString # Pass the combined string as arguments
        # Again, consider ArgumentList = @("ef") + ($EfArguments.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) + ... if problems arise.
        
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $processInfo

        $process.Start() | Out-Null

        $stdoutContent = $process.StandardOutput.ReadToEnd()
        $stderrContent = $process.StandardError.ReadToEnd()
        $process.WaitForExit()

        $exitCode = $process.ExitCode
        
        # Determine if exit code 0 means warnings or a success
        $isWarningOnly = $exitCode -eq 0 -and (-not [string]::IsNullOrEmpty($stderrContent.Trim()) -and ($stderrContent -match "(warn|warning)" -and -not ($stderrContent -match "(error|fail)")) );

        if ($exitCode -ne 0 -and -not $isWarningOnly) {
            Write-Error "$FailureMessagePrefix. EF Core command failed. Exit Code: $exitCode."
            if (-not [string]::IsNullOrEmpty($stdoutContent.Trim())) { Write-Error "STDOUT:`n$stdoutContent" }
            if (-not [string]::IsNullOrEmpty($stderrContent.Trim())) { Write-Error "STDERR:`n$stderrContent" }
            throw "EfCliCommandFailed"
        } else {
            Write-Output $SuccessMessage;
            if (-not [string]::IsNullOrEmpty($stdoutContent.Trim())) { Write-Verbose "STDOUT:`n$stdoutContent" }
            if (-not [string]::IsNullOrEmpty($stderrContent.Trim())) { Write-Warning "STDERR (warnings or non-critical info, but Exit Code 0):`n$stderrContent" }
        }
    } catch {
        Write-Error "Error executing dotnet ef command: '$EfArguments'. PowerShell Error: $($_.Exception.Message)";
        throw "EfCliCommandFailed"
    }
}

# --- Main Script Execution Logic ---
try {
    # This 'if' block handles the CmdletBinding's ShouldProcess confirmation.
    if ($PSCmdlet.ShouldProcess("EF Core Migration Management Script", "Start full execution flow")) {

        # Resolve absolute project paths (First actual step)
        Resolve-ProjectPaths

        # 1. Optionally update global dotnet-ef tool
        if ($UpdateGlobalEfTools.IsPresent) {
            if ($PSCmdlet.ShouldProcess("global dotnet-ef tool", "Update")) {
                Write-Output "Attempting to update global dotnet-ef tool..."
                # & dotnet tool update --global dotnet-ef should be run without RedirectStandardOutput for simplicity for now
                # Or re-implement its capture with new reliable method.
                # For simplicity here, calling directly & for non-captured output, and just check $LASTEXITCODE.
                # You might need to redirect output to Null or file if it causes hanging for your specific PS version.
                try {
                     & dotnet tool update --global dotnet-ef *> $null
                } catch {
                    Write-Warning "Failed to execute 'dotnet tool update'. Error: $($_.Exception.Message). Check global tool installation.";
                    # Don't throw here, as update is optional and often just warnings
                }

                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "dotnet tool update command finished with exit code $LASTEXITCODE. This might mean the tool is already up-to-date or a non-critical error occurred. See separate PowerShell output/console for details."
                } else {
                    Write-Output "dotnet-ef tool updated successfully."
                }
            }
        }

        # 2. Optionally clean and restore projects
        if ($CleanAndRestoreBuild.IsPresent) {
            if ($PSCmdlet.ShouldProcess("projects ($InfrastructureProject, $StartupProject)", "clean and restore")) {
                Write-Output "Performing clean and restore operation for projects..."
                Invoke-DotNetCommand -Command "clean" -ProjectPath $AbsoluteInfrastructurePath -ActionDescription "Clean Infrastructure project"
                Invoke-DotNetCommand -Command "clean" -ProjectPath $AbsoluteStartupPath -ActionDescription "Clean Startup project"
                Invoke-DotNetCommand -Command "restore" -ProjectPath $AbsoluteInfrastructurePath -ActionDescription "Restore Infrastructure project"
                Invoke-DotNetCommand -Command "restore" -ProjectPath $AbsoluteStartupPath -ActionDescription "Restore Startup project"
                Write-Output "Clean and restore completed for projects."
            }
        }

        # 3. Handle dangerous database drop and recreation
        if ($DropAndRecreateDatabase.IsPresent) {
            Write-Warning "DANGEROUS OPERATION: '-DropAndRecreateDatabase' specified. This will drop the ENTIRE database."
            $proceedWithDbDrop = $false
            if ($ForceConfirmDrop.IsPresent) {
                Write-Warning "'-ForceConfirmDrop' switch is present. Proceeding with database drop without manual confirmation."
                if ($PSCmdlet.ShouldProcess("the ENTIRE database", "Drop and Recreate (auto-confirmed)")) {
                    $proceedWithDbDrop = $true
                }
            } else {
                $dbConfirmationPrompt = "CRITICAL: Are you absolutely sure you want to DROP and RECREATE the entire database? ALL DATA WILL BE LOST AND CANNOT BE RECOVERED. (Type 'yes_drop_my_database' to confirm)"
                $dbConfirmation = Read-Host $dbConfirmationPrompt
                if ($dbConfirmation -eq 'yes_drop_my_database') {
                    if ($PSCmdlet.ShouldProcess("the ENTIRE database", "Drop and Recreate")) {
                        $proceedWithDbDrop = $true
                    }
                }
            }

            if ($proceedWithDbDrop) {
                Write-Output "Dropping database..."
                Invoke-DotNetEfCliCommand -EfArguments "database drop --force" `
                                          -SuccessMessage "Database dropped successfully (or did not exist)." `
                                          -FailureMessagePrefix "Failed to drop database"
                Write-Output "Database will be recreated by the 'database update' command if -ApplyToDatabase is used and no previous migration exists."
            } else {
                throw "Database drop operation cancelled by user. Aborting script."
            }
        }

        # 4. Handle dangerous full migration reset (deleting Migrations folder)
        $migrationsFolder = Join-Path -Path $AbsoluteInfrastructurePath -ChildPath "Migrations"
        if ($ForceRecreateAllMigrations.IsPresent) {
            Write-Warning "DANGEROUS OPERATION: '-ForceRecreateAllMigrations' specified. This will delete the entire '$migrationsFolder' folder."
            $proceedWithSnapshotDelete = $false
            if ($ForceConfirmAllMigrations.IsPresent) {
                Write-Warning "'-ForceConfirmAllMigrations' switch is present. Proceeding with Migrations folder deletion without manual confirmation."
                if ($PSCmdlet.ShouldProcess("the ENTIRE '$migrationsFolder' folder", "Delete recursively (auto-confirmed) to reset migration history")) {
                    $proceedWithSnapshotDelete = $true
                }
            } else {
                $snapshotConfirmationPrompt = "CRITICAL: Are you absolutely sure you want to delete '$migrationsFolder' and reset all migrations? This CANNOT BE UNDONE for local files. (Type 'yes_delete_migrations_folder' to confirm)"
                $snapshotConfirmation = Read-Host $snapshotConfirmationPrompt
                if ($snapshotConfirmation -eq 'yes_delete_migrations_folder') {
                    if ($PSCmdlet.ShouldProcess("the ENTIRE '$migrationsFolder' folder", "Delete recursively to reset migration history")) {
                        $proceedWithSnapshotDelete = $true
                    }
                }
            }

            if ($proceedWithSnapshotDelete) {
                if (Test-Path $migrationsFolder) {
                    Write-Output "Deleting existing Migrations folder: $migrationsFolder"
                    Remove-Item -Path $migrationsFolder -Recurse -Force
                    Write-Output "Migrations folder deleted."
                } else { Write-Output "Migrations folder '$migrationsFolder' does not exist. No action needed." }
                Write-Output "A new initial migration will be created, effectively resetting the database schema creation history."
            } else {
                throw "Deletion of Migrations folder cancelled by user. Aborting script."
            }
        }

        # 5. Optionally remove the last migration
        if ($RemoveLastMigration.IsPresent -and -not $ForceRecreateAllMigrations.IsPresent) {
            if ($PSCmdlet.ShouldProcess("the last EF Core Migration", "Remove operation")) {
                Write-Output "Attempting to remove the last EF Core migration..."
                Invoke-DotNetEfCliCommand -EfArguments "migrations remove" `
                                          -SuccessMessage "Last migration removed successfully (if any)." `
                                          -FailureMessagePrefix "Failed to remove the last migration"
            }
        }

        # 6. Add the new migration
        if ($PSCmdlet.ShouldProcess("a new EF Core migration named '$MigrationName'", "Add operation")) {
            Write-Output "Adding new migration: '$MigrationName'..."
            Invoke-DotNetEfCliCommand -EfArguments "migrations add `"$MigrationName`"" `
                                      -SuccessMessage "Migration '$MigrationName' added successfully." `
                                      -FailureMessagePrefix "Failed to add migration '$MigrationName'"
        }

        # 7. Optionally apply the migration to the database
        if ($ApplyToDatabase.IsPresent) {
            if ($PSCmdlet.ShouldProcess("the target database", "Update with the latest migration(s)")) {
                Write-Output "Applying migration(s) to the database..."
                Invoke-DotNetEfCliCommand -EfArguments "database update" `
                                          -SuccessMessage "Database updated successfully using latest migration(s)." `
                                          -FailureMessagePrefix "Failed to update database"
            }
        } else {
            Write-Warning "Migration '$MigrationName' added. Review it and commit to version control. To apply changes to the database, re-run the script with '-ApplyToDatabase' or update manually."
        }
    }
}
catch {
    Write-Error "A CRITICAL ERROR occurred during the migration process: $($_.Exception.Message)"
    Write-Error "The script attempted to stop on the first error. Please review the transcript log at '$LogTranscriptPath' for detailed information."
    Write-Error "Script execution ABORTED. Exit code: 1"
    exit 1 # Ensure script exits with an error code
}
finally {
    Write-Output "Script execution FINISHED at $(Get-Date)."
    Write-Output "======================================================================"
    Stop-Transcript # Always stop transcript in finally block
    Write-Output "Transcript saved to: $LogTranscriptPath"
}