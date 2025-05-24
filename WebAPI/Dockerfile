# Stage 1: Build SDK Environment & Restore Dependencies
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for better layer caching
COPY Directory.Packages.props ./
COPY Directory.Build.props ./

# Copy all project files
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]

# --- DIAGNOSTICS: Kept for one final check (Optional: Remove after successful build) ---
RUN echo "--- DIAGNOSTIC: Content of /src/Directory.Packages.props ---" && \
    (cat /src/Directory.Packages.props || echo "DIAGNOSTIC: Directory.Packages.props not found or cat failed") && \
    echo "--- DIAGNOSTIC: End of /src/Directory.Packages.props ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Content of /src/Directory.Build.props ---" && \
    (cat /src/Directory.Build.props || echo "DIAGNOSTIC: Directory.Build.props not found or cat failed") && \
    echo "--- DIAGNOSTIC: End of /src/Directory.Build.props ---"

# Restore Dependencies
RUN dotnet restore "ForexTradingBot.sln" --configfile nuget.config

# Stage 2: Build & Publish Applications
WORKDIR /src
COPY . .

# Build and Publish WebAPI
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build/webapi --no-restore
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi --no-restore /p:GenerateRuntimeConfigurationFiles=true

# Build and Publish BackgroundTasks
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build/tasks --no-restore
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks --no-restore /p:GenerateRuntimeConfigurationFiles=true

# Build and Publish TelegramPanel
RUN dotnet build "TelegramPanel/TelegramPanel.csproj" -c Release -o /app/build/telegrampanel --no-restore
RUN dotnet publish "TelegramPanel/TelegramPanel.csproj" -c Release -o /app/publish/telegrampanel --no-restore /p:GenerateRuntimeConfigurationFiles=true

# Stage 3: Final Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create non-root user and install dependencies
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        netcat-traditional \
    && rm -rf /var/lib/apt/lists/*

# Create necessary directories
RUN mkdir -p /app/telegram-sessions /app/data-protection

# Copy published applications
COPY --from=build /app/publish/webapi /app/webapi/
COPY --from=build /app/publish/tasks /app/tasks/
COPY --from=build /app/publish/telegrampanel /app/telegrampanel/

# Copy configuration files
COPY --from=build /src/WebAPI/appsettings.json /app/webapi/appsettings.json
COPY --from=build /src/WebAPI/appsettings.Production.json /app/webapi/appsettings.Production.json
COPY --from=build /src/BackgroundTasks/appsettings.json /app/tasks/appsettings.json
COPY --from=build /src/BackgroundTasks/appsettings.Production.json /app/tasks/appsettings.Production.json
COPY --from=build /src/TelegramPanel/appsettings.json /app/telegrampanel/appsettings.json
COPY --from=build /src/TelegramPanel/appsettings.Production.json /app/telegrampanel/appsettings.Production.json

# Set proper permissions
RUN chown -R appuser:appuser /app && \
    chmod -R u=rX,g=rX,o= /app/webapi && \
    chmod -R u=rX,g=rX,o= /app/tasks && \
    chmod -R u=rX,g=rX,o= /app/telegrampanel && \
    chmod -R u=rwx,g=,o= /app/telegram-sessions && \
    chmod -R u=rwx,g=,o= /app/data-protection

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80 \
    ASPNETCORE_ENVIRONMENT=Production \
    TELEGRAM_SESSION_PATH=/app/telegram-sessions \
    DOTNET_RUNNING_IN_CONTAINER=true

# Switch to non-root user
USER appuser

# Expose port and set working directory
EXPOSE 80
WORKDIR /app/webapi

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1

# Entry point
ENTRYPOINT ["dotnet", "WebAPI.dll"]