namespace Domain.ValueObjects
{
    /// <summary>
    /// یک Value Object برای نمایش مقدار یا تعداد توکن‌ها در سیستم.
    /// این ساختار تغییرناپذیر (immutable) است و برابری آن بر اساس مقدار عددی توکن‌ها تعریف می‌شود.
    /// همچنین شامل منطق برای عملیات حسابی پایه و کنترل دقت مقادیر توکن است.
    /// </summary>
    public readonly struct TokenAmount : IEquatable<TokenAmount>
    {
        /// <summary>
        /// مقدار عددی توکن‌ها.
        /// این مقدار با دقت چهار رقم اعشار و با گرد کردن به دور از صفر ذخیره می‌شود.
        /// </summary>
        public decimal Value { get; }

        /// <summary>
        /// سازنده‌ای برای ایجاد یک نمونه جدید از <see cref="TokenAmount"/>.
        /// </summary>
        /// <param name="value">مقدار اولیه توکن. این مقدار نباید منفی باشد.</param>
        /// <exception cref="ArgumentException">اگر مقدار ورودی منفی باشد.</exception>
        public TokenAmount(decimal value)
        {
            if (value < 0)
            {
                throw new ArgumentException("مقدار توکن نمی‌تواند منفی باشد.", nameof(value));
            }

            // گرد کردن مقدار به چهار رقم اعشار، با روش AwayFromZero برای سازگاری با محاسبات مالی.
            // MidpointRounding.AwayFromZero: 2.5 -> 3, -2.5 -> -3
            Value = decimal.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// مقدار توکن دیگری را به مقدار فعلی اضافه می‌کند و یک <see cref="TokenAmount"/> جدید برمی‌گرداند.
        /// </summary>
        /// <param name="other">مقدار توکنی که باید اضافه شود.</param>
        /// <returns>یک <see cref="TokenAmount"/> جدید که حاصل جمع دو مقدار است.</returns>
        public TokenAmount Add(TokenAmount other)
        {
            return new TokenAmount(Value + other.Value);
        }

        /// <summary>
        /// مقدار توکن دیگری را از مقدار فعلی کم می‌کند و یک <see cref="TokenAmount"/> جدید برمی‌گرداند.
        /// </summary>
        /// <param name="other">مقدار توکنی که باید کم شود.</param>
        /// <returns>یک <see cref="TokenAmount"/> جدید که حاصل تفریق دو مقدار است.</returns>
        /// <exception cref="InvalidOperationException">اگر مقدار فعلی توکن‌ها برای کسر کردن کافی نباشد.</exception>
        public TokenAmount Subtract(TokenAmount other)
        {
            return Value < other.Value
                ? throw new InvalidOperationException("موجودی توکن برای انجام این عملیات کافی نیست.")
                : new TokenAmount(Value - other.Value);
        }

        /// <summary>
        /// بررسی می‌کند که آیا نمونه فعلی با یک <see cref="TokenAmount"/> دیگر برابر است یا خیر.
        /// برابری بر اساس مقدار عددی <see cref="Value"/> تعیین می‌شود.
        /// </summary>
        /// <param name="other">نمونه <see cref="TokenAmount"/> دیگر برای مقایسه.</param>
        /// <returns><c>true</c> اگر مقادیر برابر باشند؛ در غیر این صورت <c>false</c>.</returns>
        public bool Equals(TokenAmount other)
        {
            return Value == other.Value;
        }

        /// <summary>
        /// بررسی می‌کند که آیا نمونه فعلی با یک شیء دیگر برابر است یا خیر.
        /// </summary>
        /// <param name="obj">شیء دیگر برای مقایسه.</param>
        /// <returns><c>true</c> اگر شیء یک <see cref="TokenAmount"/> با مقدار برابر باشد؛ در غیر این صورت <c>false</c>.</returns>
        public override bool Equals(object? obj)
        {
            return obj is TokenAmount other && Equals(other);
        }

        /// <summary>
        /// کد هش برای نمونه فعلی را برمی‌گرداند.
        /// بر اساس مقدار <see cref="Value"/> محاسبه می‌شود.
        /// </summary>
        /// <returns>کد هش برای این نمونه.</returns>
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        /// <summary>
        /// نمایش رشته‌ای از <see cref="TokenAmount"/> را برمی‌گرداند.
        /// مقدار را با چهار رقم اعشار و پسوند " Tokens" نمایش می‌دهد.
        /// </summary>
        /// <returns>نمایش رشته‌ای از مقدار توکن.</returns>
        public override string ToString()
        {
            return $"{Value:N4} Tokens"; // N4 فرمت عددی با چهار رقم اعشار
        }

        /// <summary>
        /// یک نمونه <see cref="TokenAmount"/> با مقدار صفر را برمی‌گرداند.
        /// </summary>
        public static TokenAmount Zero => new TokenAmount(0);

        /// <summary>
        /// اپراتور برابری برای مقایسه دو نمونه <see cref="TokenAmount"/>.
        /// </summary>
        public static bool operator ==(TokenAmount left, TokenAmount right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// اپراتور نابرابری برای مقایسه دو نمونه <see cref="TokenAmount"/>.
        /// </summary>
        public static bool operator !=(TokenAmount left, TokenAmount right)
        {
            return !(left == right);
        }

        /// <summary>
        /// اپراتور جمع برای دو نمونه <see cref="TokenAmount"/>.
        /// </summary>
        public static TokenAmount operator +(TokenAmount a, TokenAmount b)
        {
            return a.Add(b);
        }

        /// <summary>
        /// اپراتور تفریق برای دو نمونه <see cref="TokenAmount"/>.
        /// </summary>
        public static TokenAmount operator -(TokenAmount a, TokenAmount b)
        {
            return a.Subtract(b);
        }

        // اپراتورهای مقایسه‌ای دیگر نیز می‌توانند مفید باشند:
        // public static bool operator <(TokenAmount left, TokenAmount right) => left.Value < right.Value;
        // public static bool operator >(TokenAmount left, TokenAmount right) => left.Value > right.Value;
        // public static bool operator <=(TokenAmount left, TokenAmount right) => left.Value <= right.Value;
        // public static bool operator >=(TokenAmount left, TokenAmount right) => left.Value >= right.Value;

        // تبدیل ضمنی یا صریح به/از decimal نیز می‌تواند در نظر گرفته شود، اما با احتیاط:
        // public static explicit operator decimal(TokenAmount amount) => amount.Value;
        // public static explicit operator TokenAmount(decimal value) => new TokenAmount(value);
    }
}