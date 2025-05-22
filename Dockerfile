# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files and restore dependencies
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]

RUN dotnet restore "WebAPI/WebAPI.csproj"

# Copy the rest of the code
COPY . .

# Build and publish WebAPI
RUN dotnet build "WebAPI/WebAPI.csproj" -c Release -o /app/build
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish/webapi --no-restore /p:ExcludeAppSettings=true

# Build and publish BackgroundTasks
RUN dotnet build "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/build-tasks
RUN dotnet publish "BackgroundTasks/BackgroundTasks.csproj" -c Release -o /app/publish/tasks --no-restore /p:ExcludeAppSettings=true

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create non-root user
RUN adduser --disabled-password --gecos "" appuser

# Create secure directories
RUN mkdir -p /app/telegram-sessions && \
    mkdir -p /app/data-protection && \
    chown -R appuser:appuser /app

# Copy published files
COPY --from=build /app/publish/webapi /app/webapi
COPY --from=build /app/publish/tasks /app/tasks

# Copy appsettings files separately
COPY --from=build /src/WebAPI/appsettings.json /app/webapi/appsettings.json
COPY --from=build /src/WebAPI/appsettings.Production.json /app/webapi/appsettings.Production.json
COPY --from=build /src/BackgroundTasks/appsettings.json /app/tasks/appsettings.json
COPY --from=build /src/BackgroundTasks/appsettings.Production.json /app/tasks/appsettings.Production.json

# Set secure permissions
RUN chown -R appuser:appuser /app && \
    chmod -R 755 /app && \
    chmod -R 700 /app/telegram-sessions && \
    chmod -R 700 /app/data-protection

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TELEGRAM_SESSION_PATH=/app/telegram-sessions
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Switch to non-root user
USER appuser

EXPOSE 80
ENTRYPOINT ["dotnet", "WebAPI/WebAPI.dll"] 