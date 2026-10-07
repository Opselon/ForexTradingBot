# Quick Start

ForexTradingBot ships as a **self-contained** bundle — you do **not** need to
install .NET, a runtime, or any SDK. Everything required is inside this folder.

## 1. Native run (no Docker)

```bash
# Pick any folder you like; the app stores data next to the executable.
./WebAPI
```

On first start the Easy Setup Wizard asks which database you want:

- **PostgreSQL** (recommended for production)
- **SQLite** (zero configuration, good for trying it out)
- **SQL Server**

It then generates a random admin password and prints it once — save it.

Open the panel: http://localhost:5000

### Native quick start with SQLite (no questions asked)

```bash
ASPNETCORE_ENVIRONMENT=Production \
DatabaseSettings__DatabaseProvider=sqlite \
ConnectionStrings__DefaultConnection="Data Source=/data/forexbot.db" \
TelegramPanel__BotToken="123456:your-bot-token" \
./WebAPI
```

Any setting can be passed as an environment variable using the `__` separator,
for example `TelegramPanel__BotToken`.

## 2. CLI tool

```bash
./cli/forexbot --help
./cli/forexbot secrets set MY_API_KEY "value"
./cli/forexbot secrets list
./cli/forexbot backup create
```

## 3. Docker (PostgreSQL + Redis included)

```bash
cp .env.example .env      # then edit POSTGRES_PASSWORD
docker compose up -d --build
```

The compose file starts PostgreSQL, Redis, and the app together.
Check health: http://localhost:8080/healthz

## 4. One-command installer

If you cloned the repository instead of using this archive:

```bash
./install.sh        # Linux / macOS
.\install.ps1       # Windows PowerShell
```

It installs Docker / PostgreSQL / Redis when missing and starts everything.

## Where data lives

| Path | Purpose |
|---|---|
| `./data/` | Database, secrets vault, sessions (created on first run) |
| `./logs/` | Serilog log files |

## Common issues

- **Port already in use:** set `ASPNETCORE_URLS=http://localhost:5050` (or any port) before starting.
- **Permission denied:** `chmod +x WebAPI cli/forexbot install.sh entrypoint.sh`.
- **Windows:** extract the zip with "Extract All"; Windows Defender may scan the single-file exe on first launch.
