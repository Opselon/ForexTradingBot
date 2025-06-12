#-------------------------------------------------------------------------------------
# Stage 1: Restore Dependencies
# This stage's only job is to download NuGet packages.
# It copies only the files needed for restore, so this layer is cached very effectively.
# It will only re-run if your project dependencies change.
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS restore
WORKDIR /src

# Copy the solution file and all project files into their respective directories
COPY ForexTradingBot.sln .
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]
COPY ["Application.Tests/Application.Tests.csproj", "Application.Tests/"]

# Run the restore command for the entire solution. This creates clean 'obj' folders.
RUN dotnet restore "ForexTradingBot.sln"


#-------------------------------------------------------------------------------------
# Stage 2: Build and Publish
# This stage takes the restored packages from the 'restore' stage and compiles the application.
# It starts from the 'restore' stage, inheriting its state.
#-------------------------------------------------------------------------------------
FROM restore AS build
WORKDIR /src

# Copy the rest of the source code.
# The .dockerignore file will prevent your local bin/obj folders from being copied,
# so this command copies all your .cs files without overwriting the clean 'obj'
# folders that were created in the 'restore' stage.
COPY . .

# Publish the main application.
# The --no-restore flag is crucial here; it tells the command to use the
# packages that were already restored in the previous stage.
RUN dotnet publish "WebAPI/WebAPI.csproj" -c Release -o /app/publish --no-restore


#-------------------------------------------------------------------------------------
# Stage 3: Final Runtime Image
# This is the small, secure image that will actually run in production.
# It starts from the lightweight ASP.NET runtime image.
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install curl, which is a tiny but useful tool needed for the HEALTHCHECK.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy the final, published application from the 'build' stage.
COPY --from=build /app/publish .

# Create a dedicated, non-root user for enhanced security.
RUN adduser --system --group --disabled-password --gecos "" --home /app appuser
USER appuser

# Set environment variables for the ASP.NET Core runtime.
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose the port that the application will listen on.
EXPOSE 80

# Define a health check to let Docker (or orchestrators like Kubernetes) know if the application is healthy.
# IMPORTANT: Change '/health' to your actual health check endpoint if you have one.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Define the entry point for the container, which is the command that runs your application.
ENTRYPOINT ["dotnet", "WebAPI.dll"]