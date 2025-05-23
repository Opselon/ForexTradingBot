# Stage 1: Build SDK Environment & Restore Dependencies
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY Directory.Build.props ./

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
RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity minimal \
    || (echo "Restore failed, running with diagnostic verbosity for more details:" && dotnet restore "WebAPI/WebAPI.csproj" --verbosity diagnostic && exit 1)

# Stage 2: Copy the rest of the code, Build & Publish Applications
WORKDIR /src
COPY . .

# Build and Publish WebAPI
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build/webapi --no-restore --verbosity minimal
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi --no-restore --verbosity minimal /p:GenerateRuntimeConfigurationFiles=true

# Build and Publish BackgroundTasks
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build/tasks --no-restore --verbosity minimal
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks --no-restore --verbosity minimal /p:GenerateRuntimeConfigurationFiles=true

# Stage 3: Final Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

RUN adduser --system --group --disabled-password --gecos "" --home /app appuser
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish/webapi /app/webapi/
COPY --from=build /app/publish/tasks /app/tasks/

COPY --from=build /src/WebAPI/appsettings.json /app/webapi/appsettings.json
COPY --from=build /src/WebAPI/appsettings.Production.json /app/webapi/appsettings.Production.json
COPY --from=build /src/BackgroundTasks/appsettings.json /app/tasks/appsettings.json
COPY --from=build /src/BackgroundTasks/appsettings.Production.json /app/tasks/appsettings.Production.json

RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection
RUN chown -R appuser:appuser /app
RUN chmod -R u=rX,g=rX,o= /app/webapi && \
    chmod -R u=rX,g=rX,o= /app/tasks && \
    chmod -R u=rwx,g=,o= /app/telegram-sessions && \
    chmod -R u=rwx,g=,o= /app/data-protection

ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TELEGRAM_SESSION_PATH=/app/telegram-sessions
ENV DOTNET_RUNNING_IN_CONTAINER=true
USER appuser
EXPOSE 80
WORKDIR /app/webapi
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1
ENTRYPOINT ["dotnet", "WebAPI.dll"]