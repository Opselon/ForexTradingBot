#region Usings
// using های استاندارد .NET و NuGet Packages
// using های مربوط به پروژه Application شما
using Application.Common.Interfaces;       // برای اینترفیس‌های عمومی مانند IAppDbContext, INotificationService, و تمام اینترفیس‌های Repository
using Application.Interface;                // ✅ Namespace اصلی برای پیاده‌سازی‌های سرویس (UserService, SignalService, و غیره)
using Application.Interfaces;              // ✅ Namespace اصلی برای اینترفیس‌های سرویس (IUserService, ISignalService, و غیره)
using Application.Services;
using FluentValidation;                     // برای services.AddValidatorsFromAssembly()
using Microsoft.Extensions.DependencyInjection; // برای IServiceCollection و متدهای توسعه‌دهنده DI
using Microsoft.Extensions.Logging;         // برای ILogger (مثلاً در DummyNotificationService)
using System.Reflection;                    // برای Assembly.GetExecutingAssembly()
#endregion

namespace Application // ✅ Namespace ریشه پروژه Application
{
    /// <summary>
    /// کلاس استاتیک حاوی متدهای توسعه‌دهنده برای IServiceCollection،
    /// به منظور رجیستر کردن سرویس‌ها و وابستگی‌های لایه Application.
    /// </summary>
    public static class DependencyInjection // نام کلاس می‌تواند DependencyInjection یا ServiceCollectionExtensions باشد
    {
        /// <summary>
        /// تمام سرویس‌های مورد نیاز لایه Application را به کانتینر Dependency Injection اضافه می‌کند.
        /// این متد باید در Program.cs (یا Startup.cs) پروژه اجرایی فراخوانی شود.
        /// </summary>
        /// <param name="services">مجموعه سرویس‌های IServiceCollection برای اضافه کردن وابستگی‌ها.</param>
        /// <returns>IServiceCollection پیکربندی شده با سرویس‌های Application.</returns>
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            #region Core Application Tools Registration
            // ------------------- ۱. رجیستر کردن AutoMapper -------------------
            // AutoMapper برای تبدیل (مپینگ) بین آبجکت‌ها استفاده می‌شود، به خصوص بین Entities و DTOs.
            // Assembly.GetExecutingAssembly() باعث می‌شود AutoMapper تمام کلاس‌هایی را که از Profile ارث‌بری می‌کنند
            // در اسمبلی فعلی (Application) پیدا و پروفایل‌های مپینگ آن‌ها را رجیستر کند.
            // پیش‌نیاز: باید یک یا چند کلاس MappingProfile در Application/Common/Mappings/ داشته باشید.
            services.AddAutoMapper(Assembly.GetExecutingAssembly());
            // Comment: Registers AutoMapper profiles from the current assembly (Application layer).

            // ------------------- ۲. رجیستر کردن FluentValidation -------------------
            // FluentValidation برای اعتبارسنجی پیچیده‌تر و خواناتر آبجکت‌ها (معمولاً DTOs یا Commands/Queries) استفاده می‌شود.
            // این متد تمام کلاس‌هایی را که از AbstractValidator<T> ارث‌بری می‌کنند در اسمبلی فعلی پیدا و رجیستر می‌کند.
            // پیش‌نیاز: بسته NuGet "FluentValidation.DependencyInjectionExtensions" باید نصب شده باشد.
            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
            // Comment: Registers all FluentValidation validators from the current assembly.

            // ------------------- ۳. رجیستر کردن MediatR -------------------
            // MediatR برای پیاده‌سازی الگوهای CQRS (Command Query Responsibility Segregation) و Mediator استفاده می‌شود.
            // این متد تمام Request Handler ها (IRequestHandler<TRequest, TResponse>)، Command ها (IRequest<TResponse>)،
            // Query ها (IRequest<TResponse>) و Notification Handler ها (INotificationHandler<TNotification>) را
            // در اسمبلی فعلی پیدا و رجیستر می‌کند.
            services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
                // Comment: Registers MediatR handlers, requests, and notifications from the current assembly.

                // ------------------- (اختیاری) رجیستر کردن MediatR Pipeline Behaviors -------------------
                // Pipeline Behaviors به شما اجازه می‌دهند منطق مشترکی را قبل یا بعد از اجرای Request Handler ها اجرا کنید
                // (مانند لاگینگ، اعتبارسنجی، مدیریت خطا، کشینگ، عملکردسنجی).
                // ترتیب رجیستر کردن Behavior ها مهم است.

                // مثال: فعال کردن ValidationBehaviour (اگر کلاس آن را در Application/Common/Behaviours/ ایجاد کرده‌اید)
                // این Behavior به طور خودکار ولیدیتورهای FluentValidation را برای Request های ورودی اجرا می‌کند.
                // cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
                // Comment: Example for registering a MediatR validation pipeline behavior. Uncomment and implement ValidationBehaviour<,>.

                // cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehaviour<,>));
                // Comment: Example for a logging pipeline behavior.

                // cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehaviour<,>));
                // Comment: Example for an unhandled exception pipeline behavior.
            });
            #endregion

            #region Application Services Registration
            // ------------------- ۴. رجیستر کردن سرویس‌های خاص لایه Application -------------------
            // در اینجا، اینترفیس‌های سرویس (تعریف شده در Application/Interfaces/) به پیاده‌سازی‌های مربوطه خود
            // (تعریف شده در Application/Services/) مپ می‌شوند.
            // طول عمر Scoped معمولاً برای سرویس‌هایی که با DbContext (که Scoped است) کار می‌کنند یا وضعیت درخواست را نگه می‌دارند، مناسب است.
            // Transient برای سرویس‌های سبک و بدون state.
            // Singleton برای سرویس‌هایی که در طول عمر برنامه فقط یک نمونه از آن‌ها کافی است و thread-safe هستند.

            // سرویس مدیریت کاربران
            services.AddScoped<IUserService, UserService>();
            // Comment: Registers UserService for handling user-related business logic.

            // سرویس مدیریت سیگنال‌ها
            services.AddScoped<ISignalService, SignalService>();
            // Comment: Registers SignalService for handling signal-related business logic.

            // سرویس مدیریت اشتراک‌ها
            services.AddScoped<ISubscriptionService, SubscriptionService>();
            // Comment: Registers SubscriptionService for managing user subscriptions.

            // سرویس مدیریت پرداخت‌ها (ایجاد فاکتور و ...)
            services.AddScoped<IPaymentService, PaymentService>();
            // Comment: Registers PaymentService for handling payment initiation and related logic.

            // سرویس مدیریت تاییدیه پرداخت‌ها (پس از دریافت Webhook از درگاه)
            services.AddScoped<IPaymentConfirmationService, PaymentConfirmationService>();
            // Comment: Registers PaymentConfirmationService for processing successful payment confirmations.

            // سرویس مدیریت دسته‌بندی سیگنال‌ها (اگر منطق خاصی فراتر از CRUD Repository دارد)
            // services.AddScoped<ISignalCategoryService, SignalCategoryService>();
            // Comment: Example: Registers SignalCategoryService if there's business logic beyond repository.

            // سرویس مدیریت منابع RSS (اگر منطق خاصی مانند پردازش فیدها در این لایه است)
            // services.AddScoped<IRssSourceService, RssSourceService>();
            // Comment: Example: Registers RssSourceService for RSS feed processing logic.


            // ------------------- پیاده‌سازی پیش‌فرض/Dummy برای اینترفیس‌های عمومی -------------------
            // این پیاده‌سازی‌ها به لایه Application اجازه می‌دهند بدون وابستگی به پیاده‌سازی‌های لایه‌های دیگر (مانند TelegramPanel)
            // کامپایل و تست شوند. پیاده‌سازی واقعی در لایه مربوطه (مثلاً TelegramPanel) این رجیستری را override خواهد کرد.

            // سرویس عمومی نوتیفیکیشن (پیاده‌سازی Dummy)
            services.AddScoped<INotificationService, DummyNotificationService>();
            // Comment: Registers a dummy implementation for INotificationService.
            // The actual implementation (e.g., TelegramNotificationService) should be registered
            // in the respective presentation layer (TelegramPanel) to override this.

            #endregion

            return services;
        }
    }

    #region Dummy Notification Service Implementation (Example)
    // این کلاس می‌تواند در یک فایل جداگانه (مثلاً Application/Services/DummyNotificationService.cs) قرار گیرد.
    // برای سادگی، اینجا آورده شده است.
    /// <summary>
    /// پیاده‌سازی ساده و جایگزین (Dummy) برای INotificationService.
    /// فقط پیام‌ها را لاگ می‌کند و برای تست یا زمانی که پیاده‌سازی واقعی در دسترس نیست، استفاده می‌شود.
    /// </summary>
    internal class DummyNotificationService : INotificationService
    {
        private readonly ILogger<DummyNotificationService> _logger;

        public DummyNotificationService(ILogger<DummyNotificationService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task SendNotificationAsync(string recipientIdentifier, string message, bool useRichText = false, CancellationToken cancellationToken = default)
        {
            //  برای جلوگیری از نمایش کامل پیام‌های طولانی در لاگ، می‌توان آن را کوتاه کرد.
            string messageExcerpt = message.Length > 100 ? message.Substring(0, 97) + "..." : message;

            _logger.LogInformation(
                "[DUMMY NOTIFICATION SENT] To Recipient: {RecipientIdentifier}, RichText: {UseRichText}, Message (Excerpt): {MessageExcerpt}",
                recipientIdentifier,
                useRichText,
                messageExcerpt);

            // چون این یک پیاده‌سازی Dummy است، هیچ عملیات خارجی انجام نمی‌دهد.
            return Task.CompletedTask;
        }
    }
    #endregion
}