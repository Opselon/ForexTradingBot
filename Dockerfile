# ============================================
# Multi-Stage Dockerfile for ForexTradingBot (.NET 9)
# ============================================

# Stage 1: Build stage - restore dependencies, build, publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder
WORKDIR /src

# Copy project files first to leverage Docker layer caching
# NOTE: test projects must be present because the .sln references them,
# but only WebAPI is built/published into the runtime image.
COPY *.sln ./
COPY Application/Application.csproj ./Application/
COPY Domain/Domain.csproj ./Domain/
COPY Infrastructure/Infrastructure.csproj ./Infrastructure/
COPY Shared/Shared.csproj ./Shared/
COPY BackgroundTasks/BackgroundTasks.csproj ./BackgroundTasks/
COPY TelegramPanel/TelegramPanel.csproj ./TelegramPanel/
COPY WebAPI/WebAPI.csproj ./WebAPI/
COPY Application.Tests/Application.Tests.csproj ./Application.Tests/
COPY Tests.Application/Tests.Application.csproj ./Tests.Application/

# Restore dependencies (cached until csproj changes)
RUN dotnet restore ForexTradingBot.sln

# Copy all source code
COPY Application/ ./Application/
COPY Domain/ ./Domain/
COPY Infrastructure/ ./Infrastructure/
COPY Shared/ ./Shared/
COPY BackgroundTasks/ ./BackgroundTasks/
COPY TelegramPanel/ ./TelegramPanel/
COPY WebAPI/ ./WebAPI/

# Publish the WebAPI (entry point) in Release mode
# --property:ErrorOnDuplicatePublishOutputFiles=false avoids NETSDK1152
# when project references ship their own appsettings.json
RUN dotnet publish WebAPI/WebAPI.csproj -c Release --no-restore -o /app/publish \
    -p:ErrorOnDuplicatePublishOutputFiles=false

# Stage 2: Runtime stage - minimal runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install curl for the container healthcheck
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Set timezone and runtime defaults
ENV TZ=Asia/Tehran
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Create non-root user for security (principle of least privilege)
RUN groupadd -r forexuser -g 1000 && \
    useradd -r -g forexuser forexuser -m -d /app

COPY --from=builder /app/publish .

# Fix permissions
RUN chown -R forexuser:forexuser /app
USER forexuser

# Expose port for API
EXPOSE 8080

ENTRYPOINT ["dotnet", "WebAPI.dll"]
