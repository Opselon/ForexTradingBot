// --- File: Infrastructure/Logging/CustomCallerEnricher.cs ---

using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;
using System.Reflection;

namespace Infrastructure.Logging
{
    /// <summary>
    /// A powerful, custom Serilog enricher that accurately finds the true calling method
    /// of a log event, intelligently skipping specified framework namespaces.
    /// This is the "God Mode" version of a caller enricher.
    /// </summary>
    public class CustomCallerEnricher : ILogEventEnricher
    {
        private readonly string[] _excludedNamespaces;

        /// <summary>
        /// Initializes a new instance of the <see cref="CustomCallerEnricher"/> class.
        /// </summary>
        /// <param name="excludedNamespaces">An array of namespace prefixes to ignore when searching the call stack.</param>
        public CustomCallerEnricher(params string[] excludedNamespaces)
        {
            _excludedNamespaces = excludedNamespaces ?? Array.Empty<string>();
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var (method, filePath, lineNumber) = FindCallingMethod();

            if (method != null)
            {
                var callerInfo = $"{method.DeclaringType.FullName}.{method.Name}() in {filePath}:line {lineNumber}";
                var property = propertyFactory.CreateProperty("Caller", callerInfo);
                logEvent.AddPropertyIfAbsent(property);
            }
        }

        private (MethodBase method, string filePath, int lineNumber) FindCallingMethod()
        {
            var stack = new StackTrace(true);

            foreach (var frame in stack.GetFrames())
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType == null)
                {
                    continue;
                }

                var ns = method.DeclaringType.Namespace ?? "";

                // This is the core logic: if the namespace starts with any of our
                // excluded prefixes, we skip this frame and check the next one.
                if (_excludedNamespaces.Any(excluded => ns.StartsWith(excluded)))
                {
                    continue;
                }

                // If we get here, we've found the first method that is NOT in an excluded namespace.
                // This is our true caller.
                var lineNumber = frame.GetFileLineNumber();
                var filePath = frame.GetFileName();

                // Return if we have useful info.
                if (lineNumber > 0)
                {
                    return (method, filePath, lineNumber);
                }
            }

            return (null, null, 0);
        }
    }
}