// File: WebAPI/Program.cs

#region Usings
// Using های استاندارد .NET و NuGet Packages
// Using های مربوط به پروژه‌های شما
using Application;                          // برای متد توسعه‌دهنده AddApplicationServices
using Application.Features.Forwarding.Extensions;
using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services;
using Application.Interfaces; // برای IRssFetchingCoordinatorService (جهت زمان‌بندی Job در Hangfire)
// using Application.Interfaces;          // معمولاً اینترفیس‌های Application مستقیماً اینجا نیاز نیستند مگر برای موارد خاص
// using Application.Services;            // و نه پیاده‌سازی‌های آن
using BackgroundTasks;                    // برای متد توسعه‌دهنده AddBackgroundTasksServices (اگر تعریف کرده‌اید)
using Hangfire;                             // برای پیکربندی‌های Hangfire مانند CompatibilityLevel, RecurringJob, Cron
using Hangfire.Dashboard;                   // برای DashboardOptions, IDashboardAuthorizationFilter
using Hangfire.SqlServer;
// using Hangfire.SqlServer;              // اگر از SQL Server برای Hangfire استفاده می‌کنید
using Infrastructure;                     // برای متد توسعه‌دهنده AddInfrastructureServices
// using WebAPI.Filters; //  Namespace برای HangfireNoAuthFilter (اگر در این مسیر است و استفاده می‌کنید)
using Infrastructure.Features.Forwarding.Extensions;
using Infrastructure.Services;
using Microsoft.OpenApi.Models;             // برای OpenApiInfo
using Serilog;                              // برای Log, LoggerConfiguration, UseSerilog
using Shared.Helpers;
using Shared.Maintenance;
using Shared.Settings;                    // برای CryptoPaySettings (از پروژه Shared)
using TelegramPanel.Application.CommandHandlers.Admin;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Extensions;
using TelegramPanel.Infrastructure;
using TL;
using WebAPI.Extensions;
#endregion

// ------------------- پیکربندی اولیه لاگر Serilog (Bootstrap Logger) -------------------
// این لاگر قبل از خواندن کامل appsettings.json و ساخت هاست استفاده می‌شود
// تا خطاهای بسیار اولیه در راه‌اندازی برنامه نیز لاگ شوند.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug() //  حداقل سطح لاگ برای Bootstrap (می‌توانید تغییر دهید)
    .Enrich.FromLogContext()
    .WriteTo.Console() //  لاگ کردن در کنسول
    .CreateBootstrapLogger();

