@echo off
ECHO.
ECHO #######################################################
ECHO #         Forex Trading Bot Startup Script            #
ECHO #######################################################
ECHO.
ECHO This script will build and start your application and database using Docker Compose.
ECHO Make sure Docker Desktop is running.
ECHO.

REM Check if a .env file exists. If not, create one from the template.
IF NOT EXIST .env (
    ECHO [INFO] '.env' file not found. Creating one from the template.
    ECHO.
    ECHO [ACTION REQUIRED!] Please open the new '.env' file in a text editor and fill in your secrets.
    copy .env.example .env
    ECHO.
    ECHO After editing the .env file, please run this 'start.bat' script again.
    ECHO.
    PAUSE
    exit /b
)

ECHO [INFO] Found .env file. Starting Docker services...
ECHO This may take a few minutes the first time.
ECHO.

REM Start all services defined in docker-compose.yml
docker-compose up --build -d

ECHO.
ECHO [SUCCESS] Application and database are starting up in the background.
ECHO.
ECHO To view the application logs, run: docker-compose logs -f forex-trading-bot-app
ECHO To view the database logs, run:   docker-compose logs -f db
ECHO To stop everything, run:          docker-compose down
ECHO.
ECHO The API will be available at http://localhost:8080 shortly.
ECHO.
PAUSE