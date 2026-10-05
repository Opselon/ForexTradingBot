# ============================================
# ForexTradingBot - End User Setup Guide
# ============================================

This guide covers three setup methods:
1. **Local Development** (Docker - Easy)
2. **Production Deployment** (Docker - Server)
3. **Manual .NET Installation** (Advanced)

---

## 📋 Prerequisites

### Common Requirements
- Git installed on your machine
- Basic command line knowledge
- A Telegram account with phone number verified

### Required Accounts/Tokens
1. **Telegram Bot Token**: Get from @BotFather on Telegram
2. **Telegram API Credentials**: From https://my.telegram.org/api
3. **Database Password**: Generate strong password (see below)

Generate strong password:
```bash
openssl rand -base64 32 | tr -dc 'a-zA-Z0-9@#!' | head -c 32
```

---

## 🚀 Method 1: Docker Setup (Recommended - Easiest)

### Step 1: Clone Repository
```bash
cd /home/ubuntu/projects
git clone https://github.com/Opselon/ForexTradingBot.git
cd ForexTradingBot
```

### Step 2: Configure Environment
```bash
# Copy example file to actual .env
cp .env.example .env

# Edit with your values (use nano or vim)
nano .env
```

**Required fields to update:**
```env
TELEGRAM_BOT_TOKEN=your_bot_token_here
POSTGRES_PASSWORD=your_strong_password_here
TELEGRAM_API_ID=111111
TELEGRAM_API_HASH=your_api_hash_here
TELEGRAM_PHONE_NUMBER=+989XXXXXXXXX
```

Optional fields (add if you need these features):
```env
CRYPTO_PAY_API_TOKEN=if_using_crypto_pay
FORWARDING_BOT_TOKEN=for_auto_forwarding
SOURCE_CHANNEL_ID=-100xxxxxxxxx
TARGET_CHANNEL_ID=-100xxxxxxxxx
```

### Step 3: Start with Docker Compose

#### Local Testing (Development Mode)
```bash
docker-compose -f docker-compose.local.yml up -d --build
```

#### Production Deployment
```bash
# Build production images
docker-compose build

# Start in production mode
docker-compose -f docker-compose.prod.yml up -d
```

### Step 4: Monitor and Verify
```bash
# Check all containers are running
docker-compose ps

# View logs in real-time
docker-compose logs -f forex-bot-local

# Check health status
./scripts/health-check.sh
```

Expected output should show ✓ for all services.

---

## 🖥️ Method 2: Manual .NET Installation (Advanced Users)

### Step 1: Install .NET 9 SDK
```bash
# Ubuntu/Debian
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
apt-get install dotnet-sdk-9.0 -y
```

### Step 2: Install PostgreSQL Client Libraries
```bash
# For EF Core migrations
apt-get install libpq-dev -y
```

### Step 3: Download Application Code
```bash
mkdir -p ~/forexbot && cd ~/forexbot
git clone https://github.com/Opselon/ForexTradingBot.git .
```

### Step 4: Configure Environment
```bash
cp .env.example .env
nano .env
# Edit with your actual values (same as Docker method)
```

### Step 5: Restore and Build
```bash
dotnet restore ForexTradingBot.sln
dotnet build ForexTradingBot.sln -c Release
```

### Step 6: Run Database Migrations
```bash
cd WebAPI
dotnet ef database update
```

*Note: You may need to configure connection strings temporarily for local SQL Server development*

### Step 7: Run the Application
```bash
dotnet run --project WebAPI/WebAPI.csproj
```

Access at: http://localhost:5000

---

## 🔧 Troubleshooting

### Problem: Containers Won't Start
**Solution:**
```bash
# Check what's wrong
docker-compose config

# View error logs
docker-compose logs

# Check port conflicts
netstat -tuln | grep -E "8080|5432|6379"
```

