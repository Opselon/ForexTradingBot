using System;
using System.Globalization;

namespace Shared.Exceptions
{
    /// <summary>
    /// کلاس پایه برای Exception های سفارشی برنامه.
    /// این کلاس به شما اجازه می‌دهد تا Exception های خاص برنامه را از Exception های سیستمی تمایز دهید.
    /// </summary>
    public class AppException : Exception
    {
        public int? StatusCode { get; } // برای نگهداری کد وضعیت HTTP در صورت نیاز

        public AppException() : base() { }

        public AppException(string message) : base(message) { }

        public AppException(string message, params object[] args)
            : base(String.Format(CultureInfo.CurrentCulture, message, args))
        {
        }

        public AppException(string message, Exception innerException) : base(message, innerException) { }

        public AppException(string message, int statusCode, Exception? innerException = null) : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        // می‌توانید انواع دیگری از Exception های سفارشی را از این کلاس ارث‌بری کنید:
        // public class NotFoundException : AppException
        // {
        //     public NotFoundException(string message) : base(message, 404) { }
        //     public NotFoundException(string entityName, object key)
        //         : base($"Entity \"{entityName}\" ({key}) was not found.", 404) { }
        // }

        // public class ValidationException : AppException
        // {
        //     public IDictionary<string, string[]> Errors { get; }
        //     public ValidationException() : base("One or more validation failures have occurred.", 400)
        //     {
        //         Errors = new Dictionary<string, string[]>();
        //     }
        //     public ValidationException(IDictionary<string, string[]> errors) : this()
        //     {
        //         Errors = errors;
        //     }
        // }
    }
}