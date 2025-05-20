using Application.Common.Interfaces; // برای IUserRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore; // برای متدهای EF Core مانند FirstOrDefaultAsync, ToListAsync و ...
using System.Linq.Expressions;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت کاربر (User).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class UserRepository : IUserRepository
    {
        private readonly IAppDbContext _context;

        public UserRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }




        public async Task<IEnumerable<User>> GetUsersForNewsNotificationAsync(
           Guid? newsItemSignalCategoryId,
           bool isNewsItemVipOnly,
           CancellationToken cancellationToken = default)
        {


            // شروع با کوئری پایه: کاربرانی که نوتیفیکیشن اخبار RSS را فعال کرده‌اند.
            // و همچنین کاربران فعال سیستم (اگر فیلد IsActive در User دارید، آن را هم اضافه کنید)
            IQueryable<User> query = _context.Users
                                          .Where(u => u.EnableRssNewsNotifications == true);
            // .Where(u => u.IsActive == true); // مثال: اگر فیلد IsActive برای User دارید

            // مرحله ۱: فیلتر بر اساس VIP بودن خبر و اشتراک کاربر
            if (isNewsItemVipOnly)
            {

                // کاربر باید یک اشتراک فعال داشته باشد که به او دسترسی VIP می‌دهد.
                // این منطق باید با ساختار جدول Subscriptions و Plans شما هماهنگ باشد.
                // فرض می‌کنیم یک پلن VIP، PlanId خاصی دارد یا یک فیلد IsVip در Subscription.
                // در اینجا، ما بررسی می‌کنیم که آیا کاربر حداقل یک اشتراک فعال دارد که EndDate آن در آینده است.
                // شما باید این شرط را دقیق‌تر کنید تا فقط پلن‌های VIP را در نظر بگیرد.
                // مثال: query = query.Where(u => u.Subscriptions.Any(s => s.EndDate >= DateTime.UtcNow && s.Plan.IsVip == true));
                // برای سادگی فعلی، فرض می‌کنیم هر اشتراک فعالی دسترسی VIP می‌دهد (این باید اصلاح شود).
                // یا اگر User.Level را برای VIP بودن استفاده می‌کنید (که کمتر دقیق است):
                // query = query.Where(u => u.Level != UserLevel.Free);

                // روش دقیق‌تر با Join (اگر لازم باشد و Include کافی نباشد یا پیچیده شود):
                // این روش فرض می‌کند که User.Subscriptions به درستی بارگذاری می‌شود یا EF Core می‌تواند join مناسبی ایجاد کند.
                query = query.Where(u => u.Subscriptions.Any(s =>
                                            s.EndDate >= DateTime.UtcNow &&
                                            IsConsideredVipSubscription(s) //  یک متد برای تشخیص پلن VIP
                                        ));
                //  اگر IsConsideredVipSubscription نیاز به دسترسی به جدول Plans دارد،
                //  ممکن است نیاز به یک Subquery یا یک View در دیتابیس داشته باشید برای بهینه‌سازی.
            }

            // مرحله ۲: فیلتر بر اساس دسته‌بندی خبر و تنظیمات برگزیده کاربر
            if (newsItemSignalCategoryId.HasValue)
            {

                // کاربر باید:
                //  الف) هیچ تنظیمات برگزیده‌ای برای دسته‌بندی نداشته باشد (یعنی تمام دسته‌بندی‌ها را می‌خواهد دریافت کند)، یا
                //  ب) دسته‌بندی خبر در لیست تنظیمات برگزیده او باشد.
                // این سیاست "دریافت همه در صورت عدم تنظیم ترجیح" است.
                // اگر می‌خواهید کاربر حتماً دسته‌بندی را انتخاب کرده باشد، شرط !u.Preferences.Any() را حذف کنید.
                query = query.Where(u =>
                    !u.Preferences.Any() || // کاربر هیچ ترجیحی تنظیم نکرده است (همه را دریافت می‌کند)
                    u.Preferences.Any(p => p.CategoryId == newsItemSignalCategoryId.Value) // یا به این دسته خاص علاقه‌مند است
                );
            }

            // مرحله ۳ (اختیاری - برای آینده): فیلتر بر اساس زبان کاربر
            // if (!string.IsNullOrWhiteSpace(newsItemLanguageCode)) // فرض: خبر یک کد زبان دارد
            // {
            //     query = query.Where(u => u.PreferredLanguage == newsItemLanguageCode || u.PreferredLanguage == "all");
            // }

            // انتخاب نهایی کاربران
            // پراپرتی‌های لازم (مانند TelegramId, Username) به طور خودکار با User entity بارگذاری می‌شوند.
            // نیازی به Include صریح نیست مگر اینکه بخواهید روابطی را که در این کوئری استفاده نشده‌اند، بارگذاری کنید.
            var eligibleUsers = await query
                                      .AsNoTracking() //  برای کوئری‌های فقط خواندنی، عملکرد را بهبود می‌بخشد
                                      .ToListAsync(cancellationToken);


            return eligibleUsers;
        }

        /// <summary>
        /// (Private Helper) Determines if a subscription grants VIP access.
        /// This logic needs to be customized based on your subscription/plan structure.
        /// </summary>
        private bool IsConsideredVipSubscription(Subscription subscription)
        {
            //  این یک مثال بسیار ساده است. شما باید منطق واقعی خود را اینجا پیاده‌سازی کنید.
            //  مثلاً بر اساس Subscription.PlanId یا یک فیلد Subscription.IsVip.
            //  یا اگر User.Level را آپدیت می‌کنید بر اساس اشتراک، می‌توانید از آن استفاده کنید،
            //  اما چک کردن مستقیم اشتراک قابل اعتمادتر است.
            if (subscription == null) return false;

            //  مثال ۱: اگر یک فیلد IsVip در موجودیت Plan (که Subscription به آن لینک شده) دارید:
            //  return subscription.Plan != null && subscription.Plan.IsVip;

            //  مثال ۲: اگر شناسه‌های پلن VIP را می‌دانید:
            //  var vipPlanIds = new List<Guid> { Guid.Parse("VIP_PLAN_ID_1"), Guid.Parse("VIP_PLAN_ID_2") };
            //  return subscription.PlanId.HasValue && vipPlanIds.Contains(subscription.PlanId.Value);

            //  مثال ۳: اگر UserLevel کاربر بر اساس اشتراک آپدیت می‌شود و می‌خواهید از آن استفاده کنید (کمتر توصیه می‌شود برای این فیلتر خاص)
            //  return userOwningTheSubscription.Level >= UserLevel.Premium;

            //  فعلاً فرض می‌کنیم هر اشتراک غیر رایگانی، VIP است (این باید اصلاح شود)
            //  این یعنی باید به نوعی به UserLevel دسترسی داشته باشیم یا به نوع پلن اشتراک.
            //  اگر UserLevel در User.cs بر اساس اشتراک آپدیت می‌شود، می‌توان از آن استفاده کرد.
            //  اما برای این کوئری، بهتر است مستقیماً از اطلاعات Subscription استفاده کنیم.
            //  اگر Subscription هیچ فیلدی برای تشخیص VIP بودن ندارد، باید آن را اضافه کنید.
            //  برای این مثال، فرض می‌کنیم هر اشتراک فعالی (که قبلاً در کوئری فیلتر شده) کافی است
            //  (این یعنی isNewsItemVipOnly فقط بررسی می‌کند که کاربر اشتراک دارد یا نه، نه نوع آن را)
            //  این باید با دقت بیشتری بر اساس نیاز شما پیاده‌سازی شود.
            // _logger.LogDebug("IsConsideredVipSubscription: Checking subscription ID {SubscriptionId}. Logic needs to be implemented based on your plan structure.", subscription.Id);
            //  فعلاً به عنوان placeholder، هر اشتراک فعالی را VIP در نظر می‌گیریم (این باید تغییر کند)
            return true; //  ⚠️ Placeholder - این را با منطق واقعی تشخیص VIP جایگزین کنید!
        }

        public async Task<IEnumerable<User>> GetUsersWithNotificationSettingAsync(
            Expression<Func<User, bool>> notificationPredicate,
            CancellationToken cancellationToken = default)
        {
            if (notificationPredicate == null)
            {
                throw new ArgumentNullException(nameof(notificationPredicate));
            }

            //  می‌توانید فیلترهای عمومی دیگری هم اینجا اضافه کنید، مثلاً فقط کاربران فعال
            //  .Where(u => u.IsActive) // اگر فیلد IsActive در User دارید
            return await _context.Users
                .Where(notificationPredicate)
                .ToListAsync(cancellationToken);
        }

        public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet) // مثال: بارگذاری موجودیت مرتبط
                                             // .Include(u => u.Subscriptions) // در صورت نیاز
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        }

        public async Task<User?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
        }

        public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken); // مقایسه case-insensitive
        }

        public async Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<User>> FindAsync(Expression<Func<User, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Where(predicate)
                .Include(u => u.TokenWallet)
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            await _context.Users.AddAsync(user, cancellationToken);
            // SaveChangesAsync باید در سطح Unit of Work یا سرویس فراخوانی شود.
        }

        public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            // EF Core به طور خودکار تغییرات موجودیت‌های ردیابی شده را تشخیص می‌دهد.
            // _context.Users.Update(user); // این معمولاً لازم نیست اگر موجودیت قبلاً ردیابی شده باشد.
            // اگر موجودیت ردیابی نشده، باید آن را Attach و State آن را Modified کنید.
            _context.Users.Entry(user).State = EntityState.Modified; // یک راه برای اطمینان از علامت‌گذاری به عنوان ویرایش شده
            return Task.CompletedTask;
            // SaveChangesAsync باید در سطح Unit of Work یا سرویس فراخوانی شود.
        }

        public async Task DeleteAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            _context.Users.Remove(user);
            await Task.CompletedTask; // به تعویق انداختن حذف واقعی تا SaveChangesAsync
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userToDelete = await GetByIdAsync(id, cancellationToken);
            if (userToDelete != null)
            {
                _context.Users.Remove(userToDelete);
            }
            // SaveChangesAsync باید در سطح Unit of Work یا سرویس فراخوانی شود.
        }

        public async Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            return await _context.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken);
        }

        public async Task<bool> ExistsByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            return await _context.Users.AnyAsync(u => u.TelegramId == telegramId, cancellationToken);
        }

        // پیاده‌سازی متدهای اضافی در صورت نیاز
        // public async Task<User?> GetUserWithSubscriptionsAsync(Guid userId, CancellationToken cancellationToken = default)
        // {
        //     return await _context.Users
        //         .Include(u => u.Subscriptions)
        //         .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        // }
    }
}