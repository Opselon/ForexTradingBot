// Add services to the container.
builder.Services.AddTelegramPanelServices(builder.Configuration);

// Register HttpClient for Frankfurter.app
builder.Services.AddHttpClient("FrankfurterApiClient", client =>
{
    client.BaseAddress = new Uri("https://api.frankfurter.app/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle 