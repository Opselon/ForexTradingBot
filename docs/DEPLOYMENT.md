# ============================================
# Production Deployment Guide - ForexTradingBot
# ============================================

## Prerequisites
- Docker Engine 20.10+ installed on server
- Docker Compose 2.0+ installed
- Access to server via SSH
- Domain name with SSL certificate (optional but recommended)
- Firewall configured to allow ports 80, 443, 8080

## Step 1: Clone Repository
```bash
cd /opt
git clone https://github.com/Opselon/ForexTradingBot.git
cd ForexTradingBot
```

## Step 2: Configure Environment Variables
```bash
# Create .env file from example
cp .env.example .env

# Edit .env with your actual values
nano .env
```

**Important fields to configure:**
- `TELEGRAM_BOT_TOKEN` - Get from @BotFather on Telegram
- `POSTGRES_PASSWORD` - Set a strong password
- `TELEGRAM_API_ID`, `TELEGRAM_API_HASH` - From my.telegram.org
- `CRYPTO_PAY_*` - If using Crypto Pay integration

## Step 3: Create Directory Structure
```bash
mkdir -p ./scripts/database
```

## Step 4: Test Configuration
```bash
docker-compose config
```
Verify no errors are reported.

## Step 5: Start Services
```bash
# Build and start all services in background
docker-compose up -d --build

# Check if containers are running
docker-compose ps
```

## Step 6: Monitor Logs
```bash
# View logs in real-time
docker-compose logs -f

# View specific service logs
docker-compose logs -f forex-api
docker-compose logs -f postgres
docker-compose logs -f redis
```

## Step 7: Database Migration (if using EF Core)
```bash
# Enter API container
docker exec -it forex-bot-api sh

# Run migrations
dotnet ef database update --project WebAPI/WebAPI.csproj

# Exit container
exit
```

## Step 8: Verify Health Checks
```bash
# Check API health endpoint
curl http://localhost:8080/health

# Check database connectivity
docker exec -it forex-postgres psql -U postgres -d forexbotdb -c "SELECT version();"
```

## Maintenance Commands

### Restart All Services
```bash
docker-compose restart
```

### Stop All Services
```bash
docker-compose down
```

### Update Application
```bash
# Pull latest changes
git pull

# Rebuild and restart
docker-compose up -d --build
```

### Backup Database
```bash
docker exec forex-postgres pg_dump -U postgres forexbotdb > backup_$(date +%Y%m%d).sql
```

### Restore Database
```bash
docker exec -i forex-postgres psql -U postgres forexbotdb < backup_20241005.sql
```

## Monitoring Setup

### View Resource Usage
```bash
docker stats
```

### Check Container Logs
```bash
docker-compose logs --tail=100
```

### Inspect Service Configuration
```bash
docker inspect forex-bot-api | jq '.[0].Config.Env'
```

## Security Best Practices

1. **Never commit .env file to git**
   ```bash
   echo ".env" >> .gitignore
   ```

2. **Use strong passwords**
   Generate PostgreSQL password with:
   ```bash
   openssl rand -base64 32
   ```

3. **Enable HTTPS** (recommended for production)
   Use nginx or similar as reverse proxy with Let's Encrypt

4. **Limit exposed ports**
   Only expose what's necessary in production

5. **Regular updates**
   Keep Docker, images, and application dependencies updated

## Troubleshooting

### API Not Starting
```bash
docker-compose logs forex-api
docker exec -it forex-bot-api tail -f /app/logs/app.log
```

### Database Connection Issues
```bash
docker-compose logs postgres
docker exec -it forex-postgres psql -U postgres -c "SHOW configuration;"
```

### Port Already In Use
Edit docker-compose.yml to use different ports:
```yaml
ports:
  - "8081:8080"  # Use 8081 instead of 8080
```

## Rollback Procedure
```bash
# Stop current services
docker-compose down

# Pull previous version (if tagged)
git checkout <previous-tag>

# Rebuild with old version
docker-compose build
docker-compose up -d
```

## Scaling Considerations

For high availability:
1. Use external PostgreSQL (RDS, Cloud SQL)
2. Deploy multiple API instances behind load balancer
3. Use Redis cluster instead of standalone Redis
4. Implement proper monitoring (Prometheus + Grafana)
