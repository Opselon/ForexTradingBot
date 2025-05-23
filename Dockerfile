#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy Solution Level Files FIRST
COPY Directory.Packages.props ./
COPY Directory.Build.props ./ # Assuming you use this as you mentioned
# COPY YourSolutionName.sln ./ # Uncomment if you restore at solution level
# COPY NuGet.config ./        # Uncomment if you have a solution-specific NuGet.config

# Copy ALL Project Files NEXT
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]

# DIAGNOSTICS: Print critical file contents
RUN echo "--- DIAGNOSTIC: Content of /src/Directory.Packages.props ---" && \
    (cat /src/Directory.Packages.props || echo "DIAGNOSTIC: Directory.Packages.props not found or cat failed") && \
    echo "--- DIAGNOSTIC: End of /src/Directory.Packages.props ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Content of /src/Directory.Build.props ---" && \
    (cat /src/Directory.Build.props || echo "DIAGNOSTIC: Directory.Build.props not found or cat failed") && \
    echo "--- DIAGNOSTIC: End of /src/Directory.Build.props ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/TelegramPanel/TelegramPanel.csproj ---" && \
    (grep -i -E "PackageReference.*Scrutor|PackageReference.*HangFire|FrameworkReference.*AspNetCore.App|PackageReference.*Microsoft.AspNetCore.Mvc.Core" /src/TelegramPanel/TelegramPanel.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference/FrameworkReference in TelegramPanel") && \
    echo "--- DIAGNOSTIC: End of /src/TelegramPanel/TelegramPanel.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/Infrastructure/Infrastructure.csproj ---" && \
    (grep -i -E "PackageReference.*Scrutor|PackageReference.*HangFire|PackageReference.*Dapper" /src/Infrastructure/Infrastructure.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference in Infrastructure") && \
    echo "--- DIAGNOSTIC: End of /src/Infrastructure/Infrastructure.csproj ---"
    # You can add more grep commands here for other csproj files if needed

# Restore Dependencies with DIAGNOSTIC verbosity
RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity diagnostic

#-------------------------------------------------------------------------------------
# Stage 2: Copy the rest of the code, Build & Publish Applications
#-------------------------------------------------------------------------------------
WORKDIR /src
COPY . .

# Build and Publish WebAPI
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build/webapi --no-restore --verbosity minimal
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi --no-restore --verbosity minimal /p:GenerateRuntimeConfigurationFiles=true

# Build and Publish BackgroundTasks
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build/tasks --no-restore --verbosity minimal
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks --no-restore --verbosity minimal /p:GenerateRuntimeConfigurationFiles=true

#-------------------------------------------------------------------------------------
# Stage 3: Final Runtime Image
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create Non-Root User
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser

# Install Runtime Dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy Published Applications from Build Stage
COPY --from=build /app/publish/webapi /app/webapi/
COPY --from=build /app/publish/tasks /app/tasks/

# Copy appsettings files separately
COPY --from=build /src/WebAPI/appsettings.json /app/webapi/appsettings.json
COPY --from=build /src/WebAPI/appsettings.Production.json /app/webapi/appsettings.Production.json
COPY --from=build /src/BackgroundTasks/appsettings.json /app/tasks/appsettings.json
COPY --from=build /src/BackgroundTasks/appsettings.Production.json /app/tasks/appsettings.Production.json

# Create Secure Directories and Set Ownership/Permissions
RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection

RUN chown -R appuser:appuser /app
RUN chmod -R u=rX,g=rX,o= /app/webapi && \
    chmod -R u=rX,g=rX,o= /app/tasks && \
    chmod -R u=rwx,g=,o= /app/telegram-sessions && \
    chmod -R u=rwx,g=,o= /app/data-protection

# Environment Variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TELEGRAM_SESSION_PATH=/app/telegram-sessions
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Switch to Non-Root User
USER appuser

# Port Exposure
EXPOSE 80

# Entrypoint and Health Check for WebAPI
WORKDIR /app/webapi
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1
ENTRYPOINT ["dotnet", "WebAPI.dll"]
