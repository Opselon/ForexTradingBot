#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env # <-- Used this stage name for COPY --from later
WORKDIR /src

# --- Copy Solution Level Files for Restore Layer ---
COPY Directory.Packages.props ./
# COPY YourSolutionName.sln ./
# COPY NuGet.config ./
# COPY Directory.Build.props ./

# --- Copy Project Files for Restore ---
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]

# --- DIAGNOSTICS: Print file contents (CRUCIAL FOR DEBUGGING) ---
RUN echo "--- DIAGNOSTIC: Content of /src/Directory.Packages.props ---" && \
    cat /src/Directory.Packages.props && \
    echo "--- DIAGNOSTIC: End of /src/Directory.Packages.props ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/TelegramPanel/TelegramPanel.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*Scrutor" /src/TelegramPanel/TelegramPanel.csproj || echo "DIAGNOSTIC: Scrutor grep not matched in TelegramPanel") && \
    (grep -i -A3 -B3 "PackageReference.*HangFire" /src/TelegramPanel/TelegramPanel.csproj || echo "DIAGNOSTIC: HangFire grep not matched in TelegramPanel") && \
    (grep -i -A3 -B3 "FrameworkReference.*AspNetCore.App" /src/TelegramPanel/TelegramPanel.csproj || echo "DIAGNOSTIC: FrameworkRef AspNetCore.App grep not matched in TelegramPanel") && \
    (grep -i -A3 -B3 "PackageReference.*Microsoft.AspNetCore.Mvc.Core" /src/TelegramPanel/TelegramPanel.csproj || echo "DIAGNOSTIC: Mvc.Core PackageRef grep not matched in TelegramPanel") && \
    echo "--- DIAGNOSTIC: End of /src/TelegramPanel/TelegramPanel.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/Infrastructure/Infrastructure.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*Scrutor" /src/Infrastructure/Infrastructure.csproj || echo "DIAGNOSTIC: Scrutor grep not matched in Infrastructure") && \
    (grep -i -A3 -B3 "PackageReference.*HangFire" /src/Infrastructure/Infrastructure.csproj || echo "DIAGNOSTIC: HangFire grep not matched in Infrastructure") && \
    echo "--- DIAGNOSTIC: End of /src/Infrastructure/Infrastructure.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/BackgroundTasks/BackgroundTasks.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*Scrutor" /src/BackgroundTasks/BackgroundTasks.csproj || echo "DIAGNOSTIC: Scrutor grep not matched in BackgroundTasks") && \
    (grep -i -A3 -B3 "PackageReference.*HangFire" /src/BackgroundTasks/BackgroundTasks.csproj || echo "DIAGNOSTIC: HangFire grep not matched in BackgroundTasks") && \
    echo "--- DIAGNOSTIC: End of /src/BackgroundTasks/BackgroundTasks.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/Application/Application.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*AutoMapper" /src/Application/Application.csproj || echo "DIAGNOSTIC: AutoMapper grep not matched in Application") && \
    (grep -i -A3 -B3 "PackageReference.*MediatR" /src/Application/Application.csproj || echo "DIAGNOSTIC: MediatR grep not matched in Application") && \
    echo "--- DIAGNOSTIC: End of /src/Application/Application.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/WebAPI/WebAPI.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*HangFire.SqlServer" /src/WebAPI/WebAPI.csproj || echo "DIAGNOSTIC: HangFire.SqlServer grep not matched in WebAPI") && \
    (grep -i -A3 -B3 "PackageReference.*Scrutor" /src/WebAPI/WebAPI.csproj || echo "DIAGNOSTIC: Scrutor grep not matched in WebAPI") && \
    echo "--- DIAGNOSTIC: End of /src/WebAPI/WebAPI.csproj ---"

# --- Restore Dependencies ---
# Using minimal verbosity. If the cat/grep show correct files but restore still fails,
# then try '--verbosity diagnostic' to understand Nuget's decisions.
RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity minimal
# RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity diagnostic # <-- USE THIS IF STILL FAILING AFTER CHECKING CAT/GREP OUTPUT


#-------------------------------------------------------------------------------------
# Stage 2: Build & Publish Application
#-------------------------------------------------------------------------------------
WORKDIR /src # Ensuring WORKDIR is set correctly for the COPY . .
COPY . . # Copy all source code now

# --- Build and Publish the Application ---
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build --no-restore --verbosity minimal
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish --no-restore --verbosity minimal

#-------------------------------------------------------------------------------------
# Stage 3: Final Runtime Image
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build-env /app/publish . # Ensure this matches the SDK stage name

RUN adduser --system --group --disabled-password --gecos "" --home /app appuser
RUN chown -R appuser:appuser /app

RUN mkdir -p /app/telegram-sessions && \
    chown -R appuser:appuser /app/telegram-sessions
RUN mkdir -p /app/data-protection && \
    chown -R appuser:appuser /app/data-protection

USER appuser

ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1
ENTRYPOINT ["dotnet", "WebAPI.dll"]