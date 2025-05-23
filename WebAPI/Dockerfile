#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /src

# --- Copy Solution Level Files for Restore Layer ---
# This layer benefits most from caching if these files don't change often.
COPY Directory.Packages.props ./
# COPY YourSolutionName.sln ./ # UNCOMMENT if you want to restore at solution level
# COPY NuGet.config ./         # UNCOMMENT if you have a solution-specific NuGet.config
# COPY Directory.Build.props ./ # UNCOMMENT if you use this

# --- Copy Project Files for Restore ---
# Copy only the .csproj files to leverage Docker cache for the restore step.
# If a .csproj changes, only this layer and subsequent ones are rebuilt.
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]
# Add any other .csproj files your solution might have, e.g., test projects if they were part of restore issues.

# --- Restore Dependencies ---
# Using minimal verbosity to reduce log noise but still show errors/warnings.
# If debugging difficult restore issues, change to '--verbosity diagnostic' temporarily.
# RUN dotnet restore YourSolutionName.sln --verbosity minimal # UNCOMMENT and use this if you copied .sln
RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity minimal

#-------------------------------------------------------------------------------------
# Stage 2: Build & Publish Application
#-------------------------------------------------------------------------------------
# This stage depends on the restore layer (build-env)
# Copy the rest of the source code
WORKDIR /src # Ensure we are in the same WORKDIR if using layers explicitly like this
COPY . .

# --- Build and Publish the Application ---
# Using minimal verbosity. Ensure '--no-restore' as dependencies are already restored.
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build --no-restore --verbosity minimal
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish --no-restore --verbosity minimal

#-------------------------------------------------------------------------------------
# Stage 3: Final Runtime Image
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# --- Install Runtime Dependencies (if any) ---
# Only install essential tools needed by the application at runtime.
# curl is included here for the HEALTHCHECK.
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

# --- Copy Published Application from Build Stage ---
COPY --from=build-env /app/publish . # Changed from 'build' to 'build-env' as per updated stage name

# --- Security: Create and Use a Non-Root User ---
# Create a non-root user and group for the application
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser
# chown /app to the new user is implicitly done if --home /app works, but being explicit is okay
# Ensure app files are owned by appuser if not already.
RUN chown -R appuser:appuser /app

# --- Application-Specific Directories and Permissions (if needed) ---
# These should ideally be created by the app or init scripts if their content is dynamic.
# If static and needed at startup, create them and set ownership.
RUN mkdir -p /app/telegram-sessions && \
    chown -R appuser:appuser /app/telegram-sessions
RUN mkdir -p /app/data-protection && \
    chown -R appuser:appuser /app/data-protection

# Switch to the non-root user
USER appuser

# --- Environment Variables ---
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production # Can be overridden at runtime by docker run -e or docker-compose

# --- Port Exposure ---
EXPOSE 80

# --- Health Check ---
# Ensures the application is running and responsive.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1

# --- Entrypoint ---
# Runs the application.
ENTRYPOINT ["dotnet", "WebAPI.dll"]