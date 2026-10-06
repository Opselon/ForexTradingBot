using Application.DTOs; // Namespace اصلی DTO ها
using Application.DTOs.News;
using AutoMapper;
using Domain.Entities;

namespace Application.Common.Mappings
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {

            #region News Mappings
            CreateMap<NewsItem, NewsItemDto>()
                .ForMember(dest => dest.SourceName, opt => opt.MapFrom(src => src.RssSource.SourceName)) // مپ کردن نام از RssSource مرتبط
                .ForMember(dest => dest.CreatedAtInSystem, opt => opt.MapFrom(src => src.CreatedAt));
            //  مپینگ برای CreateNewsItemDto به NewsItem (اگر DTO برای ایجاد دارید)
            // CreateMap<CreateNewsItemDto, NewsItem>();
            #endregion

            // User Mappings
            CreateMap<User, UserDto>()
                .ForMember(dest => dest.Level, opt => opt.MapFrom(src => src.Level.ToString()))
                .ForMember(dest => dest.TokenBalance, opt => opt.MapFrom(src => src.TokenWallet != null ? src.TokenWallet.Balance : 0m))
                .ForMember(dest => dest.TokenWallet, opt => opt.MapFrom(src => src.TokenWallet))
                .ForMember(dest => dest.ActiveSubscription, opt => opt.Ignore()); // این باید دستی مپ شود یا از طریق یک resolver
            CreateMap<RegisterUserDto, User>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Level, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.EnableGeneralNotifications, opt => opt.Ignore())
                .ForMember(dest => dest.EnableVipSignalNotifications, opt => opt.Ignore())
                .ForMember(dest => dest.EnableRssNewsNotifications, opt => opt.Ignore())
                .ForMember(dest => dest.PreferredLanguage, opt => opt.Ignore())
                .ForMember(dest => dest.TokenWallet, opt => opt.Ignore())
                .ForMember(dest => dest.Subscriptions, opt => opt.Ignore())
                .ForMember(dest => dest.Transactions, opt => opt.Ignore())
                .ForMember(dest => dest.Preferences, opt => opt.Ignore());
            CreateMap<UpdateUserDto, User>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.TelegramId, opt => opt.Ignore())
                .ForMember(dest => dest.Level, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.EnableGeneralNotifications, opt => opt.Ignore())
                .ForMember(dest => dest.EnableVipSignalNotifications, opt => opt.Ignore())
                .ForMember(dest => dest.EnableRssNewsNotifications, opt => opt.Ignore())
                .ForMember(dest => dest.PreferredLanguage, opt => opt.Ignore())
                .ForMember(dest => dest.TokenWallet, opt => opt.Ignore())
                .ForMember(dest => dest.Subscriptions, opt => opt.Ignore())
                .ForMember(dest => dest.Transactions, opt => opt.Ignore())
                .ForMember(dest => dest.Preferences, opt => opt.Ignore())
                .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null)); // فقط فیلدهای غیر null را مپ کن

            // TokenWallet Mappings
            CreateMap<TokenWallet, TokenWalletDto>();

            // Subscription Mappings
            CreateMap<Subscription, SubscriptionDto>()
                .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.IsCurrentlyActive)); // پراپرتی محاسباتی
            CreateMap<CreateSubscriptionDto, Subscription>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Status, opt => opt.Ignore())
                .ForMember(dest => dest.ActivatingTransactionId, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.User, opt => opt.Ignore());

            // SignalCategory Mappings
            CreateMap<SignalCategory, SignalCategoryDto>();
            CreateMap<CreateSignalCategoryDto, SignalCategory>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Description, opt => opt.Ignore())
                .ForMember(dest => dest.IsActive, opt => opt.Ignore())
                .ForMember(dest => dest.SortOrder, opt => opt.Ignore())
                .ForMember(dest => dest.Signals, opt => opt.Ignore())
                .ForMember(dest => dest.UserPreferences, opt => opt.Ignore());

            // Signal Mappings
            CreateMap<Signal, SignalDto>()
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.Type.ToString()))
                .ForMember(dest => dest.Source, opt => opt.MapFrom(src => src.SourceProvider))
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.PublishedAt))
                .ForMember(dest => dest.Category, opt => opt.MapFrom(src => src.Category)); // مپ کردن نویگیشن پراپرتی
            CreateMap<CreateSignalDto, Signal>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.Type))
                .ForMember(dest => dest.SourceProvider, opt => opt.Ignore())
                .ForMember(dest => dest.Status, opt => opt.Ignore())
                .ForMember(dest => dest.Timeframe, opt => opt.Ignore())
                .ForMember(dest => dest.Notes, opt => opt.Ignore())
                .ForMember(dest => dest.IsVipOnly, opt => opt.Ignore())
                .ForMember(dest => dest.PublishedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.ClosedAt, opt => opt.Ignore())
                .ForMember(dest => dest.CategoryId, opt => opt.MapFrom(src => src.CategoryId))
                .ForMember(dest => dest.Category, opt => opt.Ignore())
                .ForMember(dest => dest.Analyses, opt => opt.Ignore());

            // SignalAnalysis Mappings
            CreateMap<SignalAnalysis, SignalAnalysisDto>()
                .ForMember(dest => dest.Notes, opt => opt.MapFrom(src => src.AnalysisText));
            CreateMap<CreateSignalAnalysisDto, SignalAnalysis>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.AnalysisText, opt => opt.MapFrom(src => src.Notes))
                .ForMember(dest => dest.SentimentScore, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.Signal, opt => opt.Ignore());

            // RssSource Mappings
            CreateMap<RssSource, RssSourceDto>()
                .ForMember(dest => dest.DefaultSignalCategoryName,
                           opt => opt.MapFrom(src => src.DefaultSignalCategory != null ? src.DefaultSignalCategory.Name : null))
                .ForMember(dest => dest.LastFetchedAt, opt => opt.MapFrom(src => src.LastSuccessfulFetchAt));
            CreateMap<CreateRssSourceDto, RssSource>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.LastModifiedHeader, opt => opt.Ignore())
                .ForMember(dest => dest.LastFetchAttemptAt, opt => opt.Ignore())
                .ForMember(dest => dest.LastSuccessfulFetchAt, opt => opt.Ignore())
                .ForMember(dest => dest.FetchErrorCount, opt => opt.Ignore())
                .ForMember(dest => dest.DefaultSignalCategory, opt => opt.Ignore())
                .ForMember(dest => dest.NewsItems, opt => opt.Ignore());

            // Transaction Mappings
            CreateMap<Transaction, TransactionDto>()
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.Type.ToString()));
            CreateMap<CreateTransactionDto, Transaction>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.User, opt => opt.Ignore())
                .ForMember(dest => dest.Timestamp, opt => opt.Ignore())
                .ForMember(dest => dest.PaymentGatewayInvoiceId, opt => opt.Ignore())
                .ForMember(dest => dest.PaymentGatewayName, opt => opt.Ignore())
                .ForMember(dest => dest.Status, opt => opt.Ignore())
                .ForMember(dest => dest.PaidAt, opt => opt.Ignore())
                .ForMember(dest => dest.PaymentGatewayPayload, opt => opt.Ignore())
                .ForMember(dest => dest.PaymentGatewayResponse, opt => opt.Ignore());

            // UserSignalPreference Mappings
            CreateMap<UserSignalPreference, UserSignalPreferenceDto>()
                .ForMember(dest => dest.CategoryId, opt => opt.MapFrom(src => src.CategoryId))
                .ForMember(dest => dest.CategoryName, opt => opt.MapFrom(src => src.Category.Name)) // نیاز به Include(usp => usp.Category) در Repository
                .ForMember(dest => dest.SubscribedAt, opt => opt.MapFrom(src => src.CreatedAt));
            // برای SetUserPreferencesDto نیازی به مپینگ مستقیم به Entity نیست، چون منطق آن در سرویس یا Handler پیاده‌سازی می‌شود.
        }
    }
}
