using System.Globalization; // For CultureInfo
using System.Runtime.Serialization; // Required for [Serializable]

namespace Shared.Exceptions
{
    /// <summary>
    /// Represents the base class for exceptions thrown by the application.
    /// This custom exception serves as the root of the application's specific
    /// exception hierarchy, allowing for a clear distinction between application-defined
    /// errors and general .NET system exceptions.
    ///
    /// <para>
    /// By inheriting from <see cref="AppException"/>, custom exceptions can be
    /// easily identified and handled centrally (e.g., in global exception filters
    /// in web APIs) to produce consistent error responses or logging.
    /// </para>
    ///
    /// <para>
    /// **Purpose of <see cref="AppException"/>:**
    /// <list type="bullet">
    /// <item>Provides a common base for all expected and domain-specific errors.</item>
    /// <item>Allows for the inclusion of application-specific metadata, such as <see cref="StatusCode"/>.</item>
    /// <item>Facilitates consistent error handling and mapping in API layers or UI layers.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// **When to use <see cref="AppException"/> directly or extend it:**
    /// <list type="bullet">
    /// <item>Directly when an error occurs that doesn't fit into a more specific derived exception,
    /// but is still an application-level concern (e.g., a generic service failure).</item>
    /// <item>Extend it for specific types of application errors like
    /// <c>NotFoundException</c>, <c>ValidationException</c>, <c>UnauthorizedException</c>, etc.
    /// (as shown in the examples below).</item>
    /// </list>
    /// </para>
    /// </summary>
    [Serializable] // Ensures the exception can be serialized across AppDomains or for remoting scenarios.
    public class AppException : Exception
    {
        /// <summary>
        /// Gets an optional HTTP status code associated with the exception.
        /// This is particularly useful for web APIs to map application exceptions
        /// to appropriate HTTP response statuses (e.g., 400 Bad Request, 404 Not Found, 500 Internal Server Error).
        /// If not set, it defaults to null.
        /// </summary>
        public int? StatusCode { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppException"/> class.
        /// </summary>
        public AppException() : base() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public AppException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppException"/> class with a specified formatted error message.
        /// This constructor allows for string formatting using <see cref="CultureInfo.CurrentCulture"/>.
        /// </summary>
        /// <param name="message">The format string for the error message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public AppException(string message, params object[] args)
            : base(string.Format(CultureInfo.CurrentCulture, message, args))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppException"/> class with a specified error message
        /// and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference
        /// if no inner exception is specified.</param>
        public AppException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppException"/> class with a specified error message,
        /// an HTTP status code, and an optional reference to the inner exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="statusCode">The HTTP status code to associate with this exception (e.g., 400, 404, 500).</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference
        /// if no inner exception is specified.</param>
        public AppException(string message, int statusCode, Exception? innerException = null) : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppException"/> class with serialized data.
        /// This constructor is required for deserialization when the exception is marshaled across application domain boundaries.
        /// </summary>
        /// <param name="info">The <see cref="SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="StreamingContext"/> that contains contextual information about the source or destination.</param>
#pragma warning disable SYSLIB0051 // Type or member is obsolete
        protected AppException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051 // Type or member is obsolete

        // --- Examples of specific custom exceptions derived from AppException ---
        // These can be placed in separate files or as nested classes if they are very specific.

        /*
        /// <summary>
        /// Represents an exception that occurs when a requested resource is not found (HTTP 404).
        /// </summary>
        [Serializable]
        public class NotFoundException : AppException
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="NotFoundException"/> class with a specified error message.
            /// </summary>
            /// <param name="message">The message that describes the error.</param>
            public NotFoundException(string message) : base(message, 404) { }

            /// <summary>
            /// Initializes a new instance of the <see cref="NotFoundException"/> class for a specific entity.
            /// </summary>
            /// <param name="entityName">The name of the entity that was not found.</param>
            /// <param name="key">The identifier (key) of the entity that was not found.</param>
            public NotFoundException(string entityName, object key)
                : base($"Entity \"{entityName}\" with key ({key}) was not found.", 404) { }

            /// <summary>
            /// Initializes a new instance of the <see cref="NotFoundException"/> class with serialized data.
            /// </summary>
            protected NotFoundException(SerializationInfo info, StreamingContext context) : base(info, context) { }
        }

        /// <summary>
        /// Represents an exception that occurs due to one or more validation failures (HTTP 400 Bad Request).
        /// </summary>
        [Serializable]
        public class ValidationException : AppException
        {
            /// <summary>
            /// Gets a dictionary of validation errors, where the key is the property name
            /// and the value is an array of error messages for that property.
            /// </summary>
            public IDictionary<string, string[]> Errors { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="ValidationException"/> class
            /// with a default message indicating validation failures.
            /// </summary>
            public ValidationException() : base("One or more validation failures have occurred.", 400)
            {
                Errors = new Dictionary<string, string[]>();
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="ValidationException"/> class
            /// with a collection of specific validation errors.
            /// </summary>
            /// <param name="errors">A dictionary of validation errors.</param>
            public ValidationException(IDictionary<string, string[]> errors) : this()
            {
                Errors = errors;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="ValidationException"/> class
            /// with a general message and specific validation errors.
            /// </summary>
            /// <param name="message">A general message describing the validation failure.</param>
            /// <param name="errors">A dictionary of validation errors.</param>
            public ValidationException(string message, IDictionary<string, string[]> errors) : base(message, 400)
            {
                Errors = errors;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="ValidationException"/> class with serialized data.
            /// </summary>
            protected ValidationException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
                // Deserialize the Errors dictionary if it was part of the serialized state.
                // This would require custom serialization logic if the dictionary is complex.
                Errors = new Dictionary<string, string[]>(); // Placeholder, implement actual deserialization if needed.
            }
        }
        */
    }
}