<#
.SYNOPSIS
    Advanced and automated script for managing Entity Framework Core migrations.
.DESCRIPTION
    Automates adding EF Core migrations and updating databases with enhanced control and logging.
    Features:
    - Optional cleaning and restoring of projects.
    - Optional EF Core tools update.
    - Optionally drops and recreates the entire database (EXTREMELY DANGEROUS - for development only).
    - Forceful recreation of the Migrations snapshot (DANGEROUS - use with -ForceConfirmDelete for full automation).
    - Removal of the previous migration.
    - Addition of a new migration.
    - Application of migrations to the database.
    - Detailed transcript logging.
    - Robust error checking.
.PARAMETER MigrationName
    (Required) The name for the new migration.
.PARAMETER InfrastructureProject
    (Required) Path to the project containing the DbContext and Migrations folder.
.PARAMETER StartupProject
    (Required) Path to the startup project.
.PARAMETER ApplyToDatabase
    (Optional) Switch to apply the migration to the database.
.PARAMETER DropAndRecreateDatabase
    (Optional) EXTREMELY DANGEROUS: Drops the entire database and recreates it before applying migrations.
    ONLY FOR DEVELOPMENT ENVIRONMENTS. Requires confirmation or -ForceConfirmDrop.
.PARAMETER ForceConfirmDrop
    (Optional) DANGEROUS: Confirms database drop for -DropAndRecreateDatabase without a prompt.
.PARAMETER RemovePreviousMigration
    (Optional) Switch to remove the last migration before adding the new one.
.PARAMETER CleanBuild
    (Optional) Switch to perform 'dotnet clean' and 'dotnet restore'.
.PARAMETER ForceRecreateSnapshot
    (Optional) DANGEROUS: Deletes the Migrations folder. For full automation, also use -ForceConfirmDeleteSnapshot.
.PARAMETER ForceConfirmDeleteSnapshot
    (Optional) DANGEROUS: Confirms Migrations folder deletion for -ForceRecreateSnapshot without a prompt.
.PARAMETER UpdateEfTools
    (Optional) Switch to update the global dotnet-ef tool.
.PARAMETER LogTranscriptPath
    (Optional) Path to save the execution transcript.
.EXAMPLE
    # DANGEROUS: Fully automated reset: clean, drop DB, recreate snapshot, add migration, update DB
    .\Update-EfDatabase.ps1 -MigrationName "FullResetDev" -InfrastructureProject "Infrastructure" -StartupProject "WebAPI" -CleanBuild -DropAndRecreateDatabase -ForceConfirmDrop -ForceRecreateSnapshot -ForceConfirmDeleteSnapshot -ApplyToDatabase -Confirm:$false
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Name for the new migration.")]
    [string]$MigrationName,

    [Parameter(Mandatory = $true, HelpMessage = "Path to the Infrastructure/Data project.")]
    [string]$InfrastructureProject,

    [Parameter(Mandatory = $true, HelpMessage = "Path to the Startup project.")]
    [string]$StartupProject,

    [Parameter(HelpMessage = "If specified, applies the migration to the database.")]
    [switch]$ApplyToDatabase,

    [Parameter(HelpMessage = "DANGEROUS: Drops and recreates the database. Use with -ForceConfirmDrop for no prompt.")]
    [switch]$DropAndRecreateDatabase, # ✅ پارامتر جدید

    [Parameter(HelpMessage = "DANGEROUS: Confirms database drop for -DropAndRecreateDatabase without a prompt.")]
    [switch]$ForceConfirmDrop,      # ✅ پارامتر جدید

    [Parameter(HelpMessage = "If specified, removes the last migration.")]
    [switch]$RemovePreviousMigration,

    [Parameter(HelpMessage = "If specified, cleans and restores projects.")]
    [switch]$CleanBuild,

    [Parameter(HelpMessage = "DANGEROUS: Deletes Migrations folder. Use with -ForceConfirmDeleteSnapshot for no prompt.")]
    [switch]$ForceRecreateSnapshot,

    [Parameter(HelpMessage = "DANGEROUS: Confirms Migrations folder deletion for -ForceRecreateSnapshot without a prompt.")]
    [switch]$ForceConfirmDeleteSnapshot, # ✅ تغییر نام برای وضوح بیشتر

    [Parameter(HelpMessage = "If specified, updates dotnet-ef tool.")]
    [switch]$UpdateEfTools,

    [Parameter(HelpMessage = "Path to save transcript. Defaults to 'logs\EfMigration-YYYYMMDD-HHMMSS.log'.")]
    [string]$LogTranscriptPath = "logs\EfMigration-$((Get-Date).ToString('yyyyMMdd-HHmmss')).log"
)

