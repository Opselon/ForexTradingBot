#!/bin/bash

# Define colors for better output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# --- Header ---
echo -e "${GREEN}"
echo "#######################################################"
echo "#         🐳 Forex Trading Bot Startup Script         #"
echo "#######################################################"
echo -e "${NC}"
echo "This script will build and run your application using Docker Compose."
echo ""

# --- Step 1: Check for Prerequisites ---
echo -e "--- ${YELLOW}Step 1: Checking for required tools...${NC} ---"
# Function to check if a command exists
check_command() {
    if ! command -v "$1" &> /dev/null; then
        echo -e "❌ ${RED}Error: '$1' is not installed or not in your PATH.${NC}"
        echo -e "Please install Docker and Docker Compose before running this script."
        echo "Visit: https://www.docker.com/products/docker-desktop/"
        exit 1
    fi
    echo -e "✅ Found '$1'."
}

check_command "docker"
check_command "docker-compose"
echo ""

# --- Step 2: Check for .env file ---
echo -e "--- ${YELLOW}Step 2: Checking for configuration file...${NC} ---"
if [ ! -f .env ]; then
    echo -e "ℹ️  '.env' file not found. Creating one for you from the template."
    cp .env.example .env
    echo ""
    echo -e "----------------- ${RED}ACTION REQUIRED!${NC} -----------------"
    echo -e "A new file named ${YELLOW}'.env'${NC} has been created in this directory."
    echo -e "1. Open it with a text editor."
    echo -e "2. Fill in your secret keys (API tokens, passwords, etc.)."
    echo -e "3. Save the file."
    echo -e "4. ${GREEN}Run this './start.sh' script again.${NC}"
    echo "----------------------------------------------------"
    echo ""
    exit 1
fi
echo -e "✅ Found existing '.env' configuration file."
echo ""

# --- Step 3: Start the Application ---
echo -e "--- ${YELLOW}Step 3: Building and starting Docker containers...${NC} ---"
echo "This may take a few minutes the first time as images are downloaded and built."
echo ""

# Start all services defined in docker-compose.yml
docker-compose up --build -d

# Check the exit code of the last command
if [ $? -ne 0 ]; then
    echo ""
    echo -e "❌ ${RED}FAILURE: Docker Compose failed to start.${NC}"
    echo "There was an error during the build or startup process."
    echo "Please check the logs above for error messages."
    echo "You can also run 'docker-compose logs -f' to see more details."
    exit 1
fi

# --- Success Message ---
echo ""
echo -e "✅ ${GREEN}SUCCESS! Your application stack is starting up.${NC}"
echo ""
echo "------------------- What's Next? -------------------"
echo ""
echo -e "🔗 The API will be available shortly at: ${YELLOW}http://localhost:8080${NC}"
echo ""
echo "   To view live logs for your application:"
echo -e "   ${GREEN}docker-compose logs -f forex-trading-bot-app${NC}"
echo ""
echo "   To view live logs for the database:"
echo -e "   ${GREEN}docker-compose logs -f db${NC}"
echo ""
echo "   To stop everything:"
echo -e "   ${GREEN}docker-compose down${NC}"
echo ""
echo "----------------------------------------------------"