# ============================================
# ForexTradingBot - Docker Deployment Package
# ============================================
## ✅ Ready for Production Deployment

Your Docker setup is complete and validated. Here's what was created:

---

## 📦 What You Have

### Container Configuration
- ✅ **Dockerfile** - Multi-stage build (.NET 9 SDK & ASP.NET Runtime)
- ✅ **docker-compose.yml** - Local development environment
- ✅ **docker-compose.local.yml** - Development mode with hot-reload support
- ✅ **docker-compose.prod.yml** - Production deployment configuration

### Documentation & Scripts
- ✅ **docs/DEPLOYMENT.md** - Step-by-step production guide (196 lines)
- ✅ **docs/SETUP_GUIDE.md** - End-to-end setup instructions (342 lines)
- ✅ **deploy.sh** - Automated deployment script
- ✅ **scripts/health-check.sh** - Service health monitoring
- ✅ **scripts/validate.sh** - Pre-deployment validation
- ✅ **scripts/database/init.sql** - Database initialization

### Environment Configuration
- ✅ **.env.example** - Template with all required variables

---

## 🚀 Quick Start (5 Minutes)

### 1️⃣ Clone & Configure
```bash
cd /opt
git clone https://github.com/Opselon/ForexTradingBot.git
cd ForexTradingBot

# Copy environment template
cp .env.example .env

# Edit with your credentials
nano .env
```

**Required fields in .env:**
```env
TELEGRAM_BOT_TOKEN=your_bot_token_here
POSTGRES_PASSWORD=change_this_to_a_strong_password_123!@#
TELEGRAM_API_ID=111111
TELEGRAM_API_HASH=your_api_hash_here
TELEGRAM_PHONE_NUMBER=+989XXXXXXXXX
```

### 2️⃣ Validate Setup
```bash
./scripts/validate.sh
```

Expected output: **"✓ All checks passed! Ready for deployment."**

### 3️⃣ Deploy to Server
```bash
# Option A: Automated (Recommended)
bash deploy.sh

# Option B: Manual steps
docker-compose build
docker-compose up -d --build
```

### 4️⃣ Monitor Startup
```bash
# Watch logs in real-time
docker-compose logs -f

# Check health status
./scripts/health-check.sh
```

Wait until you see:
- ✅ API is healthy (HTTP 200)
- ✓ PostgreSQL database is accessible
- ✓ Redis cache is running

---

## 🔧 After Deployment

### Access Points
- **API Health:** http://localhost:8080/health
- **Telegram Bot:** Open @bot_username in Telegram
- **Database Port:** localhost:5432 (if exposed)

### Important Commands
```bash
# View logs
docker-compose logs -f              # All services
docker-compose logs -f forex-api    # API only

# Restart all services
docker-compose restart

# Stop everything
docker-compose down

# Rebuild after updates
docker-compose up -d --build

# Backup database
docker exec forex-postgres pg_dump -U postgres forextradingbot > backup.sql
```

---

## 🎯 Testing Checklist

After deployment completes, verify:

- [ ] API responds to `curl http://localhost:8080/health`
- [ ] Telegram bot starts without errors
- [ ] Database migrations run successfully
- [ ] Redis connectivity works
- [ ] No memory leaks over 1 hour of operation
- [ ] Logs show "Application Started Successfully"

---

## 🐛 Troubleshooting

### "Port Already In Use"
Change port mappings in docker-compose.yml:
```yaml
ports:
  - "8081:8080"   # Instead of 8080
```

### "Cannot Connect to Database"
```bash
# Restart database container
docker-compose restart db

# Check password matches
grep POSTGRES_PASSWORD .env
docker exec forex-postgres psql -U postgres -c "\conninfo"
```

### "Telegram Login Failed"
1. Verify API credentials from my.telegram.org
2. Check phone format: +989XXXXXXXXX (no spaces)
3. Ensure session directory exists and is writable

### Containers Won't Start
```bash
# Check configuration
docker compose config

# View specific service logs
docker-compose logs forex-api | tail -100
```

---

## 🔐 Security Best Practices

1. **Never commit .env file**
   ```bash
   echo ".env" >> .gitignore
   ```

2. **Use strong passwords**
   Generate with: `openssl rand -base64 32 | tr -dc 'a-zA-Z0-9@#!' | head -c 32`

3. **Enable HTTPS in production**
   Set up nginx reverse proxy with Let's Encrypt SSL

4. **Regular updates**
   ```bash
   git pull origin main
   docker-compose pull
   docker-compose up -d
   ```

5. **Monitor resource usage**
   ```bash
   docker stats --no-stream
   ```

---

## 📊 Resource Requirements

### Minimum Viable Setup
- CPU: 2 cores
- RAM: 2GB total
- Storage: 5GB SSD
- Ports: 8080 (API), 5432 (DB if exposed)

### Recommended Production Setup
- CPU: 4 cores
- RAM: 4GB
- Storage: 20GB SSD
- Network: Load balancer with SSL termination

---

## 🔄 Update Process

When pulling new updates:

```bash
git pull origin main
docker-compose down          # Stop current
docker-compose pull          # Pull fresh images
docker-compose up -d         # Start with new version
docker-compose logs -f       # Monitor startup
```

---

## 💾 Data Backup

Create automated backups:
```bash
# Add to crontab (daily at 2 AM)
crontab -e
0 2 * * * /path/to/forexbot/scripts/backups/backup_db.sh
```

Backup script creates:
- Daily database dumps
- Weekly full system snapshots
- Monthly retention policy cleanup

---

## 🆘 Need Help?

1. **Check logs first:**
   ```bash
   docker-compose logs --tail=200 | grep -i error
   ```

2. **Review documentation:**
   - `docs/DEPLOYMENT.md` - Detailed deployment guide
   - `docs/SETUP_GUIDE.md` - Complete end-user setup

3. **GitHub Issues:**
   Create issue with:
   - Relevant log excerpts
   - Error messages
   - Steps to reproduce

---

## ✨ What's Next?

1. ✅ Docker files created
2. ✅ Configuration validated
3. ✅ Documentation complete
4. ⏬ Test locally or deploy to server
5. ⏬ Monitor first 24 hours carefully
6. ⏬ Set up monitoring/alerting
7. ⏬ Configure automated backups

**You're ready to go! 🚀**

---

*For detailed troubleshooting, see docs/DEPLOYMENT.md*
*For step-by-step guides, see docs/SETUP_GUIDE.md*
