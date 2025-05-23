#-------------------------------------------------------------------------------------
# Stage 1: Restore Dependencies & Diagnostics
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS restore_diagnostics
WORKDIR /src

# Copy Solution Level Files FIRST
COPY Directory.Packages.props ./
COPY Directory.Build.props ./
# COPY YourSolutionName.sln ./
# COPY NuGet.config ./

# Copy ALL Project Files NEXT
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]

# --- DIAGNOSTICS: Print critical file contents ---
# (Keeping these for one more run, just in case)
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

# Restore Dependencies: Quiet verbosity + attempt to suppress common warnings
# Common NuGet warning codes from your logs: NU1602, NU1603, NU1604, NU1608, NU1701, NU1504
# We also want to see errors like NU1109 and NU1202 if they happen.
RUN dotnet restore "WebAPI/WebAPI.csproj" \
    --verbosity quiet \
    /p:NoWarn="NU1602;NU1603;NU1604;NU1608;NU1701;NU1504" \
    || (echo "Restore failed, running with diagnostic verbosity for more details:" && dotnet restore "WebAPI/WebAPI.csproj" --verbosity diagnostic && exit 1)


#-------------------------------------------------------------------------------------
# Stage 2: Build & Publish Application
#-------------------------------------------------------------------------------------
FROM restore_diagnostics AS build
WORKDIR /src
COPY . .

# Build and Publish WebAPI
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build/webapi --no-restore --verbosity quiet /p:NoWarn="NU1602;NU1603;NU1604;NU1608;NU1701;NU1504"
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi --no-restore --verbosity quiet /p:GenerateRuntimeConfigurationFiles=true /p:NoWarn="NU1602;NU1603;NU1604;NU1608;NU1701;NU1504"

# Build and Publish BackgroundTasks
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build/tasks --no-restore --verbosity quiet /p:NoWarn="NU1602;NU1603;NU1604;NU1608;NU1701;NU1504"
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks --no-restore --verbosity quiet /p:GenerateRuntimeConfigurationFiles=true /p:NoWarn="NU1602;NU1603;NU1604;NU1608;NU1701;NU1504"

#-------------------------------------------------------------------------------------
# Stage 3: Final Runtime Image
#-------------------------------------------------------------------------------------
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