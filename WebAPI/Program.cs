using Application; // این using برای دسترسی به ServiceCollectionExtensions.AddApplicationServices ضروری است
using Application.Interfaces;
using Application.Services;
using Infrastructure;
using Infrastructure.Data; // احتمالاً برای AddInfrastructure نیاز است
using Microsoft.OpenApi.Models;
using Serilog;
using TelegramPanel.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ... (بقیه پیکربندی‌های Serilog و Controllers و Swagger) ...

builder.Host.UseSerilog((ctx, lc) => lc
    .WriteTo.Console()
    .ReadFrom.Configuration(ctx.Configuration));

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


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization(); // اگر از احراز هویت و سطوح دسترسی استفاده می‌کنید

app.MapControllers();

app.Run();