#!/bin/bash

echo "================================================"
echo "Starting ForexTradingBot..."
echo "Environment: $ASPNETCORE_ENVIRONMENT"
echo "================================================"

# Wait for PostgreSQL to be ready
echo "Waiting for PostgreSQL to be ready..."
while ! nc -z db 5432; do
  sleep 1
done
echo "PostgreSQL is ready!"

# Wait for Redis to be ready
echo "Waiting for Redis to be ready..."
while ! nc -z redis 6379; do
  sleep 1
done
echo "Redis is ready!"

# Start the application
echo "Starting application..."
cd /app/webapi
dotnet WebAPI.dll 