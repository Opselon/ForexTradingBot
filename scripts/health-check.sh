#!/bin/bash

# ============================================
# Health Check Script - ForexTradingBot
# ============================================
# Checks the health of all services in docker-compose setup

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}ForexTradingBot Health Check${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""

# Check Docker status
echo -e "${YELLOW}Checking Docker daemon...${NC}"
if ! sudo systemctl is-active --quiet docker; then
    echo -e "${RED}✗ Docker daemon is not running${NC}"
    exit 1
else
    echo -e "${GREEN}✓ Docker daemon is running${NC}"
fi
echo ""

# Check if containers are running
echo -e "${YELLOW}Container Status:${NC}"
docker-compose ps | grep -E "NAME| forex" || echo -e "${RED}No containers found. Run: docker-compose up -d${NC}"
echo ""

# Check each service individually
echo -e "${YELLOW}Service Details:${NC}"

# 1. API Health Check
echo -e "\n${YELLOW}1. API Service (forex-bot-api):${NC}"
API_HEALTH=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/health 2>/dev/null || echo "000")

if [ "$API_HEALTH" = "200" ]; then
    echo -e "   ${GREEN}✓ API is healthy (HTTP $API_HEALTH)${NC}"
    
    # Get detailed info
    API_INFO=$(curl -s http://localhost:8080/health 2>/dev/null)
    if [ -n "$API_INFO" ]; then
        echo "   Response: $API_INFO"
    fi
else
    echo -e "   ${RED}✗ API health check failed (HTTP $API_HEALTH)${NC}"
    echo -e "   Logs: docker-compose logs -f forex-api | tail -50"
fi

# 2. PostgreSQL Database
echo -e "\n${YELLOW}2. PostgreSQL Database (forex-postgres):${NC}"
DB_STATUS=$(docker exec forex-postgres psql -U postgres -d forexbotdb -t -c "SELECT version();" 2>&1 | head -c 100)

if [[ "$DB_STATUS" == *"PostgreSQL"* ]]; then
    echo -e "   ${GREEN}✓ Database is accessible${NC}"
    echo -e "   Version: $(echo $DB_STATUS | tr '\n' ' ')"
else
    echo -e "   ${RED}✗ Cannot connect to database${NC}"
    echo -e "   Logs: docker-compose logs -f db | tail -50"
fi

# 3. Redis Cache
echo -e "\n${YELLOW}3. Redis Cache (forex-redis):${NC}"
REDIS_STATUS=$(docker exec forex-redis redis-cli ping 2>/dev/null || echo "")

if [[ "$REDIS_STATUS" == *"PONG"* ]]; then
    echo -e "   ${GREEN}✓ Redis is responding${NC}"
else
    echo -e "   ${RED}✗ Redis is not responding${NC}"
    echo -e "   Logs: docker-compose logs -f redis | tail -50"
fi

# 4. Hangfire Database
echo -e "\n${YELLOW}4. Hangfire Database (forex-hangfire-db):${NC}"
HANGFIRE_STATUS=$(docker exec forex-hangfire-db psql -U postgres -d forextradingbot_hangfire -t -c "SELECT 1;" 2>&1 | head -c 50)

if [[ "$HANGFIRE_STATUS" == *"1"* ]]; then
    echo -e "   ${GREEN}✓ Hangfire database is accessible${NC}"
else
    echo -e "   ${YELLOW}⚠ Hangfire database may not be configured yet${NC}"
fi

# 5. Resource Usage
echo -e "\n${YELLOW}Resource Usage:${NC}"
docker stats --no-stream --format "table {{.Container}}\t{{.CPU %}}\t{{.Mem Usage}}\t{{.Net I/O}}"

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Health Check Complete${NC}"
echo -e "${GREEN}========================================${NC}"
