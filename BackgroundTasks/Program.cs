HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddStrongWorker(options =>
{
    // در اینجا می‌توانید تنظیمات را از فایل appsettings.json بخوانید
    // اما فعلاً از مقادیر پیش‌فرض استفاده می‌شود که کافی است.
});
builder.Services.AddLogging(configure => configure.AddConsole());
IHost host = builder.Build();
host.Run();
