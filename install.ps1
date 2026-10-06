<#
.SYNOPSIS
  ForexTradingBot — One-Command Installer for Windows (PowerShell)

.DESCRIPTION
  Run this in PowerShell (no clone needed):

      irm https://raw.githubusercontent.com/Opselon/ForexTradingBot/master/install.ps1 | iex

  What it does:
    1) Clones (or updates) the repository to %USERPROFILE%\ForexTradingBot
    2) Asks: Docker install (recommended) or Normal install (.NET on this PC)
    3) Docker  -> builds the image, starts PostgreSQL + Redis + API, waits for /healthz
    4) Normal  -> installs the .NET 9 SDK if missing, then starts the app; on FIRST
                  STARTUP the app itself asks PostgreSQL / SQLite / SQL Server and
                  saves the answer to the Easy Setup config

  Everything is idempotent — re-running is safe.

.NOTES
  If script execution is blocked, run once:
      Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
#>

$ErrorActionPreference = 'Stop'

$RepoUrl = 'https://github.com/Opselon/ForexTradingBot.git'
$Dir     = if ($env:FOREXBOT_DIR) { $env:FOREXBOT_DIR } else { Join-Path $HOME 'ForexTradingBot' }
$HealthDocker = 'http://localhost:8080/healthz'
$HealthNative = 'http://localhost:5000/healthz'

function Write-Step($msg) { Write-Host ''; Write-Host "▶ $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  ! $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "  ✗ $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

function Show-Banner {
    Write-Host ''
    Write-Host '  ForexTradingBot — One-command installer' -ForegroundColor Blue
    Write-Host '  PostgreSQL / SQLite / SQL Server' -ForegroundColor DarkGray
    Write-Host ''
}

function Test-Prereqs {
    Write-Step 'Prerequisites'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Err 'Git not found.'
        Write-Info 'Install it first: winget install Git.Git   (then reopen PowerShell)'
        exit 1
    }
    Write-Ok 'git found'
}

function Get-Source {
    Write-Step 'Source code'
    if (Test-Path (Join-Path $Dir '.git')) {
        Write-Info "Existing clone at $Dir — updating..."
        try {
            git -C $Dir pull --ff-only 2>$null | Out-Null
            Write-Ok 'Repository updated'
        } catch {
            Write-Warn 'pull failed — using local copy'
        }
    } else {
        Write-Info "Cloning $RepoUrl ..."
        git clone --depth 1 $RepoUrl $Dir | Out-Null
        Write-Ok "Cloned to: $Dir"
    }
}

function Get-DockerComposeCommand {
    if ((Get-Command docker -ErrorAction SilentlyContinue) -and
        (docker compose version 2>$null)) { return 'docker compose' }
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) { return 'docker-compose' }
    return $null
}

function Invoke-DockerInstall {
    Write-Step 'Docker environment'
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Err 'Docker is not installed.'
        Write-Info 'Install Docker Desktop: https://docs.docker.com/desktop/setup/install/windows-install/'
        exit 1
    }
    $compose = Get-DockerComposeCommand
    if (-not $compose) {
        Write-Err 'Docker Compose not found (install the compose plugin).'
        exit 1
    }
    try { docker info 2>$null | Out-Null }
    catch { }
    if ($LASTEXITCODE -ne 0) {
        Write-Err 'Docker daemon is not running. Start Docker Desktop and re-run this script.'
        exit 1
    }
    Write-Ok 'Docker + Compose ready'

    Push-Location $Dir
    try {
        Write-Step 'Configuration (.env)'
        $envFile = Join-Path $Dir '.env'
        if (-not (Test-Path $envFile)) {
            Copy-Item (Join-Path $Dir '.env.example') $envFile
            $bytes = New-Object byte[] 24
            [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            $dbPass = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
            # Write the generated value via a here-string so the random password
            # never appears literally in this file's source (avoids secret scanners).
            $content = Get-Content $envFile -Raw
            $line = "POSTGRES_PASSWORD=$dbPass"
            if ($content -match '(?m)^POSTGRES_PASSWORD=.*$') {
                $content = [regex]::Replace($content, '(?m)^POSTGRES_PASSWORD=.*$', { $line })
            } else {
                $content += "`n$line`n"
            }
            Set-Content -Path $envFile -Value $content -Encoding UTF8
            Write-Ok '.env created with an auto-generated database password'
            Write-Warn "Edit $Dir\.env later to add TELEGRAM_BOT_TOKEN (optional — the app runs without it)"
        } else {
            Write-Ok '.env already exists — kept as-is'
        }

        Write-Step 'Building & starting containers (PostgreSQL, Redis, API)'
        Write-Info 'First build takes a few minutes; later runs use the cache.'
        Invoke-Expression "$compose up -d --build"
        if ($LASTEXITCODE -ne 0) { throw 'compose up failed' }

        Write-Step 'Waiting for health check'
        $healthy = $false
        $body = ''
        for ($i = 0; $i -lt 60; $i++) {
            try {
                $resp = Invoke-WebRequest -Uri $HealthDocker -TimeoutSec 3 -UseBasicParsing
                $body = $resp.Content
                if ($body -match 'healthy') { $healthy = $true; break }
            } catch { Start-Sleep -Seconds 3 }
        }

        Write-Host ''
        if ($healthy) {
            Write-Ok "API is UP and healthy → $HealthDocker"
            Write-Host "    $body" -ForegroundColor Green
            Write-Host ''
            Write-Host 'Next steps' -ForegroundColor White
            Write-Info "Logs    : $compose logs -f forex-trading-bot-app"
            Write-Info "Stop    : $compose down"
            Write-Info "Restart : $compose up -d"
            Write-Info "Config  : $Dir\.env"
        } else {
            Write-Err 'API did not become healthy within the timeout.'
            Write-Info "Inspect with: $compose logs forex-trading-bot-app"
            exit 1
        }
    } finally { Pop-Location }
}

function Install-DotNetSdk {
    Write-Step '.NET SDK'
    $sdk = $null
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $sdk = dotnet --list-sdks 2>$null | Where-Object { $_ -match '^9\.' } | Select-Object -First 1
    }
    if (-not $sdk) {
        Write-Warn '.NET 9 SDK not found — installing to %USERPROFILE%\.dotnet'
        $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Channel 9 -InstallDir (Join-Path $HOME '.dotnet')
        Remove-Item $installer -ErrorAction SilentlyContinue
        $dotnetRoot = Join-Path $HOME '.dotnet'
        $env:DOTNET_ROOT = $dotnetRoot
        $env:Path = "$dotnetRoot;$env:Path"
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRoot, 'User')
        [Environment]::SetEnvironmentVariable('Path', "$dotnetRoot;$([Environment]::GetEnvironmentVariable('Path','User'))", 'User')
        Write-Ok '.NET 9 SDK installed'
    } else {
        Write-Ok ".NET SDK found: $sdk"
    }
}

function Invoke-NativeInstall {
    Install-DotNetSdk

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    Push-Location $Dir
    try {
        Write-Step 'Building (Release)'
        dotnet build -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }
        Write-Ok 'Build succeeded'

        Write-Step 'Starting the application'
        Write-Host ''
        Write-Host '  +----------------------------------------------------------+' -ForegroundColor White
        Write-Host '  |  First startup - choose your database                    |' -ForegroundColor White
        Write-Host '  |                                                          |' -ForegroundColor White
        Write-Host '  |  The app will now ask:                                   |' -ForegroundColor White
        Write-Host '  |    1) SQLite       (zero setup, single file)             |' -ForegroundColor White
        Write-Host '  |    2) PostgreSQL   (server, recommended for production)  |' -ForegroundColor White
        Write-Host '  |    3) SQL Server                                            |' -ForegroundColor White
        Write-Host '  |                                                          |' -ForegroundColor White
        Write-Host '  |  Your choice is saved to the Easy Setup config          |' -ForegroundColor White
        Write-Host '  +----------------------------------------------------------+' -ForegroundColor White
        Write-Host ''
        Write-Info 'Health check: ' -NoNewline
        Write-Info $HealthNative
        Write-Host ''
        dotnet run --project WebAPI -c Release
    } finally { Pop-Location }
}

# ---------------- main ----------------
Show-Banner
Test-Prereqs
Get-Source

$mode = $args | Select-Object -First 1
if (-not $mode) {
    Write-Host 'How do you want to install?' -ForegroundColor White
    Write-Host '  1) Docker   - PostgreSQL + Redis + API, fully automated (recommended)' -ForegroundColor Green
    Write-Host '  2) Normal   - .NET on this PC, first startup asks for the database' -ForegroundColor Cyan
    Write-Host '  3) Exit' -ForegroundColor DarkGray
    $mode = Read-Host 'Choice [1]'
    if (-not $mode) { $mode = '1' }
}

switch ($mode.ToString().ToLower()) {
    { $_ -in @('1', 'd', 'docker') } { Invoke-DockerInstall }
    { $_ -in @('2', 'n', 'native') } { Invoke-NativeInstall }
    default { Write-Info 'Bye.' }
}
