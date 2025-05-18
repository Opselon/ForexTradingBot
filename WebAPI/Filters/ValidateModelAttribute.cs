// این Attribute بیشتر در لایه Presentation (WebAPI) کاربرد دارد.
// اگر پروژه Shared شما به بسته‌های ASP.NET Core ارجاع نمی‌دهد،
// بهتر است این Attribute را در پروژه WebAPI یا یک پروژه Shared دیگر که به ASP.NET Core ارجاع دارد، قرار دهید.
// با این حال، برای تکمیل ساختار، اینجا قرار می‌دهیم.

#if !(NETSTANDARD || NETCOREAPP3_1_OR_GREATER && !NET5_0_OR_GREATER) // شرط برای اطمینان از وجود ActionFilterAttribute
// اگر به Microsoft.AspNetCore.Mvc.Core ارجاع ندارید، این بخش را کامنت کنید یا حذف کنید.
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Shared.Attributes
{
    /// <summary>
    /// یک Action Filter Attribute برای اعتبارسنجی خودکار ModelState قبل از اجرای Action در کنترلر.
    /// اگر ModelState نامعتبر باشد، یک BadRequestObjectResult با خطاهای اعتبارسنجی برمی‌گرداند.
    /// </summary>
    public class ValidateModelAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (!context.ModelState.IsValid)
            {
                // می‌توانید نحوه بازگرداندن خطاها را سفارشی کنید.
                // مثال: بازگرداندن یک Result سفارشی یا یک ساختار خطای استاندارد.
                var errors = context.ModelState
                    .Where(x => x.Value != null && x.Value.Errors.Any())
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray()
                    );

                // context.Result = new BadRequestObjectResult(context.ModelState); // روش استاندارد ASP.NET Core
                context.Result = new UnprocessableEntityObjectResult(new { Message = "Validation Failed", Errors = errors }); // یا 422
            }

            base.OnActionExecuting(context);
        }
    }
}
#endif