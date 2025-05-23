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

# --- DIAGNOSTICS: Print file contents (ADD THESE LINES) ---
RUN echo "--- Content of Directory.Packages.props ---" && \
    cat Directory.Packages.props && \
    echo "--- End of Directory.Packages.props ---" && \
    echo "--- Relevant content of TelegramPanel.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*Scrutor" TelegramPanel/TelegramPanel.csproj || echo "Scrutor grep not matched in TelegramPanel") && \
    (grep -i -A3 -B3 "PackageReference.*HangFire" TelegramPanel/TelegramPanel.csproj || echo "HangFire grep not matched in TelegramPanel") && \
    (grep -i -A3 -B3 "FrameworkReference.*AspNetCore.App" TelegramPanel/TelegramPanel.csproj || echo "FrameworkRef AspNetCore.App grep not matched in TelegramPanel") && \
    (grep -i -A3 -B3 "PackageReference.*Microsoft.AspNetCore.Mvc.Core" TelegramPanel/TelegramPanel.csproj || echo "Mvc.Core PackageRef grep not matched in TelegramPanel") && \
    echo "--- End of TelegramPanel.csproj ---" && \
    echo "--- Relevant content of Infrastructure.csproj ---" && \
    (grep -i -A3 -B3 "PackageReference.*Scrutor" Infrastructure/Infrastructure.csproj || echo "Scrutor grep not matched in Infrastructure") && \
    echo "--- End of Infrastructure.csproj ---"
# Add more greps for other projects/packages if needed

# --- Restore Dependencies ---
# Using minimal verbosity. If the cat/grep show correct files but restore still fails,
# then try '--verbosity diagnostic' to understand Nuget's decisions.
RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity minimal
# RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity diagnostic # <-- USE THIS IF STILL FAILING AFTER CHECKING CAT/GREP OUTPUT


#-------------------------------------------------------------------------------------
# Stage 2: Build & Publish Application
#-------------------------------------------------------------------------------------
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