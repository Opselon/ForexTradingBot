// Add services to the container.
builder.Services.AddTelegramPanelServices(builder.Configuration);

// Configure AutoForward settings
builder.Services.Configure<Domain.Settings.AutoForwardSettings>(
    builder.Configuration.GetSection("TelegramSettings:AutoForward"));

// Register AutoForward service
builder.Services.AddScoped<Application.Features.Forwarding.Services.AutoForwardService>();

// Register HttpClient for Frankfurter.app
builder.Services.AddHttpClient("FrankfurterApiClient", client =>
{
    client.BaseAddress = new Uri("https://api.frankfurter.app/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle 