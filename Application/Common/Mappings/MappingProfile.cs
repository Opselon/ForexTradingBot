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
            _ = CreateMap<NewsItem, NewsItemDto>()
                .ForMember(dest => dest.SourceName, opt => opt.MapFrom(src => src.RssSource.SourceName)) // مپ کردن نام از RssSource مرتبط
                .ForMember(dest => dest.CreatedAtInSystem, opt => opt.MapFrom(src => src.CreatedAt));
            //  مپینگ برای CreateNewsItemDto به NewsItem (اگر DTO برای ایجاد دارید)
            // CreateMap<CreateNewsItemDto, NewsItem>();
            #endregion

            // User Mappings
            _ = CreateMap<User, UserDto>()
                .ForMember(dest => dest.Level, opt => opt.MapFrom(src => src.Level.ToString()))
                .ForMember(dest => dest.ActiveSubscription, opt => opt.Ignore()); // این باید دستی مپ شود یا از طریق یک resolver
            _ = CreateMap<RegisterUserDto, User>();
            CreateMap<UpdateUserDto, User>()
                .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null)); // فقط فیلدهای غیر null را مپ کن

            // TokenWallet Mappings
            _ = CreateMap<TokenWallet, TokenWalletDto>();

            // Subscription Mappings
            _ = CreateMap<Subscription, SubscriptionDto>()
                .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.IsCurrentlyActive)); // پراپرتی محاسباتی
            _ = CreateMap<CreateSubscriptionDto, Subscription>();

            // SignalCategory Mappings
            _ = CreateMap<SignalCategory, SignalCategoryDto>();
            _ = CreateMap<CreateSignalCategoryDto, SignalCategory>();

            // Signal Mappings
            _ = CreateMap<Signal, SignalDto>()
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.Type.ToString()))
                .ForMember(dest => dest.Category, opt => opt.MapFrom(src => src.Category)); // مپ کردن نویگیشن پراپرتی
            _ = CreateMap<CreateSignalDto, Signal>();

            // SignalAnalysis Mappings
            _ = CreateMap<SignalAnalysis, SignalAnalysisDto>();
            _ = CreateMap<CreateSignalAnalysisDto, SignalAnalysis>();

            // RssSource Mappings
            _ = CreateMap<RssSource, RssSourceDto>()
                .ForMember(dest => dest.DefaultSignalCategoryName,
                           opt => opt.MapFrom(src => src.DefaultSignalCategory != null ? src.DefaultSignalCategory.Name : null));
            _ = CreateMap<CreateRssSourceDto, RssSource>();

            // Transaction Mappings
            _ = CreateMap<Transaction, TransactionDto>()
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.Type.ToString()));
            _ = CreateMap<CreateTransactionDto, Transaction>();

            // UserSignalPreference Mappings
            _ = CreateMap<UserSignalPreference, UserSignalPreferenceDto>()
                .ForMember(dest => dest.CategoryId, opt => opt.MapFrom(src => src.CategoryId))
                .ForMember(dest => dest.CategoryName, opt => opt.MapFrom(src => src.Category.Name)) // نیاز به Include(usp => usp.Category) در Repository
                .ForMember(dest => dest.SubscribedAt, opt => opt.MapFrom(src => src.CreatedAt));
            // برای SetUserPreferencesDto نیازی به مپینگ مستقیم به Entity نیست، چون منطق آن در سرویس یا Handler پیاده‌سازی می‌شود.
        }
    }
}