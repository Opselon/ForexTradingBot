using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shared.Helpers
{
    public static class SqlServiceManager
    {
        #region PostgreSQL Service Names
        // Common PostgreSQL service names for Windows and Linux
        private static readonly string[] WindowsPostgresServices = new[]
        {
            "postgresql-x64-16", // Default for PostgreSQL 16 on Windows
            "postgresql-x64-15", // Default for PostgreSQL 15 on Windows
            "postgresql-x64-14", // Default for PostgreSQL 14 on Windows
            "postgresql-x64-13", // Default for PostgreSQL 13 on Windows
            "postgresql-x64-12", // Default for PostgreSQL 12 on Windows
            "postgresql" // Sometimes just 'postgresql'
        };
        private static readonly string[] LinuxPostgresServices = new[]
        {
            "postgresql", // Most common
            "postgres" // Sometimes just 'postgres'
        };
        #endregion

        #region SQL Server Service Names
        private static readonly string[] SqlServerServices = new[]
        {
            "MsDtsServer160", "MSSQLFDLauncher", "MSSQLSERVER", "MSSQLServerOLAPService", "SQLBrowser", "SQLSERVERAGENT", "SQLTELEMETRY", "SQLWriter", "SSASTELEMETRY", "SSISTELEMETRY160"
        };
        #endregion

        #region Public API
        public static void EnsurePostgresServiceRunning()
        {
            string? foundService = FindPostgresService();
            if (foundService == null)
            {
                Console.WriteLine("[ERROR] No PostgreSQL service found on this system.");
                return;
            }

            string state = GetServiceState(foundService, ServiceType.Postgres);
            switch (state)
            {
                case "RUNNING":
                    Console.WriteLine($"[OK] PostgreSQL service '{foundService}' is already running.");
                    break;
                case "STOPPED":
                    Console.WriteLine($"[INFO] PostgreSQL service '{foundService}' is stopped. Attempting to start...");
                    StartService(foundService, ServiceType.Postgres);
                    break;
                case "START_PENDING":
                    Console.WriteLine($"[WAIT] PostgreSQL service '{foundService}' is currently starting...");
                    break;
                case "NOT_FOUND":
                    Console.WriteLine($"[ERROR] PostgreSQL service '{foundService}' not found.");
                    break;
                default:
                    Console.WriteLine($"[WARN] PostgreSQL service '{foundService}' unknown state: {state}");
                    break;
            }
        }

        public static void EnsureSqlServerServicesRunning()
        {
            var servicesToStart = new List<string>();
            foreach (var service in SqlServerServices)
            {
                try
                {
                    var state = GetServiceState(service, ServiceType.SqlServer);
                    switch (state)
                    {
                        case "RUNNING":
                            Console.WriteLine($"[OK] SQL Server service '{service}' is already running.");
                            break;
                        case "STOPPED":
                            Console.WriteLine($"[INFO] SQL Server service '{service}' is stopped. Will start later...");
                            servicesToStart.Add(service);
                            break;
                        case "START_PENDING":
                            Console.WriteLine($"[WAIT] SQL Server service '{service}' is currently starting...");
                            break;
                        case "NOT_FOUNDD":
                            Console.WriteLine($"[SKIP] SQL Server service '{service}' not found.");
                            break;
                        default:
                            Console.WriteLine($"[WARN] SQL Server service '{service}' unknown state: {state}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {service}: {ex.Message}");
                }
            }
            if (servicesToStart.Count > 0)
            {
                StartAllSqlServerServicesAtOnce(servicesToStart.ToArray());
            }
        }
        #endregion

        #region Service Detection
        private static string? FindPostgresService()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var service in WindowsPostgresServices)
                {
                    if (GetServiceState(service, ServiceType.Postgres) != "NOT_FOUND")
                        return service;
                }
            }
            else // Linux/macOS
            {
                foreach (var service in LinuxPostgresServices)
                {
                    if (GetServiceState(service, ServiceType.Postgres) != "NOT_FOUND")
                        return service;
                }
            }
            return null;
        }
        #endregion

        #region Service State
        private enum ServiceType { Postgres, SqlServer }
        private static string GetServiceState(string serviceName, ServiceType type)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "sc",
                            Arguments = $"query \"{serviceName}\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (output.Contains("RUNNING"))
                        return "RUNNING";
                    else if (output.Contains("STOPPED"))
                        return "STOPPED";
                    else if (output.Contains("START_PENDING"))
                        return "START_PENDING";
                    else if (output.Contains("STOP_PENDING"))
                        return "STOP_PENDING";
                    else
                        return "UNKNOWN";
                }
                else // Linux/macOS
                {
                    if (type == ServiceType.Postgres)
                    {
                        var process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "systemctl",
                                Arguments = $"is-active {serviceName}",
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        process.Start();
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit();
                        if (output == "active")
                            return "RUNNING";
                        else if (output == "inactive" || output == "failed")
                            return "STOPPED";
                        else if (output == "activating")
                            return "START_PENDING";
                        else if (output == "deactivating")
                            return "STOP_PENDING";
                        else if (output == "unknown" || output == "")
                            return "NOT_FOUND";
                        else
                            return output.ToUpperInvariant();
                    }
                    else // SQL Server on Linux is rare, but for completeness
                    {
                        // Not implemented: SQL Server on Linux service detection
                        return "NOT_FOUND";
                    }
                }
            }
            catch
            {
                return "NOT_FOUND";
            }
        }
        #endregion

        #region Start Service
        private static void StartService(string serviceName, ServiceType type)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "net",
                            Arguments = $"start \"{serviceName}\"",
                            Verb = "runas",
                            UseShellExecute = true,
                            CreateNoWindow = false
                        }
                    };
                    process.Start();
                    process.WaitForExit();
                }
                else if (type == ServiceType.Postgres)
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "systemctl",
                            Arguments = $"start {serviceName}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    process.WaitForExit();
                }
                Console.WriteLine($"[SUCCESS] {type} service '{serviceName}' started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAILED] Could not start {type} service '{serviceName}': {ex.Message}");
            }
        }

        private static void StartAllSqlServerServicesAtOnce(string[] services)
        {
            try
            {
                string allStarts = string.Join(" && ", services.Select(s => $"net start \"{s}\""));
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {allStarts}",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = false
                    }
                };
                process.Start();
                process.WaitForExit();
                Console.WriteLine("[SUCCESS] All requested SQL Server services have been started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAILED] Could not start SQL Server services: {ex.Message}");
            }
        }
        #endregion
    }
}
