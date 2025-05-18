using Application; // این using برای دسترسی به ServiceCollectionExtensions.AddApplicationServices ضروری است
using Application.Interfaces;
using Application.Services;
using Hangfire;
using Hangfire.MemoryStorage;
using Infrastructure;
using Microsoft.OpenApi.Models;
using Serilog;
using TelegramPanel.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ... (بقیه پیکربندی‌های Serilog و Controllers و Swagger) ...

builder.Host.UseSerilog((ctx, lc) => lc
    .WriteTo.Console()
    .ReadFrom.Configuration(ctx.Configuration));



#region Hangfire Configuration

// ۱. اضافه کردن سرویس‌های Hangfire به کانتینر DI
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180) //  یا بالاترین نسخه سازگار
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    // .UseConsole() // (اختیاری) برای لاگ کردن جزئیات Hangfire در کنسول
    //  انتخاب Storage برای Hangfire:
    //  گزینه الف: MemoryStorage (برای تست و توسعه، داده‌ها با ریستارت برنامه از بین می‌روند)
    .UseMemoryStorage()
//  گزینه ب: SQL Server Storage (نیاز به Connection String دارد، جداول را خودش می‌سازد)
/*
.UseSqlServerStorage(builder.Configuration.GetConnectionString("HangfireConnection"), new SqlServerStorageOptions
{
    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
    QueuePollInterval = TimeSpan.Zero, // استفاده از سیگنالینگ SQL Server به جای Polling مداوم
    UseRecommendedIsolationLevel = true,
    DisableGlobalLocks = true //  اگر از چندین سرور استفاده نمی‌کنید، می‌تواند عملکرد را بهبود بخشد
})
*/
//  گزینه ج: PostgreSQL Storage (باید بسته Hangfire.PostgreSqlStorage نصب شود)
/*
.UseNpgsqlStorage(builder.Configuration.GetConnectionString("HangfireConnection"), new NpgsqlStorageOptions
{
    // تنظیمات خاص PostgreSQL
})
*/
);

// ۲. اضافه کردن سرور Hangfire برای پردازش Job ها
// این باعث می‌شود Job ها در پس‌زمینه اجرا شوند.
builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"{Environment.MachineName}:ForexBot:{Guid.NewGuid().ToString("N").Substring(0, 6)}"; // نام منحصر به فرد برای هر سرور
    options.WorkerCount = Environment.ProcessorCount * 2; // تعداد Worker ها (قابل تنظیم)
    options.Queues = new[] { "default", "critical", "rss_processing", "notifications" }; // تعریف صف‌های مختلف (اختیاری)
    options.SchedulePollingInterval = TimeSpan.FromSeconds(15); // فرکانس بررسی Job های زمان‌بندی شده
});
#endregion



builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new OpenApiInfo { Title = "Forex Signal Bot API", Version = "v1" });
});

builder.Services.Configure<Domain.Settings.TelegramSettings>(builder.Configuration.GetSection("TelegramSettings"));

// این دو خط احتمالاً اضافی هستند اگر MediatR و AutoMapper را در AddApplicationServices رجیستر می‌کنید:
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
// builder.Services.AddAutoMapper(typeof(Program));
// مگر اینکه بخواهید Handler ها یا Profile های موجود در اسمبلی WebAPI را هم رجیستر کنید.
// معمولاً Handler ها و Profile ها در لایه Application هستند.
builder.Services.AddTelegramPanelServices(builder.Configuration);
// اینجا نام متد را اصلاح کنید:

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddScoped<ISignalService, SignalService>();
builder.Services.Configure<Shared.Settings.CryptoPaySettings>(builder.Configuration.GetSection(Shared.Settings.CryptoPaySettings.SectionName));
var app = builder.Build();
#region Hangfire Dashboard Configuration
// ۳. اضافه کردن Dashboard Hangfire (اختیاری اما بسیار مفید برای مانیتورینگ Job ها)
// مسیر پیش‌فرض: /hangfire
//  می‌توانید با Authorization آن را امن کنید.
var hangfireDashboardOptions = new DashboardOptions
{
    DashboardTitle = "Forex Trading Bot - Background Jobs",
    //Authorization = new[] { new HangfireNoAuthFilter() } //  ⚠️ برای توسعه: اجازه دسترسی بدون احراز هویت.
    //  برای Production، حتماً از یک Authorization Filter امن استفاده کنید.
    //  مثال: new HangfireCustomBasicAuthenticationFilter { User = "admin", Pass = "secret" }
    //  یا new HangfireAspNetCoreDashboardAuthorizationFilter (نیاز به پیاده‌سازی دارد)
};
app.UseHangfireDashboard("/hangfire", hangfireDashboardOptions);
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Hangfire Dashboard configured at /hangfire");
#endregion

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization(); // اگر از احراز هویت و سطوح دسترسی استفاده می‌کنید

app.MapControllers();

app.Run();