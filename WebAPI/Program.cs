// File: WebAPI/Program.cs

#region Usings
// Using های استاندارد .NET و NuGet Packages
// Using های مربوط به پروژه‌های شما
using Application;                          // برای متد توسعه‌دهنده AddApplicationServices
using Application.Common.Interfaces;
using Application.Features.Forwarding.Extensions;
// using Application.Interfaces;          // معمولاً اینترفیس‌های Application مستقیماً اینجا نیاز نیستند مگر برای موارد خاص
// using Application.Services;            // و نه پیاده‌سازی‌های آن
using BackgroundTasks;                    // برای متد توسعه‌دهنده AddBackgroundTasksServices (اگر تعریف کرده‌اید)
using BackgroundTasks.Services;
using Hangfire;                             // برای پیکربندی‌های Hangfire مانند CompatibilityLevel, RecurringJob, Cron
using Hangfire.Dashboard;                   // برای DashboardOptions, IDashboardAuthorizationFilter
using Hangfire.MemoryStorage;
using Hangfire.PostgreSql;
using Hangfire.SqlServer;
using Infrastructure.Data;

// using Hangfire.SqlServer;              // اگر از SQL Server برای Hangfire استفاده می‌کنید
// using WebAPI.Filters; //  Namespace برای HangfireNoAuthFilter (اگر در این مسیر است و استفاده می‌کنید)
using Infrastructure.Features.Forwarding.Extensions;
using Infrastructure.Logging;
using Infrastructure.Services;
using Microsoft.OpenApi.Models;             // برای OpenApiInfo
using Serilog;                              // برای Log, LoggerConfiguration, UseSerilog
using Serilog.Enrichers.WithCaller;
using Shared.Helpers;
using Shared.Maintenance;
using Shared.Settings;                    // برای CryptoPaySettings (از پروژه Shared)
using TelegramPanel.Extensions;
using TelegramPanel.Infrastructure.Services;
using WebAPI.Extensions;

#endregion

// ------------------- پیکربندی اولیه لاگر Serilog (Bootstrap Logger) -------------------
// این لاگر قبل از خواندن کامل appsettings.json و ساخت هاست استفاده می‌شود
// تا خطاهای بسیار اولیه در راه‌اندازی برنامه نیز لاگ شوند.
Log.Logger = new LoggerConfiguration()
    // Read base configuration from appsettings.json
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .Build())
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "WebAPI") // Set a default name
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithCaller()
    .WriteTo.Console() // Always write to console during bootstrap
    .CreateBootstrapLogger(); // Use CreateBootstrapLogger() for this initial phase

// Add a global handler for unhandled exceptions. This is our safety net.
AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
{
Log.Fatal((Exception)eventArgs.ExceptionObject, "FATAL UNHANDLED EXCEPTION");
Log.CloseAndFlush(); // Ensure the fatal log is sent before the app dies
};

