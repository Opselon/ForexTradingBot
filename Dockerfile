# ============================================
# Multi-Stage Dockerfile for ForexTradingBot (.NET 9)
# ============================================

# Stage 1: Build stage - restore dependencies, build, publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder
WORKDIR /src

# Copy project files first to leverage Docker layer caching
COPY *.sln ./
COPY Application/Application.csproj ./Application/
COPY Domain/Domain.csproj ./Domain/
COPY Infrastructure/Infrastructure.csproj ./Infrastructure/
COPY Shared/Shared.csproj ./Shared/
COPY BackgroundTasks/BackgroundTasks.csproj ./BackgroundTasks/
COPY TelegramPanel/TelegramPanel.csproj ./TelegramPanel/
COPY WebAPI/WebAPI.csproj ./WebAPI/

# Restore dependencies (cached until csproj changes)
RUN dotnet restore

# Copy all source code
COPY Application/ ./Application/
COPY Domain/ ./Domain/
COPY Infrastructure/ ./Infrastructure/
COPY Shared/ ./Shared/
COPY BackgroundTasks/ ./BackgroundTasks/
COPY TelegramPanel/ ./TelegramPanel/
COPY WebAPI/ ./WebAPI/
COPY Program.cs ./
COPY Update-EfDatabase.ps1 ./

# Publish optimized release build
RUN dotnet build "ForexTradingBot.sln" -c Release --no-restore -o /app/build
RUN dotnet publish "ForexTradingBot.sln" -c Release --no-build -o /app/publish

# Stage 2: Runtime stage - minimal runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install required packages for Serilog Console logger
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
    && mkdir -p /etc/apt/keyrings \
    && rm -rf /var/lib/apt/lists/*

# Set timezone environment variable
ENV TZ=Asia/Tehran
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Create non-root user for security (principle of least privilege)
RUN groupadd -r forexuser -g 1000 && \
    useradd -r -g forexuser forexuser -m -d /app

# Fix permissions
RUN chown -R forexuser:forexuser /app
USER forexuser

# Expose port for API
EXPOSE 8080

ENTRYPOINT ["dotnet", "ForexTradingBot.dll"]
