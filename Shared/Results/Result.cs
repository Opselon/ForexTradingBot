// File: Shared/Results/Result.cs
using System;
using System.Collections.Generic;
using System.Linq; // For .ToArray() and .Any()

namespace Shared.Results
{
    /// <summary>
    /// Represents the outcome of an operation, indicating success or failure,
    /// and optionally containing data and detailed messages or errors.
    /// This generic class provides a robust and explicit way to return results
    /// from methods, avoiding exceptions for expected failure scenarios.
    /// </summary>
    /// <typeparam name="T">The type of data returned by the operation on success.</typeparam>
    public class Result<T>
    {
        /// <summary>
        /// Gets a value indicating whether the operation succeeded.
        /// </summary>
        public bool Succeeded { get; } // Made readonly for immutability

        /// <summary>
        /// Gets the data returned by the operation if it succeeded.
        /// This will be null or default(T) if the operation failed.
        /// </summary>
        public T? Data { get; } // Made readonly for immutability

        /// <summary>
        /// Gets a read-only list of errors if the operation failed.
        /// This collection will be empty if the operation succeeded.
        /// </summary>
        public IReadOnlyList<string> Errors { get; } // Using IReadOnlyList for safer exposure

        /// <summary>
        /// Gets a success message if the operation succeeded.
        /// This will be null if the operation failed or no success message was provided.
        /// </summary>
        public string? SuccessMessage { get; } // Made readonly for immutability

        /// <summary>
        /// Gets a consolidated message describing the failure, if the operation failed.
        /// This will be null if the operation succeeded.
        /// </summary>
        public string? FailureMessage { get; } // Made readonly for immutability

        /// <summary>
        /// Protected constructor to enforce creation via static factory methods.
        /// Ensures the Result object is always in a valid state.
        /// </summary>
        /// <param name="succeeded">Indicates if the operation succeeded.</param>
        /// <param name="data">The data returned by the operation on success.</param>
        /// <param name="errors">A collection of error messages on failure.</param>
        /// <param name="successMessage">A message indicating success.</param>
        /// <param name="failureMessage">A consolidated message indicating failure.</param>
        protected Result(bool succeeded, T? data, IEnumerable<string>? errors, string? successMessage, string? failureMessage)
        {
            Succeeded = succeeded;
            Data = data;
            Errors = (errors?.Any() == true) ? errors.ToList().AsReadOnly() : Array.Empty<string>(); // Optimize: Convert to ReadOnlyList once, or keep Array.Empty
            SuccessMessage = successMessage;
            FailureMessage = failureMessage;
        }

        /// <summary>
        /// Creates a successful result with data and an optional success message.
        /// </summary>
        /// <param name="data">The data to be returned.</param>
        /// <param name="message">An optional message indicating the success.</param>
        /// <returns>A new <see cref="Result{T}"/> instance representing a successful operation.</returns>
        public static Result<T> Success(T data, string? message = null)
        {
            return new Result<T>(true, data, null, message, null);
        }

        /// <summary>
        /// Creates a successful result without data, but with an optional success message.
        /// Use this when the operation itself is the primary outcome, not returned data.
        /// </summary>
        /// <param name="message">An optional message indicating the success.</param>
        /// <returns>A new <see cref="Result{T}"/> instance representing a successful operation without data.</returns>
        public static Result<T> Success(string? message = null)
        {
            return new Result<T>(true, default, null, message, null);
        }

        /// <summary>
        /// Creates a failed result with multiple error messages and an optional consolidated failure message.
        /// </summary>
        /// <param name="errors">A collection of specific error messages.</param>
        /// <param name="failureMessage">An optional consolidated message describing the failure.
        /// If not provided, the first error message or a joined string of errors will be used.</param>
        /// <returns>A new <see cref="Result{T}"/> instance representing a failed operation.</returns>
        public static Result<T> Failure(IEnumerable<string> errors, string? failureMessage = null)
        {
            var errorList = errors?.ToList() ?? new List<string>();
            var consolidatedMessage = failureMessage ?? (errorList.Any() ? errorList.First() : "Operation failed."); // Use first error if available
            return new Result<T>(false, default, errorList, null, consolidatedMessage);
        }

