#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install Go and netcat
RUN apt-get update && apt-get install -y golang-go netcat-traditional && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy solution and project files
COPY ["ForexTradingBot.sln", "Directory.Packages.props", "nuget.config", "./"]
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Core/Core.csproj", "Core/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]

# Install EF Core tools
RUN dotnet tool install --global dotnet-ef

# Restore packages
RUN dotnet restore "WebAPI/WebAPI.csproj" --configfile nuget.config

# Copy the rest of the code
COPY . .

# Build Go log monitor
WORKDIR /src/WebAPI
RUN go build -o docker_log_monitor docker_log_monitor.go

# Build .NET applications
WORKDIR /src
RUN dotnet build "Core/Core.csproj" -c Release -o /app/build/core
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build/webapi
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build/tasks

#-------------------------------------------------------------------------------------
# Stage 2: Final Runtime Image
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create non-root user
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser

# Install curl, netcat, Go, and Docker CLI for health checks and log monitoring
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    netcat-traditional \
    golang-go \
    docker.io \
    && rm -rf /var/lib/apt/lists/*

# Copy the Go log monitor
COPY --from=build /src/WebAPI/docker_log_monitor .

# Copy the published applications
COPY --from=build /app/build/webapi ./webapi
COPY --from=build /app/build/tasks ./tasks

# Create necessary directories
RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection

# Set permissions
RUN chown -R appuser:appuser /app && \
    chmod -R u=rX,g=rX,o= /app/webapi && \
    chmod -R u=rX,g=rX,o= /app/tasks && \
    chmod -R u=rwx,g=,o= /app/telegram-sessions && \
    chmod -R u=rwx,g=,o= /app/data-protection && \
    chmod +x /app/docker_log_monitor

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TELEGRAM_SESSION_PATH=/app/telegram-sessions
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 80

# Set working directory
WORKDIR /app/webapi

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1

# Run the application
ENTRYPOINT ["dotnet", "WebAPI.dll"]
