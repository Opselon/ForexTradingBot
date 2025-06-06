﻿using System.Diagnostics;

namespace Shared.Helpers
{
    public static class SqlServiceManager
    {
        private static readonly string[] SqlServices = new[]
       {
            "MsDtsServer160",          // SQL Server Integration Services 16.0
            "MSSQLFDLauncher",         // SQL Full-text Filter Daemon Launcher
            "MSSQLSERVER",             // SQL Server (Default Instance)
            "MSSQLServerOLAPService",  // SQL Server Analysis Services
            "SQLBrowser",              // SQL Server Browser
            "SQLSERVERAGENT",          // SQL Server Agent
            "SQLTELEMETRY",            // SQL Server CEIP (Telemetry)
            "SQLWriter",               // SQL Server VSS Writer
            "SSASTELEMETRY",           // SSAS Telemetry
            "SSISTELEMETRY160"         // SSIS Telemetry
        };

        public static void EnsureSqlServicesRunning()
        {
            var servicesToStart = new List<string>();

            foreach (var service in SqlServices)
            {
                try
                {
                    var state = GetServiceState(service);

                    switch (state)
                    {
                        case "RUNNING":
                            Console.WriteLine($"[OK] {service} is already running.");
                            break;

                        case "STOPPED":
                            Console.WriteLine($"[INFO] {service} is stopped. Will start later...");
                            servicesToStart.Add(service);
                            break;

                        case "START_PENDING":
                            Console.WriteLine($"[WAIT] {service} is currently starting...");
                            break;

                        case "NOT_FOUND":
                            Console.WriteLine($"[SKIP] {service} not found.");
                            break;

                        default:
                            Console.WriteLine($"[WARN] {service} unknown state: {state}");
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
                StartAllServicesAtOnce(servicesToStart.ToArray());
            }
        }


        private static string GetServiceState(string serviceName)
        {
            try
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
            catch
            {
                return "NOT_FOUND";
            }
        }


        public static void StartAllServicesAtOnce(string[] services)
        {
            try
            {
                // ساخت دستور start همه سرویس‌ها به صورت یک خط با &&
                string allStarts = string.Join(" && ", services.Select(s => $"net start \"{s}\""));

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {allStarts}",
                        Verb = "runas", // یک بار درخواست ادمین میاد
                        UseShellExecute = true,
                        CreateNoWindow = false // اگر بخوای پنجره cmd باز بمونه برای نمایش خروجی بزار true، اگر نه false
                    }
                };

                process.Start();
                process.WaitForExit();

                Console.WriteLine("[SUCCESS] All requested services have been started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAILED] Could not start services: {ex.Message}");
            }
        }
    }
}