        /// <summary>
        /// Creates a failed result with a single error message and an optional consolidated failure message.
        /// </summary>
        /// <param name="error">A single error message.</param>
        /// <param name="failureMessage">An optional consolidated message describing the failure.
        /// If not provided, the single error message will be used.</param>
        /// <returns>A new <see cref="Result{T}"/> instance representing a failed operation.</returns>
        public static Result<T> Failure(string error, string? failureMessage = null)
        {
            return Failure(new[] { error }, failureMessage ?? error);
        }
    }

    /// <summary>
    /// Non-generic class for representing the outcome of an operation when no data is returned.
    /// This prevents the need for `Result<object>` or `Result<Unit>`.
    /// </summary>
    public class Result // This is a separate, non-generic Result class, as typically done.
    {
        /// <summary>
        /// Gets a value indicating whether the operation succeeded.
        /// </summary>
        public bool Succeeded { get; }

        /// <summary>
        /// Gets a read-only list of errors if the operation failed.
        /// This collection will be empty if the operation succeeded.
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>
        /// Gets a success message if the operation succeeded.
        /// This will be null if the operation failed or no success message was provided.
        /// </summary>
        public string? SuccessMessage { get; }

        /// <summary>
        /// Gets a consolidated message describing the failure, if the operation failed.
        /// This will be null if the operation succeeded.
        /// </summary>
        public string? FailureMessage { get; }

        /// <summary>
        /// Protected constructor to enforce creation via static factory methods.
        /// </summary>
        /// <param name="succeeded">Indicates if the operation succeeded.</param>
        /// <param name="errors">A collection of error messages on failure.</param>
        /// <param name="successMessage">A message indicating success.</param>
        /// <param name="failureMessage">A consolidated message indicating failure.</param>
        protected Result(bool succeeded, IEnumerable<string>? errors, string? successMessage, string? failureMessage)
        {
            Succeeded = succeeded;
            Errors = (errors?.Any() == true) ? errors.ToList().AsReadOnly() : Array.Empty<string>();
            SuccessMessage = successMessage;
            FailureMessage = failureMessage;
        }

        /// <summary>
        /// Creates a successful result with an optional success message.
        /// </summary>
        /// <param name="successMessage">An optional message indicating the success.</param>
        /// <returns>A new <see cref="Result"/> instance representing a successful operation.</returns>
        public static Result Success(string? successMessage = null)
        {
            return new Result(true, null, successMessage, null);
        }

        /// <summary>
        /// Creates a failed result with multiple error messages and an optional consolidated failure message.
        /// </summary>
        /// <param name="errors">A collection of specific error messages.</param>
        /// <param name="failureMessage">An optional consolidated message describing the failure.
        /// If not provided, the first error message or a joined string of errors will be used.</param>
        /// <returns>A new <see cref="Result"/> instance representing a failed operation.</returns>
        public static Result Failure(IEnumerable<string> errors, string? failureMessage = null)
        {
            var errorList = errors?.ToList() ?? new List<string>();
            var consolidatedMessage = failureMessage ?? (errorList.Any() ? errorList.First() : "Operation failed.");
            return new Result(false, errorList, null, consolidatedMessage);
        }

        /// <summary>
        /// Creates a failed result with a single error message and an optional consolidated failure message.
        /// </summary>
        /// <param name="error">A single error message.</param>
        /// <param name="failureMessage">An optional consolidated message describing the failure.
        /// If not provided, the single error message will be used.</param>
        /// <returns>A new <see cref="Result"/> instance representing a failed operation.</returns>
        public static Result Failure(string error, string? failureMessage = null)
        {
            return Failure(new[] { error }, failureMessage ?? error);
        }
    }
}