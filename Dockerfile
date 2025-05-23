#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install Go and netcat
RUN apt-get update && apt-get install -y golang-go netcat-traditional && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy solution and project files
COPY ["ForexTradingBot.sln", "Directory.Packages.props", "nuget.config", "./"]
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Core/Core.csproj", "Core/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]

# Install EF Core tools
RUN dotnet tool install --global dotnet-ef

# Restore packages
RUN dotnet restore "WebAPI/WebAPI.csproj" --configfile nuget.config

# Copy the rest of the code
COPY . .

# Build Go log monitor
WORKDIR /src/WebAPI
RUN go build -o docker_log_monitor docker_log_monitor.go

# Build .NET applications
WORKDIR /src
RUN dotnet build "Core/Core.csproj" -c Release -o /app/build/core
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build/webapi
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build/tasks

# Publish applications
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks

#-------------------------------------------------------------------------------------
# Stage 2: Final Runtime Image
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create non-root user
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser

# Install curl, netcat, Go, and Docker CLI for health checks and log monitoring
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    netcat-traditional \
    golang-go \
    docker.io \
    && rm -rf /var/lib/apt/lists/*

# Copy the Go log monitor
COPY --from=build /src/WebAPI/docker_log_monitor .

# Copy the published applications
COPY --from=build /app/publish/webapi ./webapi
COPY --from=build /app/publish/tasks ./tasks

# Copy startup script
COPY start.sh /app/start.sh

# Create necessary directories
RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection

# Set permissions
RUN chown -R appuser:appuser /app && \
    chmod -R u=rX,g=rX,o= /app/webapi && \
    chmod -R u=rX,g=rX,o= /app/tasks && \
    chmod -R u=rwx,g=,o= /app/telegram-sessions && \
    chmod -R u=rwx,g=,o= /app/data-protection && \
    chmod +x /app/docker_log_monitor && \
    chmod +x /app/start.sh

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Logging Configuration
ENV Logging__LogLevel__Default=Debug
ENV Logging__LogLevel__Microsoft.AspNetCore=Warning

# Database Configuration
ENV DatabaseProvider=Postgres
ENV ConnectionStrings__DefaultConnection="Host=db;Port=5432;Database=forextrading;Username=forexuser;Password=${DB_PASSWORD}"
ENV ConnectionStrings__Hangfire="Host=db;Port=5432;Database=forextrading;Username=forexuser;Password=${DB_PASSWORD}"

# Telegram Panel Configuration
ENV TelegramPanel__BotToken="${TELEGRAM_BOT_TOKEN}"
ENV TelegramPanel__UseWebhook=false
ENV TelegramPanel__PollingInterval=0
ENV TelegramPanel__AdminUserIds__0=5094837833
ENV TelegramPanel__EnableDebugMode=true

# Telegram User API Configuration
ENV TelegramUserApi__ApiId="${TELEGRAM_API_ID}"
ENV TelegramUserApi__ApiHash="${TELEGRAM_API_HASH}"
ENV TelegramUserApi__SessionPath=/app/telegram-sessions/telegram_user.session
ENV TelegramUserApi__VerificationCodeSource=Console
ENV TelegramUserApi__TwoFactorPasswordSource=Console

# CryptoPay Configuration
ENV CryptoPay__ApiToken="${CRYPTOPAY_API_TOKEN}"
ENV CryptoPay__BaseUrl="https://testnet-pay.crypt.bot/api/"
ENV CryptoPay__IsTestnet=true
ENV CryptoPay__WebhookSecretForCryptoPay="${CRYPTOPAY_WEBHOOK_SECRET}"
ENV CryptoPay__ApiKey="${CRYPTOPAY_API_KEY}"
ENV CryptoPay__WebhookSecret="${CRYPTOPAY_WEBHOOK_SECRET}"

# Forwarding Rules Configuration
ENV ForwardingRules__0__SourceChannelId=-1001854636317
ENV ForwardingRules__0__TargetChannelId=-1002696634930
ENV ForwardingRules__0__IsEnabled=true
ENV ForwardingRules__0__EditOptions__AppendText="\n\nForwarded by MyBotReza"
ENV ForwardingRules__0__EditOptions__RemoveLinks=false
ENV ForwardingRules__0__EditOptions__AllowedMessageTypes__0=Text
ENV ForwardingRules__0__EditOptions__AllowedMessageTypes__1=Photo
ENV ForwardingRules__0__EditOptions__AllowedMessageTypes__2=Video

# Hangfire Settings
ENV HangfireSettings__StorageType=PostgreSQL
ENV HangfireSettings__ConnectionString="Host=db;Port=5432;Database=forextrading;Username=forexuser;Password=${DB_PASSWORD}"

# Serilog Configuration
ENV Serilog__MinimumLevel__Default=Information
ENV Serilog__MinimumLevel__Override__Microsoft=Warning
ENV Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime=Information
ENV Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore=Warning
ENV Serilog__MinimumLevel__Override__System=Warning
ENV Serilog__Enrich__0=FromLogContext
ENV Serilog__Properties__Application=ForexTradingBot

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 80

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost/health || exit 1

# Run the application
ENTRYPOINT ["/app/start.sh"]
