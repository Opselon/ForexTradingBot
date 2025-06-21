// File: TelegramPanel/Infrastructure/Helper/CountryHelper.cs
namespace TelegramPanel.Infrastructure.Helper
{
    public static class CountryHelper
    {
        public static readonly List<(string Name, string Code)> AllCountries =
        [
            ("United States", "US"), ("Germany", "DE"), ("United Kingdom", "GB"),
            ("France", "FR"), ("Japan", "JP"), ("Canada", "CA"), ("Australia", "AU"),
            ("Brazil", "BR"), ("India", "IN"), ("China", "CN"), ("Russia", "RU"),
            ("South Korea", "KR"), ("Italy", "IT"), ("Spain", "ES"), ("Mexico", "MX"),
            ("Indonesia", "ID"), ("Netherlands", "NL"), ("Saudi Arabia", "SA"),
            ("Turkey", "TR"), ("Switzerland", "CH"), ("Sweden", "SE"), ("Poland", "PL"),
            ("Belgium", "BE"), ("Argentina", "AR"), ("Norway", "NO"), ("Austria", "AT"),
            ("United Arab Emirates", "AE"), ("Israel", "IL"), ("Singapore", "SG"),
            ("South Africa", "ZA"), ("Ireland", "IE"), ("Denmark", "DK"),
            ("Malaysia", "MY"), ("Hong Kong", "HK"), ("New Zealand", "NZ"),
            ("Finland", "FI"), ("Portugal", "PT"), ("Greece", "GR"), ("Chile", "CL")
            // Add more countries as needed
        ];
    }
}