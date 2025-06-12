#!/bin/bash

echo ""
echo "#######################################################"
echo "#         Forex Trading Bot Startup Script            #"
echo "#######################################################"
echo ""
echo "This script will build and start your application and database using Docker Compose."
echo "Make sure the Docker daemon is running."
echo ""

# Check if a .env file exists. If not, create one from the template.
if [ ! -f .env ]; then
    echo "[INFO] '.env' file not found. Creating one from the template."
    echo ""
    echo "[ACTION REQUIRED!] Please open the new '.env' file in a text editor and fill in your secrets."
    cp .env.example .env
    echo ""
    echo "After editing the .env file, please run this './start.sh' script again."
    echo ""
    exit 1
fi

echo "[INFO] Found .env file. Starting Docker services..."
echo "This may take a few minutes the first time."
echo ""

# Start all services defined in docker-compose.yml
docker-compose up --build -d

echo ""
echo "[SUCCESS] Application and database are starting up in the background."
echo ""
echo "To view the application logs, run: docker-compose logs -f forex-trading-bot-app"
echo "To view the database logs, run:   docker-compose logs -f db"
echo "To stop everything, run:          docker-compose down"
echo ""
echo "The API will be available at http://localhost:8080 shortly."
echo ""