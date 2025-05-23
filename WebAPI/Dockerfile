#-------------------------------------------------------------------------------------
# Stage 1: Build SDK Environment & Restore Dependencies
#-------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /src

# --- Copy Solution Level Files for Restore Layer ---
COPY Directory.Packages.props ./
# COPY YourSolutionName.sln ./ # اگر می‌خواهید در سطح سلوشن restore کنید
# COPY NuGet.config ./         # اگر فایل NuGet.config مخصوص سلوشن دارید
# COPY Directory.Build.props ./ # اگر از این استفاده می‌کنید

# --- Copy Project Files for Restore ---
COPY ["WebAPI/WebAPI.csproj", "WebAPI/"]
COPY ["Application/Application.csproj", "Application/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
COPY ["Shared/Shared.csproj", "Shared/"]
COPY ["TelegramPanel/TelegramPanel.csproj", "TelegramPanel/"]
COPY ["BackgroundTasks/BackgroundTasks.csproj", "BackgroundTasks/"]

# --- DIAGNOSTICS: Print file contents (بسیار مهم برای دیباگ) ---
RUN echo "--- DIAGNOSTIC: Content of /src/Directory.Packages.props ---" && \
    cat /src/Directory.Packages.props && \
    echo "--- DIAGNOSTIC: End of /src/Directory.Packages.props ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/TelegramPanel/TelegramPanel.csproj ---" && \
    (grep -i -E "PackageReference.*Scrutor|PackageReference.*HangFire|FrameworkReference.*AspNetCore.App|PackageReference.*Microsoft.AspNetCore.Mvc.Core" /src/TelegramPanel/TelegramPanel.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference/FrameworkReference in TelegramPanel") && \
    echo "--- DIAGNOSTIC: End of /src/TelegramPanel/TelegramPanel.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/Infrastructure/Infrastructure.csproj ---" && \
    (grep -i -E "PackageReference.*Scrutor|PackageReference.*HangFire|PackageReference.*Dapper" /src/Infrastructure/Infrastructure.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference in Infrastructure") && \
    echo "--- DIAGNOSTIC: End of /src/Infrastructure/Infrastructure.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/BackgroundTasks/BackgroundTasks.csproj ---" && \
    (grep -i -E "PackageReference.*Scrutor|PackageReference.*HangFire" /src/BackgroundTasks/BackgroundTasks.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference in BackgroundTasks") && \
    echo "--- DIAGNOSTIC: End of /src/BackgroundTasks/BackgroundTasks.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/Application/Application.csproj ---" && \
    (grep -i -E "PackageReference.*AutoMapper|PackageReference.*MediatR|PackageReference.*HangFire.Core" /src/Application/Application.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference in Application") && \
    echo "--- DIAGNOSTIC: End of /src/Application/Application.csproj ---" && \
    echo " " && \
    echo "--- DIAGNOSTIC: Relevant content of /src/WebAPI/WebAPI.csproj ---" && \
    (grep -i -E "PackageReference.*HangFire.SqlServer|PackageReference.*Scrutor" /src/WebAPI/WebAPI.csproj -B3 -A3 || echo "DIAGNOSTIC: Grep found no relevant PackageReference in WebAPI") && \
    echo "--- DIAGNOSTIC: End of /src/WebAPI/WebAPI.csproj ---"

# --- Restore Dependencies ---
# RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity minimal
# اگر پس از بررسی خروجی cat/grep همچنان مشکل پابرجا بود، این خط را با verbosity diagnostic اجرا کنید:
RUN dotnet restore "WebAPI/WebAPI.csproj" --verbosity diagnostic


#-------------------------------------------------------------------------------------
# Stage 2: Build & Publish Application
#-------------------------------------------------------------------------------------
WORKDIR /src
COPY . .

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

COPY --from=build-env /app/publish .

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