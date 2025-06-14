// File: Shared/Exceptions/RepositoryException.cs
using System.Runtime.Serialization; // Required for [Serializable]

namespace Shared.Extensions
{
    /// <summary>
    /// Represents an exception that occurs during data access operations within a repository.
    /// This custom exception serves as an abstraction layer, wrapping lower-level database
    /// exceptions (e.g., <see cref="System.Data.Common.DbException"/>, <see cref="Microsoft.Data.SqlClient.SqlException"/>)
    /// to provide a consistent and controlled way of handling data access failures across the application.
    ///
    /// <para>
    /// Using <see cref="RepositoryException"/> allows the application layer to catch a specific type of exception
    /// related to data persistence, without needing to know the exact underlying database technology
    /// or specific ORM details. It promotes cleaner error handling and adherence to the
    /// Dependency Inversion Principle.
    /// </para>
    ///
    /// <para>
    /// **When to use this exception:**
    /// <list type="bullet">
    /// <item>When a database operation (e.g., INSERT, UPDATE, DELETE, SELECT) fails unexpectedly.</item>
    /// <item>When a transient database error (e.g., network timeout, connection loss) occurs after retry policies have been exhausted.</item>
    /// <item>When a unique constraint violation or referential integrity error occurs that cannot be gracefully handled within the repository.</item>
    /// <item>To wrap an underlying <see cref="DbException"/> or specific provider exception (e.g., <see cref="SqlException"/>)
    /// as its <see cref="Exception.InnerException"/>, preserving the original error context.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// **When NOT to use this exception:**
    /// <list type="bullet">
    /// <item>For business logic validation failures (use specific validation exceptions or result patterns like <see cref="Results.Result"/>).</item>
    /// <item>For application-level configuration errors (use <see cref="InvalidOperationException"/> or custom configuration exceptions).</item>
    /// <item>For concurrency conflicts that are explicitly handled by an ORM (e.g., <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// if still using EF Core in some parts), unless you choose to abstract all concurrency as a <see cref="RepositoryException"/> with a specific message.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Serializable] // Mark as Serializable for cross-domain usage (e.g., remoting, app domains)
    public class RepositoryException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RepositoryException"/> class.
        /// </summary>
        public RepositoryException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RepositoryException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public RepositoryException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RepositoryException"/> class with a specified error message
        /// and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference
        /// (<see langword="Nothing"/> in Visual Basic) if no inner exception is specified.</param>
        public RepositoryException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RepositoryException"/> class with serialized data.
        /// </summary>
        /// <param name="info">The <see cref="SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="StreamingContext"/> that contains contextual information about the source or destination.</param>
#pragma warning disable SYSLIB0051 // Type or member is obsolete
        protected RepositoryException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051 // Type or member is obsolete
    }
}