using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Application.Interface;
using Application.Interfaces;
using Application.Services;
namespace Application
{
    /// <summary>
    /// کلاس کمکی برای افزودن سرویس‌های لایه Application به IServiceCollection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// سرویس‌های پایه لایه Application (مانند MediatR, AutoMapper, FluentValidation) را اضافه می‌کند.
        /// سرویس‌های خاص برنامه باید پس از ایجاد، به این متد اضافه شوند.
        /// </summary>
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            // 1. رجیستر کردن AutoMapper
            // تمام پروفایل‌های مپینگ در اسمبلی جاری (Application) را پیدا و رجیستر می‌کند.
            // پیش‌نیاز: فایل MappingProfile.cs باید در Application/Common/Mappings/ وجود داشته باشد.
            services.AddAutoMapper(Assembly.GetExecutingAssembly());

            // 2. رجیستر کردن FluentValidation
            // تمام ولیدیتورهایی که از AbstractValidator<T> ارث‌بری می‌کنند را در اسمبلی جاری پیدا و رجیستر می‌کند.
            // پیش‌نیاز: بسته NuGet FluentValidation.DependencyInjectionExtensions باید نصب شده باشد.
            // و کلاس‌های Validator باید در این اسمبلی تعریف شده باشند (معمولاً برای Command/Query ها).
            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

            // 3. رجیستر کردن MediatR
            // تمام Handler ها و Request ها را در اسمبلی جاری پیدا و رجیستر می‌کند.
            services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());

                // 4. رجیستر کردن MediatR Pipeline Behaviors (فعلاً کامنت)
                // برای فعال کردن، ابتدا کلاس‌های Behavior (مانند ValidationBehaviour) را ایجاد کنید
                // و سپس این خطوط را از کامنت خارج کرده و تنظیم کنید.
                // مثال:
                // cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
            });

            // 5. رجیستر کردن سرویس‌های لایه Application (فعلاً کامنت)
            // پس از ایجاد اینترفیس‌ها و پیاده‌سازی‌های سرویس، آن‌ها را در اینجا رجیستر کنید.
            // مثال:
             services.AddScoped<IUserService, UserService>();
            // ... و سایر سرویس‌ها

            return services;
        }
    }
}