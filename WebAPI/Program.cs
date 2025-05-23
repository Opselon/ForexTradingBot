﻿// File: WebAPI/Program.cs

#region Usings
// Using های استاندارد .NET و NuGet Packages
// Using های مربوط به پروژه‌های شما
using Application;                          // برای متد توسعه‌دهنده AddApplicationServices
using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services;
using Application.Features.Forwarding.Extensions;
using Application.Interfaces; // برای IRssFetchingCoordinatorService (جهت زمان‌بندی Job در Hangfire)
// using Application.Interfaces;          // معمولاً اینترفیس‌های Application مستقیماً اینجا نیاز نیستند مگر برای موارد خاص
// using Application.Services;            // و نه پیاده‌سازی‌های آن
using BackgroundTasks;                    // برای متد توسعه‌دهنده AddBackgroundTasksServices (اگر تعریف کرده‌اید)
using Core.Logging;                         // Add this line for TelegramLogger
using Hangfire;                             // برای پیکربندی‌های Hangfire مانند CompatibilityLevel, RecurringJob, Cron
using Hangfire.Dashboard;                   // برای DashboardOptions, IDashboardAuthorizationFilter
using Hangfire.PostgreSql;
// using Hangfire.SqlServer;              // اگر از SQL Server برای Hangfire استفاده می‌کنید
using Infrastructure;                     // برای متد توسعه‌دهنده AddInfrastructureServices
using Infrastructure.Services;
using Infrastructure.Settings;
using Microsoft.OpenApi.Models;             // برای OpenApiInfo
using Serilog;                              // برای Log, LoggerConfiguration, UseSerilog
using Shared.Helpers;
using Shared.Settings;                    // برای CryptoPaySettings (از پروژه Shared)
using TelegramPanel.Extensions;
using TelegramPanel.Infrastructure;
// using WebAPI.Filters; //  Namespace برای HangfireNoAuthFilter (اگر در این مسیر است و استفاده می‌کنید)
using Infrastructure.Features.Forwarding.Extensions;
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

    #region Configure Serilog Logging
    // Configure Serilog with settings from appsettings.json
    builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName()
        .Enrich.WithProcessId()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "ForexTradingBot")
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information
        )
        .WriteTo.File(
            path: "logs/forexbot-.log",
            rollingInterval: RollingInterval.Hour,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information
        )
    );
    Log.Information("Serilog configured as the primary logging provider.");
    #endregion

    #region Add Core ASP.NET Core Services
    // ------------------- ۲. اضافه کردن سرویس‌های پایه ASP.NET Core -------------------
    // فعال کردن پشتیبانی از کنترلرهای API
    builder.Services.AddControllers();
    // فعال کردن API Explorer برای تولید مستندات Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    // پیکربندی Swagger/OpenAPI برای مستندسازی API
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Version = "v1",
            Title = "Forex Signal Bot API",
            Description = "API endpoints for the Forex Signal Bot application, including Telegram webhook and administrative functions.",
            // TermsOfService = new Uri("https://example.com/terms"),
            // Contact = new OpenApiContact { Name = "Support", Email = "support@example.com" },
            // License = new OpenApiLicense { Name = "License", Url = new Uri("https://example.com/license") }
        });
        //  در صورت نیاز، می‌توانید امنیت (مانند JWT Bearer) را به Swagger اضافه کنید
        // var jwtSecurityScheme = new OpenApiSecurityScheme { ... };
        // options.AddSecurityDefinition("Bearer", jwtSecurityScheme);
        // options.AddSecurityRequirement(new OpenApiSecurityRequirement { { jwtSecurityScheme, Array.Empty<string>() } });
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

    SqlServiceManager.EnsurePostgresServiceRunning();
    //  ❌❌ یادآوری: رجیستری‌های تکراری یا جابجا شده باید از اینجا حذف شده باشند ❌❌
    //  MediatR باید در AddApplicationServices با اسمبلی لایه Application رجیستر شود.
    //  ISignalService و سایر سرویس‌های لایه Application باید در AddApplicationServices رجیستر شوند.
    #endregion

    #region Configure Hangfire
    // ------------------- ۵. پیکربندی Hangfire برای اجرای کارهای پس‌زمینه -------------------
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("Hangfire")));
    builder.Services.AddHangfireServer();
    Log.Information("Hangfire services configured with PostgreSQL storage.");
    builder.Services.Configure<List<Infrastructure.Settings.ForwardingRule>>(
        builder.Configuration.GetSection("ForwardingRules"));
    builder.Services.AddScoped<IActualTelegramMessageActions, ActualTelegramMessageActions>();
    builder.Services.AddScoped<ITelegramMessageSender, HangfireRelayTelegramMessageSender>();
    builder.Services.AddScoped<IForwardingJobActions, ForwardingJobActions>();
    #endregion

    // ------------------- ساخت WebApplication instance -------------------
    var app = builder.Build(); //  ساخت برنامه با تمام سرویس‌های پیکربندی شده
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

    //  پیکربندی‌های مختص محیط توسعه
    if (app.Environment.IsDevelopment())
    {
        programLogger.LogInformation("Development environment detected. Enabling Swagger UI and Developer Exception Page.");
        app.UseSwagger(); //  فعال کردن Middleware برای تولید مستندات Swagger JSON
        app.UseSwaggerUI(c => //  فعال کردن Middleware برای نمایش Swagger UI
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Forex Signal Bot API V1");
            // c.RoutePrefix = string.Empty; //  برای نمایش Swagger در ریشه آدرس سایت (اختیاری)
        });
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
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
        endpoints.MapHangfireDashboard();
    });

    programLogger.LogInformation("HTTP request pipeline configured.");
    #endregion

    #region Configure Hangfire Dashboard & Recurring Jobs
    // ------------------- ۷. اضافه کردن داشبورد Hangfire -------------------
    var hangfireDashboardOptions = new DashboardOptions
    {
        DashboardTitle = "Forex Trading Bot - Background Jobs Monitor",
        IgnoreAntiforgeryToken = true, //  معمولاً برای داشبوردهای داخلی لازم است
        //  ⚠️ برای محیط توسعه، اجازه دسترسی بدون احراز هویت به داشبورد داده شده است.
        //  برای محیط Production، باید حتماً از یک فیلتر احراز هویت امن استفاده کنید.
        Authorization = Array.Empty<IDashboardAuthorizationFilter>() //  ⚠️ فقط برای توسعه و تست محلی! ⚠️
        //  مثال برای فعال کردن فیلتر سفارشی (اگر کلاس HangfireNoAuthFilter را دارید):
        //  Authorization = new[] { new WebAPI.Filters.HangfireNoAuthFilter() }
    };
    app.UseHangfireDashboard("/hangfire", hangfireDashboardOptions); //  داشبورد در مسیر /hangfire در دسترس خواهد بود
    programLogger.LogInformation("Hangfire Dashboard configured at /hangfire (For development, open to all. Secure for production!).");

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
                cronExpression: Cron.MinuteInterval(1),    // ✅ برای تست: هر ۱ دقیقه اجرا شود. برای Production بیشتر کنید (مثلاً "*/15 * * * *")
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc } //  اجرای Job بر اساس زمان UTC
            );
            programLogger.LogInformation("Recurring job 'fetch-all-active-rss-feeds' scheduled to run every 1 minute (for testing).");

            //  می‌توانید Job های تکرارشونده دیگری را نیز در اینجا اضافه کنید.
            // RecurringJob.AddOrUpdate("daily-cleanup", () => Console.WriteLine("Daily cleanup job!"), Cron.Daily);

            //  (اختیاری) اجرای فوری Job برای اولین بار پس از شروع برنامه (فقط برای تست)
            // var backgroundJobClient = app.Services.GetRequiredService<IBackgroundJobClient>();
            // backgroundJobClient.Enqueue<IRssFetchingCoordinatorService>(service => service.FetchAllActiveFeedsAsync(CancellationToken.None));
            // programLogger.LogInformation("Manually enqueued 'FetchAllActiveFeedsAsync' job for immediate initial run for testing purposes.");
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
    using (var scope = app.Services.CreateScope())
    {
        var orchestrator = scope.ServiceProvider.GetRequiredService<UserApiForwardingOrchestrator>();
        // Use orchestrator if needed
    }
    app.Run(); //  شروع به گوش دادن به درخواست‌های HTTP و اجرای برنامه
    #endregion

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Warning);
    builder.Logging.AddDebug();
    builder.Logging.AddTelegramLogger("/app/logs/telegram.log");
}
catch (Exception ex)
{
    //  لاگ کردن خطاهای بسیار بحرانی که مانع از اجرای برنامه شده‌اند.
    Log.Fatal(ex, "Application host terminated unexpectedly.");
    // Environment.ExitCode = 1; //  برای نشان دادن خروج ناموفق به سیستم عامل یا اسکریپت‌های دیگر
}
finally
{
    Log.Information("--------------------------------------------------");
    Log.Information("Application Shutting Down...");
    Log.CloseAndFlush(); //  بسیار مهم: اطمینان از نوشته شدن تمام لاگ‌های بافر شده قبل از خروج کامل برنامه
    Log.Information("--------------------------------------------------");
}