try
{

    Log.Information("--------------------------------------------------");
    Log.Information("Application Starting Up (Program.cs)...");
    Log.Information("--------------------------------------------------");
    int minThreads = 500; // Adjust as needed based on monitoring
    _ = ThreadPool.SetMinThreads(minThreads, minThreads);
    Log.Information("ThreadPool minimum threads set to {MinThreads}.", minThreads);
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    _ = builder.WebHost.UseKestrel();
    builder.Services.AddSingleton<Infrastructure.Logging.TelegramAdminSink>();
    // This is more reliable than GetValue<bool> for environment variables.
    string smokeTestFlag = builder.Configuration["IsSmokeTest"] ?? "false";
    bool isSmokeTest = "true".Equals(smokeTestFlag, StringComparison.OrdinalIgnoreCase);
    #region Configure Serilog Logging
    // ------------------- ۱. پیکربندی Serilog با تنظیمات از appsettings.json -------------------
    // این بخش Serilog را به عنوان سیستم لاگینگ اصلی برنامه تنظیم می‌کند.
    _ = builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)

        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", context.HostingEnvironment.ApplicationName)
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName()

        // --- THIS IS THE FINAL, DEFINITIVE CONFIGURATION ---
        .Enrich.With(new CustomCallerEnricher(
            "Serilog",
            "Microsoft",
            "System",
            "Infrastructure.Logging" // <-- The critical addition
        ))
        // ----------------------------------------------------

        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}")

        .WriteTo.Sink(
            new Infrastructure.Logging.TelegramAdminSink(context.Configuration),
            Serilog.Events.LogEventLevel.Error
        )
    );

    builder.Services.AddAutoMapper(typeof(Program));
    builder.Services.AddSingleton<Application.Common.Interfaces.ILoggingSanitizer, Infrastructure.Security.PiiLoggingSanitizer>();
    #endregion

    #region Add Core ASP.NET Core Services
    _ = builder.Services.AddHostedService<IdleNewsMonitorService>();
    _ = builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "ForexTradingBotAPI";
    });

    // ------------------- ۲. اضافه کردن سرویس‌های پایه ASP.NET Core -------------------
    // فعال کردن پشتیبانی از کنترلرهای API
    _ = builder.Services.AddControllers();
    // فعال کردن API Explorer برای تولید مستندات Swagger/OpenAPI
    _ = builder.Services.AddEndpointsApiExplorer();

    // Add CORS
    _ = builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(builder =>
        {
            _ = builder.WithOrigins("http://localhost:3000", "http://localhost:4200")
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .AllowCredentials();
        });
    });
    IWebHostEnvironment environment = builder.Environment;
    // پیکربندی Swagger/OpenAPI برای مستندسازی API
    _ = builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Version = "v1",
            Title = "Forex Signal Bot API",
            Description = "API endpoints for the Forex Signal Bot application, including Telegram webhook and administrative functions.",
            Contact = new OpenApiContact
            {
                Name = "Support",
                Email = "support@example.com"
            }
        });

        // Add XML documentation
        string xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }

        // Add JWT Authentication
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer"
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });

        // Configure Swagger to handle conflicting routes
        options.CustomSchemaIds(type => type.FullName);
        options.ResolveConflictingActions(apiDescriptions =>
        {
            Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription first = apiDescriptions.First();
            return first;
        });
    });
    Log.Information("Core ASP.NET Core services (Controllers, API Explorer, Swagger) added.");
    #endregion

    #region Configure Application Options/Settings
    // ------------------- ۳. پیکربندی Options (خواندن تنظیمات از appsettings.json) -------------------
    // مپ کردن بخش "TelegramSettings" از appsettings.json به کلاس Domain.Settings.TelegramSettings
    // این کلاس می‌تواند شامل تنظیمات عمومی تلگرام مانند AdminUserId باشد.
    _ = builder.Services.Configure<Domain.Settings.TelegramSettings>(builder.Configuration.GetSection("TelegramSettings"));
    // Configure TelegramUserApiSettings
    // مپ کردن بخش CryptoPaySettings.SectionName (که "CryptoPay" است) از appsettings.json به کلاس Shared.Settings.CryptoPaySettings
    _ = builder.Services.Configure<CryptoPaySettings>(builder.Configuration.GetSection(CryptoPaySettings.SectionName));


    _ = builder.Services.AddMemoryCache();


    // TelegramPanelSettings در متد AddTelegramPanelServices پیکربندی می‌شود.
    Log.Information("Application settings (Options pattern) configured.");


    #endregion

    #region Register Custom Application Layers and Services
    // ------------------- ۴. رجیستر کردن سرویس‌های لایه‌های مختلف برنامه -------------------
    // این متدها باید در فایل‌های DependencyInjection.cs (یا ServiceCollectionExtensions.cs) هر لایه تعریف شده باشند.
    // ترتیب فراخوانی: ابتدا لایه‌های پایه (Application, Infrastructure)، سپس لایه‌های Presentation یا خاص (TelegramPanel, BackgroundTasks).




    _ = builder.Services.AddApplicationServices();
    Log.Information("Application services registered.");

    _ = builder.Services.AddInfrastructureServices(builder.Configuration, isSmokeTest);
    Log.Information("Infrastructure services registered.");


    _ = builder.Services.AddTelegramPanelServices(builder.Configuration);
    Log.Information("Telegram panel services registered.");

    _ = builder.Services.AddBackgroundTasksServices();
    Log.Information("Background tasks services registered.");
    _ = builder.Services.AddHealthChecks();



    // --- Universal Conditional Feature: Auto-Forwarding ---
    // We check for the actual secrets required for this feature to run.
    string? apiId = builder.Configuration["TelegramUserApi:ApiId"];
    string? apiHash = builder.Configuration["TelegramUserApi:ApiHash"];
    try
    {
        if (!string.IsNullOrEmpty(apiId) && !string.IsNullOrEmpty(apiHash))
        {
            Log.Information("✅ Auto-Forwarding feature ENABLED (ApiId and ApiHash were found). Registering all related services...");

            // 1. Configure the settings object
            _ = builder.Services.Configure<Infrastructure.Settings.TelegramUserApiSettings>(builder.Configuration.GetSection("TelegramUserApi"));

            // 2. Register the API client itself
            _ = builder.Services.AddSingleton<ITelegramUserApiClient, TelegramUserApiClient>();

            // 3. Register the background service that initializes the client
            _ = builder.Services.AddHostedService<TelegramUserApiInitializationService>();

            // 4. Register all the other forwarding services from the other projects
            _ = builder.Services.AddForwardingInfrastructure();
            _ = builder.Services.AddForwardingServices();
            _ = builder.Services.AddForwardingOrchestratorServices();

            Log.Information("All Auto-Forwarding services have been successfully registered.");
        }
        else
        {
            // If the secrets are missing, we skip ALL related services.
            Log.Information("ℹ️ Auto-Forwarding feature DISABLED (ApiId or ApiHash not found in configuration).");
        }


        // --- Universal Conditional Feature: CryptoPay (Example) ---
        // The same robust pattern is applied here.
        string? cryptoPayToken = builder.Configuration["CryptoPay:ApiToken"];
        string? cryptoPayApiKey = builder.Configuration["CryptoPay:ApiKey"]; // Assuming you have this key too

        if (!string.IsNullOrEmpty(cryptoPayToken) && !string.IsNullOrEmpty(cryptoPayApiKey))
        {
            // Here you would register your CryptoPay specific services.
            // builder.Services.AddCryptoPayServices(builder.Configuration);
            Log.Information("✅ CryptoPay feature ENABLED (ApiToken and ApiKey were found in configuration).");
        }
        else
        {
            Log.Information("ℹ️ CryptoPay feature DISABLED (CryptoPay secrets not found in configuration).");
        }

    }
    catch
    {
        // If the secrets are missing, we skip ALL related services.
        Log.Information("ℹ️ Auto-Forwarding feature DISABLED (ApiId or ApiHash not found in configuration).");
    }






    _ = builder.Services.AddWindowsService();
    bool isRunningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
    if (OperatingSystem.IsWindows() && !isRunningInContainer)
    {
        try
        {
            Log.Information("Running on Windows (not in a container). Checking for local SQL Server services...");
            SqlServiceManager.EnsureSqlServicesRunning();
            Log.Information("SQL Server service check complete.");
        }
        catch (Exception exSql)
        {
            // This is not a fatal error, so we just log it and continue.
            // The app might be connecting to a remote or non-service SQL instance.
            Log.Warning(exSql, "Could not ensure local SQL Server services are running. This may be expected.");
        }
    }
    else
    {
        // Log why we are skipping the check. This is useful for debugging.
        if (isRunningInContainer)
        {
            Log.Information("Skipping SQL Server service check: Application is running inside a Docker container.");
        }
        else if (!OperatingSystem.IsWindows())
        {
            Log.Information("Skipping SQL Server service check: Application is not running on Windows.");
        }
    }





    #endregion

    #region Configure Hangfire

    // =========================================================================
    // ✅✅ CORRECTED HANGFIRE CONFIGURATION (PROVIDER-AWARE) ✅✅
    // =========================================================================

    if (isSmokeTest)
    {
        // --- SMOKE TEST CONFIGURATION ---
        Log.Information("✅ Smoke Test: Configuring Hangfire with In-Memory storage.");

        _ = builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseMemoryStorage());
    }
    else
    {
        // --- PRODUCTION / REAL DEVELOPMENT CONFIGURATION ---
        // Read the database provider from configuration.
        string? dbProvider = builder.Configuration.GetValue<string>("DatabaseSettings:DatabaseProvider")?.ToLowerInvariant();

        Log.Information("Configuring Hangfire for production/development with '{DbProvider}' provider.", dbProvider);

        _ = dbProvider switch
        {
            "sqlserver" => builder.Services.AddHangfire(config => config
                                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                                .UseSimpleAssemblyNameTypeSerializer()
                                .UseRecommendedSerializerSettings()
                                .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection"), new SqlServerStorageOptions
                                {
                                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                                    QueuePollInterval = TimeSpan.FromSeconds(15),
                                    UseRecommendedIsolationLevel = true,
                                    DisableGlobalLocks = true,
                                    SchemaName = "HangFire"
                                })),// Use the robust SQL Server storage provider.
            "postgres" or "postgresql" => builder.Services.AddHangfire(config => config
                                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                                .UseSimpleAssemblyNameTypeSerializer()
                                .UseRecommendedSerializerSettings()
                                .UsePostgreSqlStorage(options =>
                                    options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("PostgresConnection"))
                                )),// Use the PostgreSQL storage provider.
            _ => throw new NotSupportedException($"Hangfire configuration failed: Unsupported DatabaseProvider '{dbProvider}'."),
        };
    }

    _ = builder.Services.AddHangfireCleaner();
    _ = builder.Services.AddHangfireServer();


    Log.Information("Hangfire cleaner service added.");

    Log.Information("Performing final manual service registrations...");

    // FIX FOR: Unable to resolve 'IBotCommandSetupService'
    _ = builder.Services.AddTransient<IBotCommandSetupService, BotCommandSetupService>();

    Log.Information("Final manual service registrations complete.");
    _ = builder.Services.Configure<List<Infrastructure.Settings.ForwardingRule>>( // <<< Fully qualified
    builder.Configuration.GetSection("ForwardingRules"));


    #endregion

    // ------------------- ساخت WebApplication instance -------------------
    WebApplication app = builder.Build(); //  ساخت برنامه با تمام سرویس‌های پیکربندی شده
    Log.Information("Application host built. Performing mandatory startup tasks...");

    // The application will now start INSTANTLY.
    #region Queue Startup Maintenance Jobs to Hangfire

    // We register a callback that runs ONCE, right after the application has fully started.
    _ = app.Lifetime.ApplicationStarted.Register(() =>
    {
        Log.Information("Application has fully started. Now enqueuing background maintenance jobs.");
        try
        {
            // We get the necessary services from the application's root service provider.
            IBackgroundJobClient backgroundJobClient = app.Services.GetRequiredService<IBackgroundJobClient>();
            IConfiguration configuration = app.Services.GetRequiredService<IConfiguration>();
            string? connectionString = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                Log.Error("Cannot enqueue maintenance jobs: DefaultConnection string is missing.");
                return;
            }

            Log.Information("Enqueuing Hangfire core cleanup job to run in the background...");
            // This job will run once, as soon as a Hangfire server is available.
            _ = backgroundJobClient.Enqueue<IHangfireCleaner>(cleaner => cleaner.PurgeCompletedAndFailedJobs(connectionString));

            Log.Information("Enqueuing duplicate NewsItem cleanup job to run in the background...");
            _ = backgroundJobClient.Enqueue<IHangfireCleaner>(cleaner => cleaner.PurgeDuplicateNewsItems(connectionString));

            Log.Information("✅ All startup maintenance jobs have been successfully enqueued. They will run asynchronously.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while trying to enqueue startup maintenance jobs.");
        }
    });

    #endregion

    Log.Information("Mandatory startup tasks completed.");


    // ------------------- دریافت لاگر از DI برای استفاده در ادامه Program.cs -------------------
    //  این لاگر، لاگری است که توسط UseSerilog پیکربندی شده است.
    ILogger<Program> programLogger = app.Services.GetRequiredService<ILogger<Program>>(); //  استفاده از ILogger<Program> برای لاگ‌های مختص Program.cs

    #region Configure HTTP Request Pipeline
    // ------------------- ۶. پیکربندی پایپ‌لاین پردازش درخواست‌های HTTP -------------------

    //  فعال کردن Request Body Buffering. این برای خواندن بدنه درخواست چندین بار (مثلاً در Middleware ها یا کنترلرها) لازم است.
    //  مخصوصاً برای CryptoPayWebhookController جهت اعتبارسنجی امضا.
    _ = app.Use(async (context, next) =>
    {
        context.Request.EnableBuffering();
        await next.Invoke();
    });

    // Enable Swagger in all environments
    _ = app.UseSwagger();
    _ = app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Forex Signal Bot API V1");
        c.RoutePrefix = string.Empty; // This will make Swagger UI the root page
        c.DefaultModelsExpandDepth(-1); // Hide models section by default
    });

    //  پیکربندی‌های مختص محیط توسعه
    if (app.Environment.IsDevelopment())
    {

        programLogger.LogInformation("Development environment detected. Enabling Developer Exception Page.");
        _ = app.UseDeveloperExceptionPage(); //  نمایش صفحه خطای با جزئیات برای توسعه‌دهندگان
    }
    else //  پیکربندی‌های مختص محیط Production
    {
        programLogger.LogInformation("Production environment detected. Enabling HSTS.");
        // app.UseExceptionHandler("/Error"); //  می‌توانید یک صفحه خطای سفارشی برای کاربران نهایی تعریف کنید
        _ = app.UseHsts(); //  افزودن هدر HTTP Strict Transport Security برای امنیت بیشتر (اجبار استفاده از HTTPS)
    }

    _ = app.UseHttpsRedirection(); //  ریدایرکت خودکار تمام درخواست‌های HTTP به HTTPS

    _ = app.UseSerilogRequestLogging(); //  لاگ کردن تمام درخواست‌های HTTP ورودی با جزئیات (توسط Serilog)

    _ = app.UseRouting();
    _ = app.UseAuthorization();
    _ = app.MapHangfireDashboard();
    programLogger.LogInformation("HTTP request pipeline configured.");
    #endregion

    #region Configure Hangfire Dashboard & Recurring Jobs
    // ------------------- ۷. اضافه کردن داشبورد Hangfire -------------------
    DashboardOptions hangfireDashboardOptions = new()
    {
        DashboardTitle = "Forex Trading Bot - Background Jobs Monitor",
        IgnoreAntiforgeryToken = true,
        // ✅ CHANGED: Applying a basic authorization filter for development.
        // For production, you MUST implement proper authentication/authorization here.
        Authorization = new[] { new LocalRequestsOnlyAuthorizationFilter() }
    };

    _ = app.UseHangfireDashboard("/hangfire", hangfireDashboardOptions);
    programLogger.LogInformation("Hangfire Dashboard configured at /hangfire (For development, open to local requests. Secure for production!).");

    // ------------------- ۸. زمان‌بندی Job های تکرارشونده Hangfire -------------------
    //  این Job ها پس از شروع کامل برنامه، توسط سرور Hangfire به طور خودکار اجرا خواهند شد.
    IHostApplicationLifetime appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    _ = appLifetime.ApplicationStarted.Register(() => // اجرا پس از اینکه برنامه کامل شروع شد
    {
        // آیا این لاگ را در کنسول می‌بینید؟
        programLogger.LogInformation("Application fully started. Scheduling/Updating Hangfire recurring jobs...");
        try
        {
            IRecurringJobManager recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();

            // از این روش استفاده کنیم که وابستگی‌ها را بهتر مدیریت می‌کند
            recurringJobManager.AddOrUpdate<IRssFetchingCoordinatorService>(
                recurringJobId: "fetch-all-active-rss-feeds",
                methodCall: service => service.FetchAllActiveFeedsAsync(CancellationToken.None),
                cronExpression: "*/15 * * * *",
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
            );

            // آیا این لاگ موفقیت را در کنسول می‌بینید؟
            programLogger.LogInformation(">>> Recurring job 'fetch-all-active-rss-feeds' was successfully scheduled. <<<");
        }
        catch (Exception ex)
        {
            // اگر خطایی رخ دهد، آیا این لاگ را می‌بینید؟
            programLogger.LogCritical(ex, ">>> CRITICAL: FAILED to schedule Hangfire recurring job. <<<");
        }

    });
    programLogger.LogInformation("Hangfire recurring jobs registration initiated (will run after application starts).");
    #endregion

    #region Map Controllers & Run Application
    _ = app.MapHealthChecks("/healthz");
    // ------------------- مپ کردن کنترلرها و اجرای برنامه -------------------
    _ = app.MapControllers(); //  مسیردهی درخواست‌ها به Action های کنترلرها
    programLogger.LogInformation("Application setup complete. Starting web host now...");

    // Get the application URL from configuration or use default
    string urls = builder.Configuration["Urls"] ?? "https://localhost:5001;http://localhost:5000";
    string firstUrl = urls.Split(';')[0].Trim();

    using (IServiceScope scope = app.Services.CreateScope())
    {
        UserApiForwardingOrchestrator orchestrator = scope.ServiceProvider.GetRequiredService<UserApiForwardingOrchestrator>();
        // Use orchestrator if needed
    }

    // --- File: WebAPI/Program.cs ---

    // ... after app.UseSerilogRequestLogging() and other middleware ...

    app.MapGet("/testerror", () => {
        // This will force an exception and a high-level log event
        try
        {
            throw new InvalidOperationException("This is a guaranteed test exception for the Telegram sink.");
        }
        catch (Exception ex)
        {
            // Use the ILogger from the dependency injection container
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Caught a test exception.");
            return Results.Problem("A test error was logged. Check your Telegram and console.");
        }
    });
    app.UseSerilogRequestLogging();
    // In the middleware pipeline section (before app.Run())
    _ = app.UseStaticFiles();
    app.Run(); //  شروع به گوش دادن به درخواست‌های HTTP و اجرای برنامه
    #endregion
}
catch (Exception ex)
{
    //  لاگ کردن خطاهای بسیار بحرانی که مانع از اجرای برنامه شده‌اند.
    Log.Fatal(ex, "Application host very much terminated unexpectedly.");
    // Environment.ExitCode = 1; //  برای نشان دادن خروج ناموفق به سیستم عامل یا اسکریپت‌های دیگر
}
finally
{

    Log.Information("--------------------------------------------------");
    Log.Information("Application Shutting Down...");
    Log.CloseAndFlush();
    Log.Information("--------------------------------------------------");
}