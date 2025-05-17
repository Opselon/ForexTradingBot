namespace Shared.Results
{
    /// <summary>
    /// کلاس عمومی برای نمایش نتیجه یک عملیات.
    /// می‌تواند موفقیت‌آمیز یا ناموفق باشد و شامل پیام‌ها یا داده‌های مرتبط باشد.
    /// </summary>
    public class Result
    {
        public bool Succeeded { get; protected set; }
        public IEnumerable<string> Errors { get; protected set; } = Enumerable.Empty<string>();
        public string? SuccessMessage { get; protected set; }

        protected Result(bool succeeded, IEnumerable<string>? errors = null, string? successMessage = null)
        {
            Succeeded = succeeded;
            if (errors != null)
            {
                Errors = errors.ToArray();
            }
            SuccessMessage = successMessage;
        }

        public static Result Success(string? successMessage = null)
        {
            return new Result(true, null, successMessage);
        }

        public static Result Failure(IEnumerable<string> errors)
        {
            return new Result(false, errors);
        }

        public static Result Failure(string error)
        {
            return new Result(false, new[] { error });
        }
    }

    /// <summary>
    /// کلاس عمومی برای نمایش نتیجه یک عملیات همراه با داده بازگشتی.
    /// </summary>
    /// <typeparam name="T">نوع داده بازگشتی.</typeparam>
    public class Result<T> : Result
    {
        public T? Data { get; private set; }

        protected Result(T? data, bool succeeded, IEnumerable<string>? errors = null, string? successMessage = null)
            : base(succeeded, errors, successMessage)
        {
            Data = data;
        }

        public static Result<T> Success(T data, string? successMessage = null)
        {
            return new Result<T>(data, true, null, successMessage);
        }

        // متد Failure برای Result<T> از Result پایه ارث‌بری می‌شود
        // اما می‌توانیم برای راحتی بیشتر، متدهای Failure خاصی هم در اینجا اضافه کنیم
        // که Data را null یا مقدار پیش‌فرض T قرار دهند.

        public new static Result<T> Failure(IEnumerable<string> errors)
        {
            return new Result<T>(default(T), false, errors);
        }

        public new static Result<T> Failure(string error)
        {
            return new Result<T>(default(T), false, new[] { error });
        }
    }
}