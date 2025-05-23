# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# --- Crucial: Copy solution-level files FIRST ---
# Copy the .sln file (if you restore at solution level, which is good practice)
# COPY YourSolutionName.sln ./ 
# Copy Directory.Packages.props for Central Package Management
COPY Directory.Packages.props ./
# Copy Directory.Build.props if you have one
# COPY Directory.Build.props ./
# Copy NuGet.config if you have one
# COPY NuGet.config ./

# --- Now copy project files ---
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]       # Added this, was missing
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"] # Added this, was missing

# --- Restore dependencies ---
# It's often better to restore at the solution level if you copied the .sln file
# RUN dotnet restore YourSolutionName.sln
# Or, if restoring project by project, ensure all projects that need restoring are done
# For a single restore command that considers all, targeting the main project is often okay if it references others.
# However, since your previous logs showed multiple projects failing restore,
# let's stick to restoring the main one, assuming it will chain correctly *now that CPM is present*.
RUN dotnet restore "WebAPI/WebAPI.csproj"
# If issues persist, you might need to restore each project that has direct dependencies
# or, ideally, restore the .sln file.

# Copy the rest of the source code
COPY . .

# Build and publish
# The build command will implicitly restore if needed, but the explicit restore earlier is good for layer caching.
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build --no-restore # Add --no-restore
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish --no-restore # Add --no-restore

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install necessary tools
# Consider if 'curl' is truly needed in the final runtime image. It's good for HEALTHCHECK, but keep images minimal.
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy published files
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production # This can be overridden at runtime

# Expose port
EXPOSE 80

# Create a non-root user for better security (matches your previous log's appuser setup)
RUN adduser --disabled-password --gecos "" appuser && \
    chown -R appuser:appuser /app

# Create directories and set permissions (if these are needed by your app and not created by the app itself)
# These were in your GitHub Actions log earlier, so they might be relevant
RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection && \
    chown -R appuser:appuser /app/telegram-sessions && \
    chown -R appuser:appuser /app/data-protection

USER appuser # Run the application as a non-root user

# Health check
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
    CMD curl -f http://localhost/health || exit 1 # This will run as 'appuser'

# Run the app
ENTRYPOINT ["dotnet", "WebAPI.dll"]