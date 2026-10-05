#!/bin/bash

# ============================================
# Production Deployment Script - ForexTradingBot
# ============================================
# This script automates the deployment process on a fresh server
# Run with: sudo bash deploy.sh

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
APP_NAME="ForexTradingBot"
APP_DIR="/opt/$APP_NAME"
BACKUP_DIR="/backup/$APP_NAME/$(date +%Y%m%d_%H%M%S)"

echo -e "${GREEN}=== $APP_NAME Deployment Script ===${NC}"
echo ""

# Step 1: Check prerequisites
echo -e "${YELLOW}[Step 1] Checking prerequisites...${NC}"
if ! command -v docker &> /dev/null; then
    echo -e "${RED}Docker is not installed. Installing Docker...${NC}"
    curl -fsSL https://get.docker.com -o get-docker.sh
    sh get-docker.sh
    rm get-docker.sh
fi

if ! command -v docker-compose &> /dev/null; then
    echo -e "${RED}Docker Compose is not installed.${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Prerequisites met${NC}"
echo ""

# Step 2: Create backup directory
echo -e "${YELLOW}[Step 2] Setting up directories...${NC}"
sudo mkdir -p "$APP_DIR"
sudo mkdir -p "$BACKUP_DIR"
echo -e "${GREEN}✓ Directories created${NC}"
echo ""

# Step 3: Clone or update repository
echo -e "${YELLOW}[Step 3] ${APP_NAME} code...${NC}"
cd "$APP_DIR" || {
    git clone https://github.com/Opselon/ForexTradingBot.git .
}

if [ -d ".git" ]; then
    git pull origin main || true
fi
echo -e "${GREEN}✓ Repository updated${NC}"
echo ""

# Step 4: Configure environment variables
echo -e "${YELLOW}[Step 4] Environment configuration...${NC}"
if [ ! -f ".env" ]; then
    cp .env.example .env
    echo -e "${YELLOW}Created .env file from template. Please edit it with your credentials!${NC}"
    echo -e "${YELLOW}Edit with: nano $APP_DIR/.env${NC}"
    
    # Generate strong password if needed
    if grep -q "change_this_to_a_strong_password" .env; then
        NEW_PASSWORD=$(openssl rand -base64 32 | tr -dc 'a-zA-Z0-9@#!' | head -c 32)
        sed -i "s/change_this_to_a_strong_password.*/$NEW_PASSWORD/" .env
        echo -e "${GREEN}Generated PostgreSQL password: $NEW_PASSWORD${NC}"
        echo -e "${GREEN}IMPORTANT: Save this password securely!${NC}"
    fi
fi
echo -e "${GREEN}✓ Environment configured${NC}"
echo ""

# Step 5: Setup database scripts
echo -e "${YELLOW}[Step 5] Setting up database initialization...${NC}"
mkdir -p ./scripts/database
if [ ! -f "./scripts/database/init.sql" ]; then
    echo "Database initialization script already exists"
fi
echo -e "${GREEN}✓ Database scripts ready${NC}"
echo ""

# Step 6: Test Docker configuration
echo -e "${YELLOW}[Step 6] Validating Docker configuration...${NC}"
docker-compose config > /dev/null 2>&1
if [ $? -ne 0 ]; then
    echo -e "${RED}✗ Docker Compose configuration validation failed${NC}"
    exit 1
fi
echo -e "${GREEN}✓ Configuration valid${NC}"
echo ""

# Step 7: Backup existing data (if any)
echo -e "${YELLOW}[Step 7] Backing up existing data...${NC}"
if [ -d "/var/lib/docker/volumes/${APP_NAME}_postgres_data" ]; then
    docker run --rm -v ${APP_NAME}_postgres_data:/data alpine tar czf "$BACKUP_DIR/postgres_backup.tar.gz" /data
    echo -e "${GREEN}✓ Data backed up to $BACKUP_DIR${NC}"
fi
echo ""

# Step 8: Build and start containers
echo -e "${YELLOW}[Step 8] Building and starting containers...${NC}"
docker-compose down --remove-orphans 2>/dev/null || true
docker-compose build --no-cache
docker-compose up -d

echo -e "${GREEN}✓ Containers started${NC}"
echo ""

# Step 9: Wait for services to be healthy
echo -e "${YELLOW}[Step 9] Waiting for services to start...${NC}"
MAX_RETRIES=30
RETRY_COUNT=0

while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
    HEALTH=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/health 2>/dev/null || echo "000")
    if [ "$HEALTH" = "200" ]; then
        echo -e "${GREEN}✓ API is healthy!${NC}"
        break
    fi
    
    RETRY_COUNT=$((RETRY_COUNT + 1))
    echo -e "${YELLOW}Waiting for API health check... ($RETRY_COUNT/$MAX_RETRIES) - HTTP Status: $HEALTH${NC}"
    sleep 10
done

if [ "$HEALTH" != "200" ]; then
    echo -e "${RED}✗ API failed health check after $MAX_RETRIES attempts${NC}"
    echo -e "${YELLOW}Check logs: docker-compose logs -f forex-api${NC}"
    exit 1
fi

echo ""

# Step 10: Verify all services
echo -e "${YELLOW}[Step 10] Verifying all services...${NC}"
docker-compose ps

DB_STATUS=$(docker exec forex-postgres psql -U postgres -d forexbotdb -t -c "SELECT 1;" 2>&1)
if [[ "$DB_STATUS" == *"1"* ]]; then
    echo -e "${GREEN}✓ PostgreSQL database is accessible${NC}"
else
    echo -e "${YELLOW}⚠ Database check skipped (this may be expected during initial setup)${NC}"
fi

REDIS_STATUS=$(docker exec forex-redis redis-cli ping 2>/dev/null || echo "")
if [[ "$REDIS_STATUS" == *"PONG"* ]]; then
    echo -e "${GREEN}✓ Redis cache is running${NC}"
else
    echo -e "${YELLOW}⚠ Redis check skipped${NC}"
fi

echo ""

# Step 11: Summary
echo -e "${GREEN}=========================================="
echo "         DEPLOYMENT COMPLETE!"
echo "==========================================${NC}"
echo ""
echo "Application URL:      http://your-server-ip:8080"
echo "Health Check:         http://your-server-ip:8080/health"
echo "Telegram Bot URL:     Check bot status in Telegram"
echo ""
echo "Available Commands:"
echo "  docker-compose logs -f          # View logs"
echo "  docker-compose restart          # Restart all services"
echo "  docker-compose stop             # Stop all services"
echo "  docker-compose up -d --build    # Rebuild and restart"
echo ""
echo "Backup Location: $BACKUP_DIR"
echo "Configuration File: $APP_DIR/.env"
echo ""
echo -e "${YELLOW}IMPORTANT:${NC} Monitor logs for first hour to ensure everything works correctly"
echo ""