# --- Global Variables & Setup ---
$ErrorActionPreference = "Stop"
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
Write-Output "Parameters:"
Write-Output "  MigrationName: $MigrationName"
Write-Output "  InfrastructureProject: $InfrastructureProject"
Write-Output "  StartupProject: $StartupProject"
Write-Output "  ApplyToDatabase: $($ApplyToDatabase.IsPresent)"
Write-Output "  DropAndRecreateDatabase: $($DropAndRecreateDatabase.IsPresent)"
Write-Output "  ForceConfirmDrop: $($ForceConfirmDrop.IsPresent)"
Write-Output "  RemovePreviousMigration: $($RemovePreviousMigration.IsPresent)"
Write-Output "  CleanBuild: $($CleanBuild.IsPresent)"
Write-Output "  ForceRecreateSnapshot: $($ForceRecreateSnapshot.IsPresent)"
Write-Output "  ForceConfirmDeleteSnapshot: $($ForceConfirmDeleteSnapshot.IsPresent)"
Write-Output "  UpdateEfTools: $($UpdateEfTools.IsPresent)"
Write-Output "Logging transcript to: $LogTranscriptPath"
Write-Output "======================================================================"

# --- Helper Functions (Resolve-ProjectPaths, Invoke-DotNetCommand, Invoke-DotNetEfCliCommand - بدون تغییر عمده) ---
function Resolve-ProjectPaths {
    Write-Verbose "Resolving project paths..."
    try {
        $Global:AbsoluteInfrastructurePath = (Resolve-Path -LiteralPath $InfrastructureProject -ErrorAction Stop).Path
        $Global:AbsoluteStartupPath = (Resolve-Path -LiteralPath $StartupProject -ErrorAction Stop).Path
        Write-Output "Using Infrastructure Project: $AbsoluteInfrastructurePath"
        Write-Output "Using Startup Project: $AbsoluteStartupPath"
    } catch { Write-Error "Error resolving project paths: Infra='$InfrastructureProject', Startup='$StartupProject'. Error: $($_.Exception.Message)"; throw }
    Write-Verbose "Project paths resolved."
}
function Invoke-DotNetCommand { param ([string]$Command, [string]$ProjectPath, [string]$ActionDescription)
    $FullCommand = "dotnet $Command `"$ProjectPath`""; Write-Output "Executing: $FullCommand"
    $output = Invoke-Expression "$FullCommand 2>&1 | Out-String"; if ($LASTEXITCODE -ne 0) { Write-Error "Failed to $ActionDescription for project '$ProjectPath'. Code: $LASTEXITCODE. Output:`n$output"; throw "Cmd failed" }
    else { Write-Output "$ActionDescription for '$ProjectPath' OK."; if ($output.Trim()) { Write-Verbose "Output:`n$output" } }
}
function Invoke-DotNetEfCliCommand { param ([string]$EfArguments, [string]$SuccessMessage, [string]$FailureMessagePrefix)
    $FullEfCommand = "dotnet ef $EfArguments --project `"$AbsoluteInfrastructurePath`" --startup-project `"$AbsoluteStartupPath`" --verbose"
    Write-Output "Executing: $FullEfCommand"; $tmpOut = "$PWD\ef_out.tmp"; $tmpErr = "$PWD\ef_err.tmp"
    try { $proc = Start-Process "dotnet" -Arg $FullEfCommand -NoNewWindow -Wait -PassThru -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr; $exitCode = $proc.ExitCode }
    catch { Write-Error "$FailureMessagePrefix. PowerShell error: $($_.Exception.Message)"; throw }
    $stdout = Get-Content $tmpOut -EA SilentlyContinue | Out-String; $stderr = Get-Content $tmpErr -EA SilentlyContinue | Out-String
    Remove-Item $tmpOut, $tmpErr -EA SilentlyContinue
    if ($exitCode -ne 0) { Write-Error "$FailureMessagePrefix. EF Core cmd failed (Code $exitCode)."; if ($stdout.Trim()) { Write-Error "STDOUT:`n$stdout" }; if ($stderr.Trim()) { Write-Error "STDERR:`n$stderr" }; throw "EF cmd failed" }
    else { Write-Output $SuccessMessage; if ($stdout.Trim()) { Write-Verbose "STDOUT:`n$stdout" }; if ($stderr.Trim()) { Write-Warning "STDERR (but ExitCode 0):`n$stderr" } }
}
# --- End Helper Functions ---

