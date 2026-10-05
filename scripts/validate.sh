#!/bin/bash

# ============================================
# Deployment Validation Script - ForexTradingBot
# ============================================
# Pre-deployment checks and validation

set -e

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}ForexTradingBot Deployment Validator${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""

ERRORS=0

# Check 1: Git Repository
echo -e "${YELLOW}[CHECK 1] Git repository...${NC}"
if git rev-parse --git-dir > /dev/null 2>&1; then
    echo -e "${GREEN}✓ Valid Git repository${NC}"
else
    echo -e "${RED}✗ Not a Git repository${NC}"
    ERRORS=$((ERRORS + 1))
fi
echo ""

# Check 2: Docker Compose File
echo -e "${YELLOW}[CHECK 2] Docker Compose configuration...${NC}"
if docker compose config --quiet > /dev/null 2>&1; then
    echo -e "${GREEN}✓ Docker Compose configuration is valid${NC}"
    
    # Count services
    SERVICE_COUNT=$(docker compose config | grep "^  [a-z]" | wc -l)
    echo -e "   Found $SERVICE_COUNT services"
else
    echo -e "${RED}✗ Docker Compose configuration errors found${NC}"
    ERRORS=$((ERRORS + 1))
    docker compose config 2>&1 | tail -20
fi
echo ""

# Check 3: Environment Variables
echo -e "${YELLOW}[CHECK 3] Environment variables...${NC}"
if [ -f ".env" ]; then
    echo -e "${GREEN}✓ .env file exists${NC}"
    
    # Check for required variables
    if grep -q "TELEGRAM_BOT_TOKEN=" .env; then
        echo -e "   ✓ TELEGRAM_BOT_TOKEN configured"
    else
        echo -e "   ⚠ TELEGRAM_BOT_TOKEN not set (optional for testing)"
    fi
    
    if grep -q "POSTGRES_PASSWORD=" .env; then
        echo -e "   ⚠ POSTGRES_PASSWORD may be empty or weak"
        PASSWORD=$(grep "POSTGRES_PASSWORD=" .env | cut -d'=' -f2)
        if [ "$PASSWORD" = "change_this_to_a_strong_password_123!@#" ] || [ "$PASSWORD" = "your_strong_password_here" ]; then
            echo -e "   ${RED}   ✗ Please change default password!${NC}"
            ERRORS=$((ERRORS + 1))
        fi
    else
        echo -e "   ⚠ POSTGRES_PASSWORD not configured"
    fi
else
    echo -e "${YELLOW}⚠ No .env file found (copy from .env.example)${NC}"
    ERRORS=$((ERRORS + 1))
fi
echo ""

# Check 4: Files Existence
echo -e "${YELLOW}[CHECK 4] Required files...${NC}"
FILES=(
    "Dockerfile"
    "docker-compose.yml"
    "docker-compose.local.yml"
    "docker-compose.prod.yml"
    ".env.example"
    "scripts/database/init.sql"
)

for file in "${FILES[@]}"; do
    if [ -f "$file" ]; then
        echo -e "   ✓ $file"
    else
        echo -e "   ${RED}✗ Missing: $file${NC}"
        ERRORS=$((ERRORS + 1))
    fi
done
echo ""

# Check 5: Scripts Executable
echo -e "${YELLOW}[CHECK 5] Script permissions...${NC}"
SCRIPTS=(
    "deploy.sh"
    "scripts/health-check.sh"
)

for script in "${SCRIPTS[@]}"; do
    if [ -x "$script" ]; then
        echo -e "   ✓ $script is executable"
    else
        echo -e "   ${YELLOW}⚠ $script is not executable (run: chmod +x $script)${NC}"
    fi
done
echo ""

# Check 6: .NET Project Structure
echo -e "${YELLOW}[CHECK 6] .NET project structure...${NC}"
PROJECTS=(
    "Application/Application.csproj"
    "Domain/Domain.csproj"
    "Infrastructure/Infrastructure.csproj"
    "Shared/Shared.csproj"
    "WebAPI/WebAPI.csproj"
    "ForexTradingBot.sln"
)

for proj in "${PROJECTS[@]}"; do
    if [ -f "$proj" ]; then
        echo -e "   ✓ $proj"
    else
        echo -e "   ${RED}✗ Missing: $proj${NC}"
        ERRORS=$((ERRORS + 1))
    fi
done
echo ""

# Check 7: Documentation
echo -e "${YELLOW}[CHECK 7] Documentation...${NC}"
DOCS=(
    "docs/DEPLOYMENT.md"
    "docs/SETUP_GUIDE.md"
)

for doc in "${DOCS[@]}"; do
    if [ -f "$doc" ]; then
        LINES=$(wc -l < "$doc")
        echo -e "   ✓ $doc ($LINES lines)"
    else
        echo -e "   ⚠ Missing: $doc"
    fi
done
echo ""

# Summary
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Validation Summary${NC}"
echo -e "${GREEN}========================================${NC}"

if [ $ERRORS -eq 0 ]; then
    echo -e "${GREEN}✓ All checks passed! Ready for deployment.${NC}"
    echo ""
    echo -e "${YELLOW}Next steps:${NC}"
    echo -e "  1. Review and edit .env with your actual credentials"
    echo -e "  2. Run deployment: bash deploy.sh"
    echo -e "  3. Monitor startup: docker-compose logs -f"
else
    echo -e "${RED}✗ $ERRORS issue(s) found${NC}"
    echo ""
    echo -e "${YELLOW}Please fix the issues above before deploying.${NC}"
fi
echo ""

exit $ERRORS