### Problem: Cannot Connect to Database
**Solution:**
```bash
# Restart database container
docker-compose restart db

# Check database logs
docker-compose logs db

# Verify password matches in .env and init.sql
grep POSTGRES_PASSWORD .env
```

### Problem: Telegram Login Fails
**Solutions:**
1. Verify API ID and Hash from my.telegram.org
2. Check phone number format: +989XXXXXXXXX (no spaces, with country code)
3. Ensure session storage directory exists and is writable

### Problem: Port Already In Use
**Solution:** Change ports in docker-compose.yml
```yaml
ports:
  - "8081:8080"   # Instead of 8080
  - "5433:5432"   # Instead of 5432
```

### Problem: Memory Usage Too High
**Solution:** Add resource limits to docker-compose.yml
```yaml
services:
  forex-api:
    deploy:
      resources:
        limits:
          memory: 1G
```

---

## 📝 Initial Configuration Checklist

After deployment, complete these steps:

1. [ ] **Test API Endpoint**
   ```bash
   curl http://localhost:8080/health
   ```

2. [ ] **Verify Telegram Bot**
   - Open bot URL: https://t.me/your_bot_username
   - Click Start and verify response

3. [ ] **Check Database Tables**
   ```bash
   docker exec -it forex-postgres psql -U postgres -d forexbotdb -c "\dt"
   ```

4. [ ] **Monitor Logs First Hour**
   ```bash
   docker-compose logs -f --tail=100
   ```

5. [ ] **Set Up Backup Schedule**
   ```bash
   # Add to crontab (every day at 2 AM)
   0 2 * * * docker exec forex-postgres pg_dump -U postgres forexbotdb > /backup/forex_$(date +\%Y\%m\%d).sql
   ```

---

## 🔐 Security Best Practices

1. **Never commit .env file** to git
   ```bash
   echo ".env" >> .gitignore
   git add .gitignore
   git commit -m "Add .env to gitignore"
   ```

2. **Use HTTPS in production**
   - Set up nginx with Let's Encrypt SSL certificate
   - Update `ASPNETCORE_URLS` to `https://+:443`

3. **Regular Updates**
   ```bash
   docker-compose pull
   docker-compose up -d
   ```

4. **Strong Passwords**
   Use at least 32 characters with mixed case, numbers, and symbols

5. **Firewall Configuration**
   ```bash
   # Allow only necessary ports
   sudo ufw allow 22/tcp       # SSH
   sudo ufw allow 80/tcp       # HTTP
   sudo ufw allow 443/tcp      # HTTPS
   sudo ufw enable
   ```

---

## 📊 Monitoring Commands

```bash
# Real-time metrics
docker stats

# Log aggregation
docker-compose logs -f

# Resource usage history
docker system df

# Disk space check
df -h
```

---

## 🆘 Support

For issues not covered here:
1. Check detailed logs: `docker-compose logs -f`
2. Review application documentation in `/docs` folder
3. Create GitHub issue with relevant logs
4. Search existing issues on GitHub repository

---

## 🔄 Update Process

When pulling new updates from GitHub:

```bash
# Pull latest code
git pull origin main

# Stop services
docker-compose down

# Pull fresh images
docker-compose pull

# Rebuild if needed
docker-compose build

# Start with new version
docker-compose up -d

# Monitor startup
docker-compose logs -f
```

---

## 💾 Data Backup

Automated backup script:
```bash
#!/bin/bash
BACKUP_DIR="/backup/forextradingbot"
DATE=$(date +%Y%m%d_%H%M%S)

docker compose exec -T db pg_dump -U postgres forexbotdb > $BACKUP_DIR/db_$DATE.sql
docker save forex-postgres > $BACKUP_DIR/postgres_$IMAGE.tar.gz

echo "Backup completed: $BACKUP_DIR/$DATE"
```

Schedule with cron:
```bash
crontab -e
# Add line: 0 2 * * * /path/to/backup.sh
```