try
{
    Log.Information("--------------------------------------------------");
    Log.Information("Application Starting Up (Program.cs)...");
    Log.Information("--------------------------------------------------");

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseKestrel();

    #region Configure Serilog Logging
    // ------------------- ۱. پیکربندی Serilog با تنظیمات از appsettings.json -------------------
    // این بخش Serilog را به عنوان سیستم لاگینگ اصلی برنامه تنظیم می‌کند.
    builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
       .ReadFrom.Configuration(context.Configuration)
       .ReadFrom.Services(services)
       .Enrich.FromLogContext()
       .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")

       // ✅✅✅✅✅ THE FIX FOR FILE LOGGING IS HERE ✅✅✅✅✅
       // این بخش لاگ‌ها را در فایلی در مسیر 'C:\Apps\ForexTradingBot\logs' ذخیره می‌کند
       // هر روز یک فایل جدید ساخته می‌شود و فایل‌های قدیمی‌تر از ۷ روز به طور خودکار پاک می‌شوند.
       .WriteTo.File(
           path: Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt"), // مسیر و الگوی نام فایل
           rollingInterval: RollingInterval.Day, // ایجاد یک فایل جدید به صورت روزانه
           rollOnFileSizeLimit: true, // اگر حجم فایل زیاد شد، یک فایل جدید بساز
           fileSizeLimitBytes: 10 * 1024 * 1024, // محدودیت حجم فایل: ۱۰ مگابایت
           retainedFileCountLimit: 1, // حداکثر ۷ فایل لاگ (۷ روز) را نگه دار
           outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}")
   );
    #endregion

    #region Add Core ASP.NET Core Services
    // ------------------- ۲. اضافه کردن سرویس‌های پایه ASP.NET Core -------------------
    // فعال کردن پشتیبانی از کنترلرهای API
    builder.Services.AddControllers();
    // فعال کردن API Explorer برای تولید مستندات Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();

    // Add CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(builder =>
        {
            builder.WithOrigins("http://localhost:3000", "http://localhost:4200")
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .AllowCredentials();
        });
    });
    var environment = builder.Environment;
    // پیکربندی Swagger/OpenAPI برای مستندسازی API
    builder.Services.AddSwaggerGen(options =>
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
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
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
            var first = apiDescriptions.First();
            return first;
        });
    });
    Log.Information("Core ASP.NET Core services (Controllers, API Explorer, Swagger) added.");
    #endregion

    #region Configure Application Options/Settings
    // ------------------- ۳. پیکربندی Options (خواندن تنظیمات از appsettings.json) -------------------
    // مپ کردن بخش "TelegramSettings" از appsettings.json به کلاس Domain.Settings.TelegramSettings
    // این کلاس می‌تواند شامل تنظیمات عمومی تلگرام مانند AdminUserId باشد.
    builder.Services.Configure<Domain.Settings.TelegramSettings>(builder.Configuration.GetSection("TelegramSettings"));
    // Configure TelegramUserApiSettings
    builder.Services.Configure<Infrastructure.Settings.TelegramUserApiSettings>(builder.Configuration.GetSection("TelegramUserApi"));
    builder.Services.AddSingleton<Application.Common.Interfaces.ITelegramUserApiClient, Infrastructure.Services.TelegramUserApiClient>();
    builder.Services.AddHostedService<Infrastructure.Services.TelegramUserApiInitializationService>();
    // مپ کردن بخش CryptoPaySettings.SectionName (که "CryptoPay" است) از appsettings.json به کلاس Shared.Settings.CryptoPaySettings
    builder.Services.Configure<CryptoPaySettings>(builder.Configuration.GetSection(CryptoPaySettings.SectionName));
    builder.Services.AddMemoryCache();
    // TelegramPanelSettings در متد AddTelegramPanelServices پیکربندی می‌شود.
    Log.Information("Application settings (Options pattern) configured.");
    #endregion

    #region Register Custom Application Layers and Services
    // ------------------- ۴. رجیستر کردن سرویس‌های لایه‌های مختلف برنامه -------------------
    // این متدها باید در فایل‌های DependencyInjection.cs (یا ServiceCollectionExtensions.cs) هر لایه تعریف شده باشند.
    // ترتیب فراخوانی: ابتدا لایه‌های پایه (Application, Infrastructure)، سپس لایه‌های Presentation یا خاص (TelegramPanel, BackgroundTasks).

    builder.Services.AddApplicationServices();
    Log.Information("Application services registered.");

    builder.Services.AddInfrastructureServices(builder.Configuration);
    Log.Information("Infrastructure services registered.");

    builder.Services.AddForwardingInfrastructure();
    Log.Information("Forwarding infrastructure services registered.");

    builder.Services.AddTelegramPanelServices(builder.Configuration);
    Log.Information("Telegram panel services registered.");

    builder.Services.AddBackgroundTasksServices();
    Log.Information("Background tasks services registered.");

    builder.Services.AddForwardingServices();
    Log.Information("Forwarding services registered.");

    builder.Services.AddForwardingOrchestratorServices();
    Log.Information("Forwarding orchestrator services registered.");

    builder.Services.AddWindowsService();
    try
    {
        SqlServiceManager.EnsureSqlServicesRunning();
    }
    catch (Exception exSql)
    {
        Log.Error(exSql, "Error during SqlServiceManager.EnsureSqlServicesRunning()");
        // Optionally rethrow if it's critical, or decide if the app can continue
    }

    #endregion





    #region Configure Hangfire

    // ------------------- ۵. پیکربندی Hangfire برای اجرای کارهای پس‌زمینه -------------------


    
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection"), new SqlServerStorageOptions // ✅ CHANGED: Added SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.FromSeconds(15), // How often to check for new jobs (adjust as needed)
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true, // Set to true if using Azure SQL Database or similar cloud environments
            SchemaName = "HangFire" // Optional: Specify a custom schema name if you don't want the default "HangFire"
        }));



    builder.Services.AddHangfireServer();
    Log.Information("Hangfire services (with SQL Server for production) added.");

    builder.Services.AddHangfireCleaner();

    Log.Information("Hangfire cleaner service added.");

    Log.Information("Performing final manual service registrations...");

    // FIX FOR: Unable to resolve 'IBotCommandSetupService'
    builder.Services.AddTransient<IBotCommandSetupService, BotCommandSetupService>();

    Log.Information("Final manual service registrations complete.");
    builder.Services.Configure<List<Infrastructure.Settings.ForwardingRule>>( // <<< Fully qualified
    builder.Configuration.GetSection("ForwardingRules"));


    #endregion

    // ------------------- ساخت WebApplication instance -------------------
    var app = builder.Build(); //  ساخت برنامه با تمام سرویس‌های پیکربندی شده
    Log.Information("Application host built. Performing mandatory startup tasks...");

    // The application will now start INSTANTLY.
    #region Queue Startup Maintenance Jobs to Hangfire

    // We register a callback that runs ONCE, right after the application has fully started.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Log.Information("Application has fully started. Now enqueuing background maintenance jobs.");
        try
        {
            // We get the necessary services from the application's root service provider.
            var backgroundJobClient = app.Services.GetRequiredService<IBackgroundJobClient>();
            var configuration = app.Services.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                Log.Error("Cannot enqueue maintenance jobs: DefaultConnection string is missing.");
                return;
            }

            Log.Information("Enqueuing Hangfire core cleanup job to run in the background...");
            // This job will run once, as soon as a Hangfire server is available.
            backgroundJobClient.Enqueue<IHangfireCleaner>(cleaner => cleaner.PurgeCompletedAndFailedJobs(connectionString));

            Log.Information("Enqueuing duplicate NewsItem cleanup job to run in the background...");
            backgroundJobClient.Enqueue<IHangfireCleaner>(cleaner => cleaner.PurgeDuplicateNewsItems(connectionString));

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
    var programLogger = app.Services.GetRequiredService<ILogger<Program>>(); //  استفاده از ILogger<Program> برای لاگ‌های مختص Program.cs

    #region Configure HTTP Request Pipeline
    // ------------------- ۶. پیکربندی پایپ‌لاین پردازش درخواست‌های HTTP -------------------

    //  فعال کردن Request Body Buffering. این برای خواندن بدنه درخواست چندین بار (مثلاً در Middleware ها یا کنترلرها) لازم است.
    //  مخصوصاً برای CryptoPayWebhookController جهت اعتبارسنجی امضا.
    app.Use(async (context, next) =>
    {
        context.Request.EnableBuffering();
        await next.Invoke();
    });

    // Enable Swagger in all environments
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Forex Signal Bot API V1");
        c.RoutePrefix = string.Empty; // This will make Swagger UI the root page
        c.DefaultModelsExpandDepth(-1); // Hide models section by default
    });

    //  پیکربندی‌های مختص محیط توسعه
    if (app.Environment.IsDevelopment())
    {

        programLogger.LogInformation("Development environment detected. Enabling Developer Exception Page.");
        app.UseDeveloperExceptionPage(); //  نمایش صفحه خطای با جزئیات برای توسعه‌دهندگان
    }
    else //  پیکربندی‌های مختص محیط Production
    {
        programLogger.LogInformation("Production environment detected. Enabling HSTS.");
        // app.UseExceptionHandler("/Error"); //  می‌توانید یک صفحه خطای سفارشی برای کاربران نهایی تعریف کنید
        app.UseHsts(); //  افزودن هدر HTTP Strict Transport Security برای امنیت بیشتر (اجبار استفاده از HTTPS)
    }

    app.UseHttpsRedirection(); //  ریدایرکت خودکار تمام درخواست‌های HTTP به HTTPS

    app.UseSerilogRequestLogging(); //  لاگ کردن تمام درخواست‌های HTTP ورودی با جزئیات (توسط Serilog)

    app.UseRouting();
    app.UseAuthorization();
    app.MapHangfireDashboard();
    programLogger.LogInformation("HTTP request pipeline configured.");
    #endregion

    #region Configure Hangfire Dashboard & Recurring Jobs
    // ------------------- ۷. اضافه کردن داشبورد Hangfire -------------------
    var hangfireDashboardOptions = new DashboardOptions
    {
        DashboardTitle = "Forex Trading Bot - Background Jobs Monitor",
        IgnoreAntiforgeryToken = true,
        // ✅ CHANGED: Applying a basic authorization filter for development.
        // For production, you MUST implement proper authentication/authorization here.
        Authorization = new[] { new LocalRequestsOnlyAuthorizationFilter() }
    };

    app.UseHangfireDashboard("/hangfire", hangfireDashboardOptions);
    programLogger.LogInformation("Hangfire Dashboard configured at /hangfire (For development, open to local requests. Secure for production!).");

    // ------------------- ۸. زمان‌بندی Job های تکرارشونده Hangfire -------------------
    //  این Job ها پس از شروع کامل برنامه، توسط سرور Hangfire به طور خودکار اجرا خواهند شد.
    var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    appLifetime.ApplicationStarted.Register(() => //  اجرا پس از اینکه برنامه به طور کامل شروع به کار کرد
    {
        programLogger.LogInformation("Application fully started. Scheduling/Updating Hangfire recurring jobs...");
        try
        {
            // زمان‌بندی اجرای متد FetchAllActiveFeedsAsync از سرویس IRssFetchingCoordinatorService
            RecurringJob.AddOrUpdate<IRssFetchingCoordinatorService>(
                recurringJobId: "fetch-all-active-rss-feeds", //  یک شناسه منحصر به فرد و خوانا برای این Job
                methodCall: service => service.FetchAllActiveFeedsAsync(CancellationToken.None), // متدی که باید اجرا شود
                cronExpression: Cron.MinuteInterval(30),    // ✅ برای تست: هر ۱ دقیقه اجرا شود. برای Production بیشتر کنید (مثلاً "*/15 * * * *")
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc } //  اجرای Job بر اساس زمان UTC
            );
            programLogger.LogInformation("Recurring job 'fetch-all-active-rss-feeds' scheduled to run every 1 minute (for testing).");
        }
        catch (Exception ex)
        {
            programLogger.LogCritical(ex, "CRITICAL: Failed to schedule/update Hangfire recurring job 'fetch-all-active-rss-feeds'. RSS fetching will NOT occur automatically!");
        }
    });
    programLogger.LogInformation("Hangfire recurring jobs registration initiated (will run after application starts).");
    #endregion

    #region Map Controllers & Run Application
    // ------------------- مپ کردن کنترلرها و اجرای برنامه -------------------
    app.MapControllers(); //  مسیردهی درخواست‌ها به Action های کنترلرها
    programLogger.LogInformation("Application setup complete. Starting web host now...");

    // Get the application URL from configuration or use default
    var urls = builder.Configuration["Urls"] ?? "https://localhost:5001;http://localhost:5000";
    var firstUrl = urls.Split(';')[0].Trim();

    using (var scope = app.Services.CreateScope())
    {
        var orchestrator = scope.ServiceProvider.GetRequiredService<UserApiForwardingOrchestrator>();
        // Use orchestrator if needed
    }

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