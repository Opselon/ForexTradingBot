# Stage 1: Build the Application (Unified Stage)
# This single-stage approach is the most robust way to solve persistent
# file contamination issues between the host and the container.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy ALL files from your local project directory into the container.
# At this point, the container has your source code AND the contaminated
# Windows-specific bin/obj folders.
COPY . .

# --- THE CRITICAL SANITIZATION STEP ---
# Run a fresh `dotnet restore` on the entire solution INSIDE the container.
# This command completely ignores and overwrites the contaminated obj folders
# with new, clean ones that have correct Linux paths. This sanitizes the
# entire build environment and is the key to fixing the error.
RUN dotnet restore "ForexTradingBot.sln"

# Now that the environment is clean, publish the application.
# We do not use --no-restore, allowing publish to use the sanitized assets.
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish


# Stage 2: Final Runtime Image
# This stage remains the same, creating a small and secure final image.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install curl, which is a tiny but useful tool needed for the HEALTHCHECK.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy the published application from the 'build' stage.
COPY --from=build /app/publish .

# Create a dedicated, non-root user for security.
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser
USER appuser

# Set environment variables for the ASP.NET Core runtime.
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose the port that the application will listen on.
EXPOSE 80

# Define a health check. IMPORTANT: Change '/health' to your actual health check endpoint.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Define the entry point for the container.
ENTRYPOINT ["dotnet", "WebAPI.dll"]