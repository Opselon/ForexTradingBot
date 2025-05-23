#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["ForexTradingBot.sln", "./"]
COPY ["nuget.config", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]

# Restore packages
RUN dotnet restore --configfile nuget.config

# Copy the rest of the code
COPY . .

# Build and publish
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks

#-------------------------------------------------------------------------------------
# Stage 2: Final Runtime Image
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create non-root user
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser

# Install curl for health checks
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy published files
COPY --from=build /app/publish/webapi /app/webapi/
COPY --from=build /app/publish/tasks /app/tasks/

# Create necessary directories
RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection

# Set permissions
RUN chown -R appuser:appuser /app && \
    chmod -R u=rX,g=rX,o= /app/webapi && \
    chmod -R u=rX,g=rX,o= /app/tasks && \
    chmod -R u=rwx,g=,o= /app/telegram-sessions && \
    chmod -R u=rwx,g=,o= /app/data-protection

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