try {
    Resolve-ProjectPaths

    # 1. Optionally update EF Core tools
    if ($UpdateEfTools.IsPresent) {
        if ($PSCmdlet.ShouldProcess("dotnet-ef tool", "Update globally")) {
            Write-Output "Attempting to update global dotnet-ef tool..."
            & dotnet tool update --global dotnet-ef
            if ($LASTEXITCODE -ne 0) { Write-Warning "dotnet tool update cmd finished (Code $LASTEXITCODE). May be up-to-date or error." }
            else { Write-Output "dotnet-ef tool update executed." }
        }
    }

    # 2. Optionally clean and restore projects
    if ($CleanBuild.IsPresent) {
        if ($PSCmdlet.ShouldProcess("projects ($AbsoluteInfrastructurePath, $AbsoluteStartupPath)", "Clean and Restore")) {
            Write-Output "Performing clean build..."
            Invoke-DotNetCommand -Command "clean" -ProjectPath $AbsoluteInfrastructurePath -ActionDescription "Clean Infra"
            Invoke-DotNetCommand -Command "clean" -ProjectPath $AbsoluteStartupPath -ActionDescription "Clean Startup"
            Invoke-DotNetCommand -Command "restore" -ProjectPath $AbsoluteInfrastructurePath -ActionDescription "Restore Infra"
            Invoke-DotNetCommand -Command "restore" -ProjectPath $AbsoluteStartupPath -ActionDescription "Restore Startup"
            Write-Output "Clean build completed."
        }
    }

    # 3. Optionally drop and recreate the database (EXTREMELY DANGEROUS)
    if ($DropAndRecreateDatabase.IsPresent) {
        Write-Warning "ACTION: -DropAndRecreateDatabase specified. The ENTIRE database will be dropped and recreated."
        $proceedWithDbDrop = $false
        if ($ForceConfirmDrop.IsPresent) {
            Write-Warning "'-ForceConfirmDrop' switch is present. Bypassing manual 'yes' confirmation for dropping database."
            if ($PSCmdlet.ShouldProcess("the ENTIRE database", "Drop and Recreate (AUTO-CONFIRMED VIA -ForceConfirmDrop)")) {
                $proceedWithDbDrop = $true
            }
        } else {
            $dbConfirmation = Read-Host "CRITICAL: Are you absolutely sure you want to DROP and RECREATE the entire database? ALL DATA WILL BE LOST. (Type 'yes_drop_my_database' to confirm)"
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
            Write-Output "Database will be recreated by the 'database update' command if -ApplyToDatabase is used."
        } else {
            throw "User cancelled database drop operation or confirmation was not provided. Aborting."
        }
    }

    # 4. Optionally force recreate snapshot (DANGEROUS)
    $migrationsFolder = Join-Path -Path $AbsoluteInfrastructurePath -ChildPath "Migrations"
    if ($ForceRecreateSnapshot.IsPresent) {
        Write-Warning "ACTION: -ForceRecreateSnapshot specified. The ENTIRE '$migrationsFolder' folder will be deleted."
        $proceedWithSnapshotDelete = $false
        if ($ForceConfirmDeleteSnapshot.IsPresent) {
            Write-Warning "'-ForceConfirmDeleteSnapshot' switch present. Bypassing manual 'yes' for deleting Migrations folder."
            if ($PSCmdlet.ShouldProcess($migrationsFolder, "Delete recursively (AUTO-CONFIRMED VIA -ForceConfirmDeleteSnapshot) to reset migration history")) {
                $proceedWithSnapshotDelete = $true
            }
        } else {
            $snapshotConfirmation = Read-Host "CRITICAL: Are you absolutely sure you want to delete '$migrationsFolder' and reset all migrations? This cannot be undone for local files. (Type 'yes_delete_migrations_folder' to confirm)"
            if ($snapshotConfirmation -eq 'yes_delete_migrations_folder') {
                if ($PSCmdlet.ShouldProcess($migrationsFolder, "Delete recursively to reset migration history")) {
                     $proceedWithSnapshotDelete = $true
                }
            }
        }

        if ($proceedWithSnapshotDelete) {
            if (Test-Path $migrationsFolder) {
                Write-Output "Deleting existing Migrations folder: $migrationsFolder"
                Remove-Item -Path $migrationsFolder -Recurse -Force
                Write-Output "Migrations folder deleted."
            } else { Write-Output "Migrations folder '$migrationsFolder' does not exist." }
            Write-Output "A new initial migration will be created."
        } else {
            throw "User cancelled deletion of Migrations folder or confirmation was not provided. Aborting."
        }
    }

    # 5. Optionally remove the previous migration (if not forcing recreate and if switch is present)
    if ($RemovePreviousMigration.IsPresent -and -not $ForceRecreateSnapshot.IsPresent) {
        if ($PSCmdlet.ShouldProcess("the last EF Core Migration (if any)", "Remove operation")) {
            Invoke-DotNetEfCliCommand -EfArguments "migrations remove" `
                                      -SuccessMessage "Previous migration (if any) removed." `
                                      -FailureMessagePrefix "Failed to remove previous migration"
        }
    }

    # 6. Add the new migration
    # ShouldProcess is implicitly handled by CmdletBinding for the function
    Invoke-DotNetEfCliCommand -EfArguments "migrations add `"$MigrationName`"" `
                              -SuccessMessage "Migration '$MigrationName' added to '$AbsoluteInfrastructurePath'." `
                              -FailureMessagePrefix "Failed to add migration '$MigrationName'"


    # 7. Optionally apply the migration to the database
    if ($ApplyToDatabase.IsPresent) {
        if ($PSCmdlet.ShouldProcess("the target database", "Update with the latest migration(s)")) {
            Invoke-DotNetEfCliCommand -EfArguments "database update" `
                                      -SuccessMessage "Database updated successfully using latest migration(s)." `
                                      -FailureMessagePrefix "Failed to update database"
        }
    } else {
        Write-Warning "Migration '$MigrationName' added. Review it. To apply, re-run with -ApplyToDatabase or update manually."
    }

    Write-Output "======================================================================"
    Write-Output "EF Core Migration Process Completed Successfully at $(Get-Date)."
    Write-Output "======================================================================"
}
catch {
    Write-Error "A CRITICAL ERROR occurred during the migration process: $($_.Exception.Message)"
    Write-Error "Please review the transcript log '$LogTranscriptPath' and the script output for details."
    Write-Error "Script execution ABORTED."
    exit 1 # Ensure script exits with an error code for CI/CD or other automation
}
finally {
    Write-Output "Stopping transcript..."
    Stop-Transcript
}