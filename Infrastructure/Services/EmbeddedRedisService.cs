// In Infrastructure/Services/EmbeddedRedisService.cs

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis; // We need this to ping the server
using StackExchange.Redis.Maintenance;
using StackExchange.Redis.Profiling;
using System.Diagnostics;
using System.Net;

namespace Infrastructure.Services
{
    // --- STEP 1: Add a small settings class for configurability ---
    public class EmbeddedRedisSettings
    {
        public const string SectionName = "EmbeddedRedis";
        public string ServerExecutablePath { get; set; } = "redis-server/redis-server.exe";
        public string ConnectionString { get; set; } = "localhost:6379";
        public int StartupPingTimeoutSeconds { get; set; } = 5;
    }

    /// <summary>
    /// An IHostedService that manages the lifecycle of an embedded, portable Redis server.
    /// It starts the redis-server.exe process, actively verifies its responsiveness,
    /// and ensures it's terminated when the application shuts down.
    /// </summary>
    public class EmbeddedRedisService : IHostedService
    {
        private readonly ILogger<EmbeddedRedisService> _logger;
        private readonly EmbeddedRedisSettings _settings;
        private Process? _redisProcess;
        private readonly string _redisServerFullPath;

        public EmbeddedRedisService(ILogger<EmbeddedRedisService> logger, IOptions<EmbeddedRedisSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;

            // Try multiple possible paths for the Redis server executable
            List<string> possiblePaths = new()
            {
                // Path relative to WebAPI project (most likely)
                Path.Combine(AppContext.BaseDirectory, "..", "WebAPI", _settings.ServerExecutablePath),
                // Path relative to current directory
                Path.Combine(AppContext.BaseDirectory, _settings.ServerExecutablePath),
                // Absolute path from WebAPI directory
                Path.Combine(Directory.GetCurrentDirectory(), "WebAPI", _settings.ServerExecutablePath),
                // Fallback to just the executable name (if it's in PATH)
                _settings.ServerExecutablePath
            };

            // The bundled binaries are Windows .exe files. Launching them on Linux/macOS
            // fails with a Permission/format error, so on those platforms prefer a
            // redis-server found in PATH before falling back to the bundled name.
            if (!OperatingSystem.IsWindows())
            {
                possiblePaths.InsertRange(0, new[] { "redis-server", "/usr/bin/redis-server", "/usr/local/bin/redis-server" });
            }

            _redisServerFullPath = possiblePaths.FirstOrDefault(File.Exists) ?? possiblePaths[0];

            _logger.LogInformation("Redis server executable path resolved to: {Path}", _redisServerFullPath);
        }

        // --- REFACTORED StartAsync METHOD ---
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ConsoleUI.WriteHeader("Embedded Redis Service");

            // Step 1: Validate the executable path. If not found, log and exit.
            if (!File.Exists(_redisServerFullPath))
            {
                HandleExecutableNotFound();
                return;
            }

            // Step 2: Check if another process is on the port. If yes, handle it and exit.
            if (IsPortInUse(6379))
            {
                if (await HandleExistingProcess())
                {
                    // HandleExistingProcess returns 'true' if we successfully connected
                    // to an existing server, so we can exit.
                    return;
                }
                // If it returns 'false', it's a port conflict, so we also exit.
                return;
            }

            // Step 3: If the path is valid and the port is free, launch the process.
            await LaunchAndVerifyRedisProcess(cancellationToken);
        }

        // =========================================================================
        // ✅ ADD THESE THREE NEW PRIVATE HELPER METHODS TO YOUR CLASS
        //    (They contain your original, unchanged logic)
        // =========================================================================

        /// <summary>
        /// Contains the original logic for logging an error when the Redis executable is not found.
        /// </summary>
        private void HandleExecutableNotFound()
        {
            ConsoleUI.WriteError($"Embedded Redis server not found at '{_redisServerFullPath}'.");
            _logger.LogError("Embedded Redis server executable not found. The server will not be started.");

            // List all possible paths for debugging
            string[] possiblePaths = new[]
            {
        Path.Combine(AppContext.BaseDirectory, "..", "WebAPI", _settings.ServerExecutablePath),
        Path.Combine(AppContext.BaseDirectory, _settings.ServerExecutablePath),
        Path.Combine(Directory.GetCurrentDirectory(), "WebAPI", _settings.ServerExecutablePath),
        _settings.ServerExecutablePath
    };

            _logger.LogError("Tried these paths:");
            foreach (string? path in possiblePaths)
            {
                _logger.LogError("  - {Path} (Exists: {Exists})", path, File.Exists(path));
            }
        }

        /// <summary>
        /// Contains the original logic for handling the case where port 6379 is already in use.
        /// </summary>
        /// <returns>True if connected to an existing responsive server, false otherwise.</returns>
        private async Task<bool> HandleExistingProcess()
        {
            ConsoleUI.WriteWarning("Port 6379 is already in use. Redis server might already be running.");
            _logger.LogWarning("Port 6379 is already in use. Attempting to connect to existing Redis server.");

            if (await IsRedisResponsiveAsync())
            {
                ConsoleUI.WriteSuccess("Connected to existing Redis server on port 6379.");
                return true; // Success, we are done.
            }
            else
            {
                ConsoleUI.WriteError("Port 6379 is in use but Redis is not responding. Please check what's using the port.");
                _logger.LogError("Port 6379 is in use but Redis is not responding. This might be a port conflict.");
                return false; // Failure, but we are also done.
            }
        }

        /// <summary>
        /// Contains the original logic for starting the Redis process and verifying it is responsive.
        /// </summary>
        private async Task LaunchAndVerifyRedisProcess(CancellationToken cancellationToken)
        {
            ConsoleUI.WriteInfo($"Attempting to start embedded Redis server from: {_redisServerFullPath}");
            _logger.LogInformation("Starting embedded Redis server from: {Path}", _redisServerFullPath);

            ProcessStartInfo processStartInfo = new()
            {
                FileName = _redisServerFullPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_redisServerFullPath) // Set working directory
            };

            try
            {
                _redisProcess = new Process
                {
                    StartInfo = processStartInfo,
                    EnableRaisingEvents = true
                };

                TaskCompletionSource<bool> serverReadyTcs = new();
                List<string> outputLines = [];

                _redisProcess.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputLines.Add(args.Data);
                        _logger.LogDebug("Redis output: {Output}", args.Data);
                        if (args.Data.Contains("Ready to accept connections"))
                        {
                            _ = serverReadyTcs.TrySetResult(true);
                        }
                    }
                };

                _redisProcess.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        _logger.LogWarning("Redis Process stderr: {RedisError}", args.Data);
                        outputLines.Add($"ERROR: {args.Data}");
                    }
                };

                _ = _redisProcess.Start();
                _redisProcess.BeginOutputReadLine();
                _redisProcess.BeginErrorReadLine();

                ConsoleUI.WriteInfo($"Redis process started with ID {_redisProcess.Id}. Waiting for it to become responsive...");

                Task completedTask = await Task.WhenAny(serverReadyTcs.Task, Task.Delay(TimeSpan.FromSeconds(_settings.StartupPingTimeoutSeconds), cancellationToken));

                if (completedTask == serverReadyTcs.Task && serverReadyTcs.Task.Result)
                {
                    ConsoleUI.WriteSuccess("Redis server is ready to accept connections.");
                    _logger.LogInformation("Redis server started successfully. Process ID: {ProcessId}", _redisProcess.Id);
                }
                else
                {
                    if (await IsRedisResponsiveAsync())
                    {
                        ConsoleUI.WriteSuccess("Redis server is responding to pings.");
                        _logger.LogInformation("Redis server is responding to pings. Process ID: {ProcessId}", _redisProcess.Id);
                    }
                    else
                    {
                        ConsoleUI.WriteError("Redis process started but did not become responsive in time.");
                        _logger.LogError("Redis process started but did not become responsive. Process ID: {ProcessId}", _redisProcess.Id);
                        _logger.LogError("Redis output: {Output}", string.Join(Environment.NewLine, outputLines));

                        if (_redisProcess.HasExited)
                        {
                            _logger.LogError("Redis process has exited with code: {ExitCode}", _redisProcess.ExitCode);
                        }
                        else
                        {
                            _redisProcess.Kill();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleUI.WriteError("A critical error occurred while starting the embedded Redis server.");
                _logger.LogCritical(ex, "Embedded Redis startup failed. Please ensure it's not blocked by antivirus.");
            }
        }
        private bool IsPortInUse(int port)
        {
            try
            {
                using System.Net.Sockets.TcpClient client = new();
                client.Connect("localhost", port);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> IsRedisResponsiveAsync()
        {
            try
            {
                ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(_settings.ConnectionString + ",connectTimeout=1000");
                if (connection.IsConnected)
                {
                    _ = await connection.GetDatabase().PingAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Ping to embedded Redis failed (this can be normal during startup).");
            }
            return false;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_redisProcess != null && !_redisProcess.HasExited)
            {
                ConsoleUI.WriteHeader("Shutting Down Embedded Redis");
                _logger.LogInformation("Stopping embedded Redis server (Process ID: {ProcessId})...", _redisProcess.Id);
                try
                {
                    if (!_redisProcess.CloseMainWindow())
                    {
                        _redisProcess.Kill();
                        _logger.LogWarning("Redis process did not shut down gracefully and was killed.");
                    }
                    ConsoleUI.WriteSuccess("Embedded Redis server stopped.");
                }
                catch (Exception ex)
                {
                    ConsoleUI.WriteError("An error occurred while stopping the embedded Redis server.");
                    _logger.LogError(ex, "Failure during embedded Redis shutdown.");
                }
                finally
                {
                    _redisProcess.Dispose();
                }
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fallback in-memory Redis service for when the real Redis server is not available.
    /// This provides basic caching functionality using in-memory storage.
    /// </summary>
    public class FallbackRedisService : IConnectionMultiplexer
    {
        private readonly ILogger<FallbackRedisService> _logger;
        private readonly Dictionary<string, object> _cache = [];
        private readonly object _lock = new();

        public FallbackRedisService(ILogger<FallbackRedisService> logger)
        {
            _logger = logger;
            _logger.LogWarning("Using fallback in-memory Redis service. Distributed features will be limited.");
        }

        public IDatabase GetDatabase(int db = -1, object? asyncState = null)
        {
            return new FallbackDatabase(_cache, _lock, _logger);
        }

        public IServer GetServer(string host, int port, object? asyncState = null)
        {
            throw new NotImplementedException("Server operations not supported in fallback mode");
        }

        public IServer GetServer(string hostAndPort, object? asyncState = null)
        {
            throw new NotImplementedException("Server operations not supported in fallback mode");
        }

        public IServer GetServer(IPAddress host, int port)
        {
            throw new NotImplementedException("Server operations not supported in fallback mode");
        }

        public IServer GetServer(EndPoint endpoint, object? asyncState = null)
        {
            throw new NotImplementedException("Server operations not supported in fallback mode");
        }

        public Task<IServer[]> GetServerListAsync(object? asyncState = null)
        {
            return Task.FromResult(Array.Empty<IServer>());
        }

        public void RegisterProfiler(Func<ProfilingSession?> profilingSessionProvider)
        {
            // No-op in fallback mode
        }


        public void ExportConfiguration(Stream destination, ExportOptions options = ExportOptions.All)
        {
            // No-op in fallback mode
        }

        public string GetStatus()
        {
            return "Fallback Mode";
        }

        public void GetStatus(TextWriter log)
        {
            log.WriteLine("Fallback Mode");
        }

        public void Close(bool allowCommandsToComplete = true)
        {
            // No-op in fallback mode
        }

        public async Task CloseAsync(bool allowCommandsToComplete = true)
        {
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            // No-op in fallback mode
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void RegisterProfiler(ProfilingSession profilingSession)
        {
            // No-op in fallback mode
        }


        public EndPoint[] GetEndPoints(bool configuredOnly = false)
        {
            throw new NotImplementedException();
        }

        public void Wait(Task task)
        {
            throw new NotImplementedException();
        }

        public T Wait<T>(Task<T> task)
        {
            throw new NotImplementedException();
        }

        public void WaitAll(params Task[] tasks)
        {
            throw new NotImplementedException();
        }

        public int HashSlot(RedisKey key)
        {
            throw new NotImplementedException();
        }

        public ISubscriber GetSubscriber(object? asyncState = null)
        {
            throw new NotImplementedException();
        }

        public IServer[] GetServers()
        {
            throw new NotImplementedException();
        }

        public Task<bool> ConfigureAsync(TextWriter? log = null)
        {
            throw new NotImplementedException();
        }

        public bool Configure(TextWriter? log = null)
        {
            throw new NotImplementedException();
        }

        public string? GetStormLog()
        {
            throw new NotImplementedException();
        }

        public void ResetStormLog()
        {
            throw new NotImplementedException();
        }

        public long PublishReconfigure(CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> PublishReconfigureAsync(CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public int GetHashSlot(RedisKey key)
        {
            throw new NotImplementedException();
        }

        public void AddLibraryNameSuffix(string suffix)
        {
            throw new NotImplementedException();
        }

        public ServerCounters GetCounters()
        {
            throw new NotImplementedException();
        }

        public bool IsConnected => true;
        public bool IsReplica => false;
        public bool IncludeDetailInExceptions { get; set; }
        public int TimeoutMilliseconds { get; set; } = 5000;
        public long OperationCount => 0;
        public bool PreserveAsyncOrder { get; set; }
        public bool IncludePerformanceCountersInExceptions { get; set; }
        public string ClientName { get; set; } = "FallbackRedis";
        public string Configuration { get; } = "fallback";
        public int ConnectTimeout { get; set; } = 5000;
        public int SyncTimeout { get; set; } = 5000;


        public bool IsConnecting => throw new NotImplementedException();

        public int StormLogThreshold { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public event EventHandler<RedisErrorEventArgs>? ErrorMessage;
        public event EventHandler<ConnectionFailedEventArgs>? ConnectionFailed;
        public event EventHandler<InternalErrorEventArgs>? InternalError;
        public event EventHandler<ConnectionFailedEventArgs>? ConnectionRestored;
        public event EventHandler<EndPointEventArgs>? ConfigurationChanged;
        public event EventHandler<EndPointEventArgs>? ConfigurationChangedBroadcast;
        public event EventHandler<HashSlotMovedEventArgs>? HashSlotMoved;
        public event EventHandler<ServerMaintenanceEvent>? ServerMaintenanceEvent;

        // CS8766: IConnectionMultiplexer re-declares ToString() as non-nullable; make our
        // inherited implementation explicit so the nullability matches the interface.
        string IConnectionMultiplexer.ToString() => base.ToString() ?? string.Empty;

        // CS0067: these interface events are required by IConnectionMultiplexer but are never
        // raised by this in-memory fallback stub. Referencing them here (guarded so it never
        // runs at runtime) keeps the compiler quiet without raising fake events.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Must be instance to reference the events.")]
        private void SuppressUnusedEventWarnings()
        {
            _ = (ErrorMessage, ConnectionFailed, InternalError, ConnectionRestored,
                 ConfigurationChanged, ConfigurationChangedBroadcast, HashSlotMoved, ServerMaintenanceEvent);
        }
    }

    /// <summary>
    /// Fallback database implementation for when Redis is not available.
    /// </summary>
    public class FallbackDatabase : IDatabase
    {
        private readonly Dictionary<string, object> _cache;
        private readonly object _lock;
        private readonly ILogger _logger;

        public FallbackDatabase(Dictionary<string, object> cache, object lockObj, ILogger logger)
        {
            _cache = cache;
            _lock = lockObj;
            _logger = logger;
        }

        public IBatch CreateBatch(object? asyncState = null)
        {
            throw new NotImplementedException("Batch operations not supported in fallback mode");
        }

        public ITransaction CreateTransaction(object? asyncState = null)
        {
            throw new NotImplementedException("Transaction operations not supported in fallback mode");
        }

        public RedisValue DebugObject(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public bool GeoAdd(RedisKey key, double longitude, double latitude, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public bool GeoAdd(RedisKey key, GeoEntry value, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long GeoAdd(RedisKey key, GeoEntry[] values, CommandFlags flags = CommandFlags.None)
        {
            return values.Length;
        }

        public double? GeoDistance(RedisKey key, RedisValue member1, RedisValue member2, GeoUnit unit = GeoUnit.Meters, CommandFlags flags = CommandFlags.None)
        {
            return null;
        }

        public string[] GeoHash(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            return new string[members.Length];
        }

        public string GeoHash(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            return "";
        }

        public GeoPosition?[] GeoPosition(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            return new GeoPosition?[members.Length];
        }

        public GeoPosition? GeoPosition(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            return null;
        }

        public GeoRadiusResult[] GeoRadius(RedisKey key, RedisValue member, double radius, GeoUnit unit = GeoUnit.Meters, int count = -1, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<GeoRadiusResult>();
        }

        public GeoRadiusResult[] GeoRadius(RedisKey key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.Meters, int count = -1, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<GeoRadiusResult>();
        }

        public bool GeoRemove(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                return _cache.Remove(key!);
            }
        }

        public long HashDecrement(RedisKey key, RedisValue hashField, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public double HashDecrement(RedisKey key, RedisValue hashField, double value, CommandFlags flags = CommandFlags.None)
        {
            return 0.0;
        }

        public bool HashDelete(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long HashDelete(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            return hashFields.Length;
        }

        public bool HashExists(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            return false;
        }

        public RedisValue HashGet(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public RedisValue[] HashGet(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            return new RedisValue[hashFields.Length];
        }

        public HashEntry[] HashGetAll(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<HashEntry>();
        }

        public long HashIncrement(RedisKey key, RedisValue hashField, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public double HashIncrement(RedisKey key, RedisValue hashField, double value, CommandFlags flags = CommandFlags.None)
        {
            return 0.0;
        }

        public RedisValue[] HashKeys(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public long HashLength(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public bool HashSet(RedisKey key, RedisValue hashField, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public void HashSet(RedisKey key, HashEntry[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }

        public RedisValue[] HashValues(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public bool HyperLogLogAdd(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public bool HyperLogLogAdd(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long HyperLogLogLength(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long HyperLogLogLength(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public void HyperLogLogMerge(RedisKey destination, RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }

        public void HyperLogLogMerge(RedisKey destination, RedisKey[] sourceKeys, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }

        public EndPoint IdentifyEndpoint(RedisKey key = new RedisKey(), CommandFlags flags = CommandFlags.None)
        {
            return new IPEndPoint(IPAddress.Loopback, 6379);
        }

        public bool KeyDelete(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                return _cache.Remove(key!);
            }
        }

        public long KeyDelete(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                long deleted = 0;
                foreach (RedisKey key in keys)
                {
                    if (_cache.Remove(key!))
                    {
                        deleted++;
                    }
                }
                return deleted;
            }
        }

        public bool KeyExists(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                return _cache.ContainsKey(key!);
            }
        }

        public long KeyExists(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                return keys.Count(k => _cache.ContainsKey(k!));
            }
        }

        public bool KeyExpire(RedisKey key, TimeSpan? expiry, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public bool KeyExpire(RedisKey key, DateTime? expiry, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public TimeSpan? KeyIdleTime(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return TimeSpan.Zero;
        }

        public bool KeyMove(RedisKey key, int database, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public bool KeyPersist(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public bool KeyRename(RedisKey key, RedisKey newKey, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public void KeyRestore(RedisKey key, byte[] value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }

        public TimeSpan? KeyTimeToLive(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return TimeSpan.FromHours(1); // Default TTL
        }

        public RedisType KeyType(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisType.None;
        }

        public RedisValue ListGetByIndex(RedisKey key, long index, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public long ListInsertAfter(RedisKey key, RedisValue pivot, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long ListInsertBefore(RedisKey key, RedisValue pivot, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public RedisValue ListLeftPop(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public long ListLeftPush(RedisKey key, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long ListLeftPush(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long ListLength(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public RedisValue[] ListRange(RedisKey key, long start = 0, long stop = -1, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public long ListRemove(RedisKey key, RedisValue value, long count = 0, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public RedisValue ListRightPop(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public RedisValue ListRightPopLeftPush(RedisKey source, RedisKey destination, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public long ListRightPush(RedisKey key, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long ListRightPush(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public void ListSetByIndex(RedisKey key, long index, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }

        public void ListTrim(RedisKey key, long start, long stop, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }

        public bool LockExtend(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public RedisValue LockQuery(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public bool LockTake(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long Publish(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public void ScriptEvaluate(byte[] hash, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            // No-op in fallback mode
        }


        public RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            return RedisResult.Create(RedisValue.Null);
        }


        public bool SetAdd(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long SetAdd(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            return values.Length;
        }

        public RedisValue[] SetCombine(SetOperation operation, RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public RedisValue[] SetCombine(SetOperation operation, RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public long SetCombineAndStore(SetOperation operation, RedisKey destination, RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long SetCombineAndStore(SetOperation operation, RedisKey destination, RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public bool SetContains(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return false;
        }

        public long SetLength(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public RedisValue[] SetMembers(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public bool SetMove(RedisKey source, RedisKey destination, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public RedisValue SetPop(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public RedisValue[] SetPop(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public RedisValue SetRandomMember(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }

        public RedisValue[] SetRandomMembers(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public bool SetRemove(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long SetRemove(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            return values.Length;
        }

        public RedisValue[] SetScan(RedisKey key, RedisValue pattern = default, int pageSize = 10, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public bool SortedSetAdd(RedisKey key, RedisValue member, double score, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public bool SortedSetAdd(RedisKey key, RedisValue member, double score, When when, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long SortedSetAdd(RedisKey key, SortedSetEntry[] values, CommandFlags flags = CommandFlags.None)
        {
            return values.Length;
        }

        public long SortedSetAdd(RedisKey key, SortedSetEntry[] values, When when, CommandFlags flags = CommandFlags.None)
        {
            return values.Length;
        }

        public long SortedSetCombineAndStore(SetOperation operation, RedisKey destination, RedisKey first, RedisKey second, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long SortedSetCombineAndStore(SetOperation operation, RedisKey destination, RedisKey[] keys, double[]? weights = null, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public double SortedSetDecrement(RedisKey key, RedisValue member, double value, CommandFlags flags = CommandFlags.None)
        {
            return 0.0;
        }

        public double SortedSetIncrement(RedisKey key, RedisValue member, double value, CommandFlags flags = CommandFlags.None)
        {
            return 0.0;
        }

        public long SortedSetLength(RedisKey key, double min = double.NegativeInfinity, double max = double.PositiveInfinity, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long SortedSetLengthByValue(RedisKey key, RedisValue min, RedisValue max, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public RedisValue[] SortedSetRangeByRank(RedisKey key, long start = 0, long stop = -1, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public SortedSetEntry[] SortedSetRangeByRankWithScores(RedisKey key, long start = 0, long stop = -1, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<SortedSetEntry>();
        }

        public RedisValue[] SortedSetRangeByScore(RedisKey key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public SortedSetEntry[] SortedSetRangeByScoreWithScores(RedisKey key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<SortedSetEntry>();
        }

        public RedisValue[] SortedSetRangeByValue(RedisKey key, RedisValue min = default, RedisValue max = default, Exclude exclude = Exclude.None, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public long SortedSetRank(RedisKey key, RedisValue member, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            return -1;
        }

        public bool SortedSetRemove(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long SortedSetRemove(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            return members.Length;
        }

        public long SortedSetRemoveRangeByRank(RedisKey key, long start, long stop, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long SortedSetRemoveRangeByScore(RedisKey key, double start, double stop, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long SortedSetRemoveRangeByValue(RedisKey key, RedisValue min, RedisValue max, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public RedisValue[] SortedSetScan(RedisKey key, RedisValue pattern = default, int pageSize = 10, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            return Array.Empty<RedisValue>();
        }

        public double? SortedSetScore(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            return null;
        }

        public bool StringAppend(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }

        public long StringBitCount(RedisKey key, long start = 0, long end = -1, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long StringBitOperation(Bitwise operation, RedisKey destination, RedisKey first, RedisKey second = new RedisKey(), CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long StringBitOperation(Bitwise operation, RedisKey destination, RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long StringBitPosition(RedisKey key, bool bit, long start = 0, long end = -1, CommandFlags flags = CommandFlags.None)
        {
            return -1;
        }

        public double StringDecrement(RedisKey key, double value, CommandFlags flags = CommandFlags.None)
        {
            return 0.0;
        }

        public long StringDecrement(RedisKey key, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public bool StringGet(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            return false;
        }


        public bool StringGetBit(RedisKey key, long offset, CommandFlags flags = CommandFlags.None)
        {
            return false;
        }

        public RedisValue StringGetRange(RedisKey key, long start, long end, CommandFlags flags = CommandFlags.None)
        {
            return RedisValue.Null;
        }



        public double StringIncrement(RedisKey key, double value, CommandFlags flags = CommandFlags.None)
        {
            return 0.0;
        }

        public long StringIncrement(RedisKey key, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            return 0;
        }

        public long StringLength(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                return _cache.TryGetValue(key!, out object? value) ? (value?.ToString()?.Length ?? 0) : 0;
            }
        }

        public bool StringSet(RedisKey key, RedisValue value, TimeSpan? expiry = null, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                _cache[key!] = value;
                return true;
            }
        }

        public bool StringSet(KeyValuePair<RedisKey, RedisValue>[] values, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                foreach (KeyValuePair<RedisKey, RedisValue> kvp in values)
                {
                    _cache[kvp.Key!] = kvp.Value;
                }
                return true;
            }
        }

        public bool StringSetBit(RedisKey key, long offset, bool bit, CommandFlags flags = CommandFlags.None)
        {
            return true;
        }


        public TimeSpan Ping(CommandFlags flags = CommandFlags.None)
        {
            return TimeSpan.Zero;
        }

        public bool TryWait(Task task)
        {
            return true;
        }

        public void Wait(Task task)
        {
            // No-op in fallback mode
        }

        public T Wait<T>(Task<T> task)
        {
            return default;
        }

        public void WaitAll(params Task[] tasks)
        {
            // No-op in fallback mode
        }

        public void KeyMigrate(RedisKey key, EndPoint toServer, int toDatabase = 0, int timeoutMilliseconds = 0, MigrateOptions migrateOptions = MigrateOptions.None, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public GeoRadiusResult[] GeoSearch(RedisKey key, RedisValue member, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public GeoRadiusResult[] GeoSearch(RedisKey key, double longitude, double latitude, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long GeoSearchAndStore(RedisKey sourceKey, RedisKey destinationKey, RedisValue member, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, bool storeDistances = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long GeoSearchAndStore(RedisKey sourceKey, RedisKey destinationKey, double longitude, double latitude, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, bool storeDistances = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public ExpireResult[] HashFieldExpire(RedisKey key, RedisValue[] hashFields, TimeSpan expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public ExpireResult[] HashFieldExpire(RedisKey key, RedisValue[] hashFields, DateTime expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long[] HashFieldGetExpireDateTime(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public PersistResult[] HashFieldPersist(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long[] HashFieldGetTimeToLive(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Lease<byte>? HashGetLease(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue HashRandomField(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] HashRandomFields(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public HashEntry[] HashRandomFieldsWithValues(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<HashEntry> HashScan(RedisKey key, RedisValue pattern, int pageSize, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<HashEntry> HashScan(RedisKey key, RedisValue pattern = default, int pageSize = 250, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<RedisValue> HashScanNoValues(RedisKey key, RedisValue pattern = default, int pageSize = 250, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long HashStringLength(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool KeyCopy(RedisKey sourceKey, RedisKey destinationKey, int destinationDatabase = -1, bool replace = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public byte[]? KeyDump(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public string? KeyEncoding(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool KeyExpire(RedisKey key, TimeSpan? expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool KeyExpire(RedisKey key, DateTime? expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public DateTime? KeyExpireTime(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long? KeyFrequency(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisKey KeyRandom(CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long? KeyRefCount(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool KeyTouch(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long KeyTouch(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] ListLeftPop(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public ListPopResult ListLeftPop(RedisKey[] keys, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long ListPosition(RedisKey key, RedisValue element, long rank = 1, long maxLength = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long[] ListPositions(RedisKey key, RedisValue element, long count, long rank = 1, long maxLength = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long ListLeftPush(RedisKey key, RedisValue[] values, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue ListMove(RedisKey sourceKey, RedisKey destinationKey, ListSide sourceSide, ListSide destinationSide, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] ListRightPop(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public ListPopResult ListRightPop(RedisKey[] keys, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long ListRightPush(RedisKey key, RedisValue[] values, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool LockRelease(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisResult Execute(string command, params object[] args)
        {
            throw new NotImplementedException();
        }

        public RedisResult Execute(string command, ICollection<object> args, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        RedisResult IDatabase.ScriptEvaluate(byte[] hash, RedisKey[]? keys, RedisValue[]? values, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public RedisResult ScriptEvaluate(LuaScript script, object? parameters = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisResult ScriptEvaluate(LoadedLuaScript script, object? parameters = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisResult ScriptEvaluateReadOnly(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisResult ScriptEvaluateReadOnly(byte[] hash, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool[] SetContains(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long SetIntersectionLength(RedisKey[] keys, long limit = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<RedisValue> SetScan(RedisKey key, RedisValue pattern, int pageSize, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        IEnumerable<RedisValue> IDatabase.SetScan(RedisKey key, RedisValue pattern, int pageSize, long cursor, int pageOffset, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] Sort(RedisKey key, long skip = 0, long take = -1, Order order = Order.Ascending, SortType sortType = SortType.Numeric, RedisValue by = default, RedisValue[]? get = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long SortAndStore(RedisKey destination, RedisKey key, long skip = 0, long take = -1, Order order = Order.Ascending, SortType sortType = SortType.Numeric, RedisValue by = default, RedisValue[]? get = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool SortedSetAdd(RedisKey key, RedisValue member, double score, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long SortedSetAdd(RedisKey key, SortedSetEntry[] values, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] SortedSetCombine(SetOperation operation, RedisKey[] keys, double[]? weights = null, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public SortedSetEntry[] SortedSetCombineWithScores(SetOperation operation, RedisKey[] keys, double[]? weights = null, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long SortedSetIntersectionLength(RedisKey[] keys, long limit = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue SortedSetRandomMember(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] SortedSetRandomMembers(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public SortedSetEntry[] SortedSetRandomMembersWithScores(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long SortedSetRangeAndStore(RedisKey sourceKey, RedisKey destinationKey, RedisValue start, RedisValue stop, SortedSetOrder sortedSetOrder = SortedSetOrder.ByRank, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long? take = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] SortedSetRangeByValue(RedisKey key, RedisValue min = default, RedisValue max = default, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        long? IDatabase.SortedSetRank(RedisKey key, RedisValue member, Order order, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<SortedSetEntry> SortedSetScan(RedisKey key, RedisValue pattern, int pageSize, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        IEnumerable<SortedSetEntry> IDatabase.SortedSetScan(RedisKey key, RedisValue pattern, int pageSize, long cursor, int pageOffset, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public double?[] SortedSetScores(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public SortedSetEntry? SortedSetPop(RedisKey key, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public SortedSetEntry[] SortedSetPop(RedisKey key, long count, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public SortedSetPopResult SortedSetPop(RedisKey[] keys, long count, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool SortedSetUpdate(RedisKey key, RedisValue member, double score, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long SortedSetUpdate(RedisKey key, SortedSetEntry[] values, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StreamAcknowledge(RedisKey key, RedisValue groupName, RedisValue messageId, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StreamAcknowledge(RedisKey key, RedisValue groupName, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StreamAdd(RedisKey key, RedisValue streamField, RedisValue streamValue, RedisValue? messageId = null, int? maxLength = null, bool useApproximateMaxLength = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StreamAdd(RedisKey key, NameValueEntry[] streamPairs, RedisValue? messageId = null, int? maxLength = null, bool useApproximateMaxLength = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamAutoClaimResult StreamAutoClaim(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue startAtId, int? count = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamAutoClaimIdsOnlyResult StreamAutoClaimIdsOnly(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue startAtId, int? count = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamEntry[] StreamClaim(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] StreamClaimIdsOnly(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool StreamConsumerGroupSetPosition(RedisKey key, RedisValue groupName, RedisValue position, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamConsumerInfo[] StreamConsumerInfo(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool StreamCreateConsumerGroup(RedisKey key, RedisValue groupName, RedisValue? position, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public bool StreamCreateConsumerGroup(RedisKey key, RedisValue groupName, RedisValue? position = null, bool createStream = true, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StreamDelete(RedisKey key, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StreamDeleteConsumer(RedisKey key, RedisValue groupName, RedisValue consumerName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool StreamDeleteConsumerGroup(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamGroupInfo[] StreamGroupInfo(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamInfo StreamInfo(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StreamLength(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamPendingInfo StreamPending(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamPendingMessageInfo[] StreamPendingMessages(RedisKey key, RedisValue groupName, int count, RedisValue consumerName, RedisValue? minId = null, RedisValue? maxId = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamEntry[] StreamRange(RedisKey key, RedisValue? minId = null, RedisValue? maxId = null, int? count = null, Order messageOrder = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamEntry[] StreamRead(RedisKey key, RedisValue position, int? count = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisStream[] StreamRead(StreamPosition[] streamPositions, int? countPerStream = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public StreamEntry[] StreamReadGroup(RedisKey key, RedisValue groupName, RedisValue consumerName, RedisValue? position, int? count, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public StreamEntry[] StreamReadGroup(RedisKey key, RedisValue groupName, RedisValue consumerName, RedisValue? position = null, int? count = null, bool noAck = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisStream[] StreamReadGroup(StreamPosition[] streamPositions, RedisValue groupName, RedisValue consumerName, int? countPerStream, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public RedisStream[] StreamReadGroup(StreamPosition[] streamPositions, RedisValue groupName, RedisValue consumerName, int? countPerStream = null, bool noAck = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StreamTrim(RedisKey key, int maxLength, bool useApproximateMaxLength = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        long IDatabase.StringAppend(RedisKey key, RedisValue value, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public long StringBitCount(RedisKey key, long start = 0, long end = -1, StringIndexType indexType = StringIndexType.Byte, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StringBitPosition(RedisKey key, bool bit, long start = 0, long end = -1, StringIndexType indexType = StringIndexType.Byte, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        RedisValue IDatabase.StringGet(RedisKey key, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public RedisValue[] StringGet(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Lease<byte>? StringGetLease(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringGetSet(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringGetSetExpiry(RedisKey key, TimeSpan? expiry, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringGetSetExpiry(RedisKey key, DateTime expiry, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringGetDelete(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValueWithExpiry StringGetWithExpiry(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public string? StringLongestCommonSubsequence(RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public long StringLongestCommonSubsequenceLength(RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public LCSMatchResult StringLongestCommonSubsequenceWithMatches(RedisKey first, RedisKey second, long minLength = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool StringSet(RedisKey key, RedisValue value, TimeSpan? expiry, When when)
        {
            throw new NotImplementedException();
        }

        public bool StringSet(RedisKey key, RedisValue value, TimeSpan? expiry = null, bool keepTtl = false, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringSetAndGet(RedisKey key, RedisValue value, TimeSpan? expiry, When when, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringSetAndGet(RedisKey key, RedisValue value, TimeSpan? expiry = null, bool keepTtl = false, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public RedisValue StringSetRange(RedisKey key, long offset, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public bool IsConnected(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task KeyMigrateAsync(RedisKey key, EndPoint toServer, int toDatabase = 0, int timeoutMilliseconds = 0, MigrateOptions migrateOptions = MigrateOptions.None, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> DebugObjectAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> GeoAddAsync(RedisKey key, double longitude, double latitude, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> GeoAddAsync(RedisKey key, GeoEntry value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> GeoAddAsync(RedisKey key, GeoEntry[] values, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> GeoRemoveAsync(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double?> GeoDistanceAsync(RedisKey key, RedisValue member1, RedisValue member2, GeoUnit unit = GeoUnit.Meters, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<string?[]> GeoHashAsync(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<string?> GeoHashAsync(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<GeoPosition?[]> GeoPositionAsync(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<GeoPosition?> GeoPositionAsync(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<GeoRadiusResult[]> GeoRadiusAsync(RedisKey key, RedisValue member, double radius, GeoUnit unit = GeoUnit.Meters, int count = -1, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<GeoRadiusResult[]> GeoRadiusAsync(RedisKey key, double longitude, double latitude, double radius, GeoUnit unit = GeoUnit.Meters, int count = -1, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<GeoRadiusResult[]> GeoSearchAsync(RedisKey key, RedisValue member, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<GeoRadiusResult[]> GeoSearchAsync(RedisKey key, double longitude, double latitude, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, GeoRadiusOptions options = GeoRadiusOptions.Default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> GeoSearchAndStoreAsync(RedisKey sourceKey, RedisKey destinationKey, RedisValue member, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, bool storeDistances = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> GeoSearchAndStoreAsync(RedisKey sourceKey, RedisKey destinationKey, double longitude, double latitude, GeoSearchShape shape, int count = -1, bool demandClosest = true, Order? order = null, bool storeDistances = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HashDecrementAsync(RedisKey key, RedisValue hashField, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double> HashDecrementAsync(RedisKey key, RedisValue hashField, double value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> HashDeleteAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HashDeleteAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> HashExistsAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<ExpireResult[]> HashFieldExpireAsync(RedisKey key, RedisValue[] hashFields, TimeSpan expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<ExpireResult[]> HashFieldExpireAsync(RedisKey key, RedisValue[] hashFields, DateTime expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long[]> HashFieldGetExpireDateTimeAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<PersistResult[]> HashFieldPersistAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long[]> HashFieldGetTimeToLiveAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> HashGetAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<Lease<byte>?> HashGetLeaseAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> HashGetAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<HashEntry[]> HashGetAllAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HashIncrementAsync(RedisKey key, RedisValue hashField, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double> HashIncrementAsync(RedisKey key, RedisValue hashField, double value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> HashKeysAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HashLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> HashRandomFieldAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> HashRandomFieldsAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<HashEntry[]> HashRandomFieldsWithValuesAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<HashEntry> HashScanAsync(RedisKey key, RedisValue pattern = default, int pageSize = 250, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<RedisValue> HashScanNoValuesAsync(RedisKey key, RedisValue pattern = default, int pageSize = 250, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task HashSetAsync(RedisKey key, HashEntry[] hashFields, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> HashSetAsync(RedisKey key, RedisValue hashField, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HashStringLengthAsync(RedisKey key, RedisValue hashField, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> HashValuesAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> HyperLogLogAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> HyperLogLogAddAsync(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HyperLogLogLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> HyperLogLogLengthAsync(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task HyperLogLogMergeAsync(RedisKey destination, RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task HyperLogLogMergeAsync(RedisKey destination, RedisKey[] sourceKeys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<EndPoint?> IdentifyEndpointAsync(RedisKey key = default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyCopyAsync(RedisKey sourceKey, RedisKey destinationKey, int destinationDatabase = -1, bool replace = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> KeyDeleteAsync(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]?> KeyDumpAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<string?> KeyEncodingAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyExistsAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> KeyExistsAsync(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyExpireAsync(RedisKey key, DateTime? expiry, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyExpireAsync(RedisKey key, DateTime? expiry, ExpireWhen when = ExpireWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<DateTime?> KeyExpireTimeAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long?> KeyFrequencyAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<TimeSpan?> KeyIdleTimeAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyMoveAsync(RedisKey key, int database, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyPersistAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisKey> KeyRandomAsync(CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long?> KeyRefCountAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyRenameAsync(RedisKey key, RedisKey newKey, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task KeyRestoreAsync(RedisKey key, byte[] value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<TimeSpan?> KeyTimeToLiveAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> KeyTouchAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> KeyTouchAsync(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisType> KeyTypeAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> ListGetByIndexAsync(RedisKey key, long index, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> ListInsertAfterAsync(RedisKey key, RedisValue pivot, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> ListInsertBeforeAsync(RedisKey key, RedisValue pivot, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> ListLeftPopAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> ListLeftPopAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<ListPopResult> ListLeftPopAsync(RedisKey[] keys, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> ListPositionAsync(RedisKey key, RedisValue element, long rank = 1, long maxLength = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long[]> ListPositionsAsync(RedisKey key, RedisValue element, long count, long rank = 1, long maxLength = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }
        // =========================================================================
        // ✅ SMART IMPLEMENTATION: ListLeftPushAsync (In-Memory Redis)
        // =========================================================================

        public Task<long> ListLeftPushAsync(RedisKey key, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                string strKey = key.ToString();

                // Check existence for conditional insertion (When.Exists / When.NotExists)
                bool exists = _cache.TryGetValue(strKey, out object? obj);

                if (when == When.Exists && !exists) return Task.FromResult(0L);
                if (when == When.NotExists && exists) return Task.FromResult(0L);

                // Get or create list
                List<RedisValue> list;
                if (!exists || obj is not List<RedisValue>)
                {
                    // If key doesn't exist or isn't a list (and we're overwriting/ignoring types for simple fallback), create new
                    list = new List<RedisValue>();
                    _cache[strKey] = list;
                }
                else
                {
                    list = (List<RedisValue>)obj!;
                }

                list.Insert(0, value);
                return Task.FromResult((long)list.Count);
            }
        }

        public Task<long> ListLeftPushAsync(RedisKey key, RedisValue[] values, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            if (values == null || values.Length == 0) return Task.FromResult(0L);

            lock (_lock)
            {
                string strKey = key.ToString();
                bool exists = _cache.TryGetValue(strKey, out object? obj);

                if (when == When.Exists && !exists) return Task.FromResult(0L);
                if (when == When.NotExists && exists) return Task.FromResult(0L);

                List<RedisValue> list;
                if (!exists || obj is not List<RedisValue>)
                {
                    list = new List<RedisValue>();
                    _cache[strKey] = list;
                }
                else
                {
                    list = (List<RedisValue>)obj!;
                }

                // Redis LPUSH behavior: values are pushed one by one.
                // LPUSH key a b c -> results in [c, b, a, ...]
                // So we iterate normally and Insert(0, ...) or InsertRange(0, values.Reverse())
                // Simpler: Insert them in reverse order at index 0 to maintain input order at the head.
                for (int i = 0; i < values.Length; i++)
                {
                    list.Insert(i, values[i]);
                }

                return Task.FromResult((long)list.Count);
            }
        }

        public Task<long> ListLeftPushAsync(RedisKey key, RedisValue[] values, CommandFlags flags)
        {
            return ListLeftPushAsync(key, values, When.Always, flags);
        }

        public Task<long> ListLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> ListMoveAsync(RedisKey sourceKey, RedisKey destinationKey, ListSide sourceSide, ListSide destinationSide, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> ListRangeAsync(RedisKey key, long start = 0, long stop = -1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> ListRemoveAsync(RedisKey key, RedisValue value, long count = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> ListRightPopAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> ListRightPopAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<ListPopResult> ListRightPopAsync(RedisKey[] keys, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> ListRightPopLeftPushAsync(RedisKey source, RedisKey destination, CommandFlags flags = CommandFlags.None)
        {
            lock (_lock)
            {
                string srcKey = source.ToString();

                if (!_cache.TryGetValue(srcKey, out object? srcObj) ||
                    srcObj is not List<RedisValue> sourceList ||
                    sourceList.Count == 0)
                {
                    return Task.FromResult(RedisValue.Null);
                }

                RedisValue value = sourceList[^1];
                sourceList.RemoveAt(sourceList.Count - 1);

                string destKey = destination.ToString();
                if (!_cache.TryGetValue(destKey, out object? destObj) || destObj is not List<RedisValue> destList)
                {
                    destList = new List<RedisValue>();
                    _cache[destKey] = destList;
                }

                destList.Insert(0, value);
                return Task.FromResult(value);
            }
        }

        public Task<long> ListRightPushAsync(RedisKey key, RedisValue value, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> ListRightPushAsync(RedisKey key, RedisValue[] values, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> ListRightPushAsync(RedisKey key, RedisValue[] values, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task ListSetByIndexAsync(RedisKey key, long index, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task ListTrimAsync(RedisKey key, long start, long stop, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> LockExtendAsync(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> LockQueryAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> LockReleaseAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> LockTakeAsync(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> PublishAsync(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ExecuteAsync(string command, params object[] args)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ExecuteAsync(string command, ICollection<object>? args, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ScriptEvaluateAsync(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ScriptEvaluateAsync(byte[] hash, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ScriptEvaluateAsync(LuaScript script, object? parameters = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ScriptEvaluateAsync(LoadedLuaScript script, object? parameters = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ScriptEvaluateReadOnlyAsync(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisResult> ScriptEvaluateReadOnlyAsync(byte[] hash, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SetAddAsync(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SetCombineAsync(SetOperation operation, RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SetCombineAsync(SetOperation operation, RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SetCombineAndStoreAsync(SetOperation operation, RedisKey destination, RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SetCombineAndStoreAsync(SetOperation operation, RedisKey destination, RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SetContainsAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool[]> SetContainsAsync(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SetIntersectionLengthAsync(RedisKey[] keys, long limit = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SetLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SetMoveAsync(RedisKey source, RedisKey destination, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> SetPopAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SetPopAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> SetRandomMemberAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SetRandomMembersAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SetRemoveAsync(RedisKey key, RedisValue[] values, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<RedisValue> SetScanAsync(RedisKey key, RedisValue pattern = default, int pageSize = 250, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortAsync(RedisKey key, long skip = 0, long take = -1, Order order = Order.Ascending, SortType sortType = SortType.Numeric, RedisValue by = default, RedisValue[]? get = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortAndStoreAsync(RedisKey destination, RedisKey key, long skip = 0, long take = -1, Order order = Order.Ascending, SortType sortType = SortType.Numeric, RedisValue by = default, RedisValue[]? get = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, When when, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetAddAsync(RedisKey key, SortedSetEntry[] values, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetAddAsync(RedisKey key, SortedSetEntry[] values, When when, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetAddAsync(RedisKey key, SortedSetEntry[] values, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortedSetCombineAsync(SetOperation operation, RedisKey[] keys, double[]? weights = null, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetEntry[]> SortedSetCombineWithScoresAsync(SetOperation operation, RedisKey[] keys, double[]? weights = null, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetCombineAndStoreAsync(SetOperation operation, RedisKey destination, RedisKey first, RedisKey second, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetCombineAndStoreAsync(SetOperation operation, RedisKey destination, RedisKey[] keys, double[]? weights = null, Aggregate aggregate = Aggregate.Sum, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double> SortedSetDecrementAsync(RedisKey key, RedisValue member, double value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double> SortedSetIncrementAsync(RedisKey key, RedisValue member, double value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetIntersectionLengthAsync(RedisKey[] keys, long limit = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetLengthAsync(RedisKey key, double min = double.NegativeInfinity, double max = double.PositiveInfinity, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetLengthByValueAsync(RedisKey key, RedisValue min, RedisValue max, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> SortedSetRandomMemberAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortedSetRandomMembersAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetEntry[]> SortedSetRandomMembersWithScoresAsync(RedisKey key, long count, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortedSetRangeByRankAsync(RedisKey key, long start = 0, long stop = -1, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetRangeAndStoreAsync(RedisKey sourceKey, RedisKey destinationKey, RedisValue start, RedisValue stop, SortedSetOrder sortedSetOrder = SortedSetOrder.ByRank, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long? take = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetEntry[]> SortedSetRangeByRankWithScoresAsync(RedisKey key, long start = 0, long stop = -1, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortedSetRangeByScoreAsync(RedisKey key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetEntry[]> SortedSetRangeByScoreWithScoresAsync(RedisKey key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortedSetRangeByValueAsync(RedisKey key, RedisValue min, RedisValue max, Exclude exclude, long skip, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> SortedSetRangeByValueAsync(RedisKey key, RedisValue min = default, RedisValue max = default, Exclude exclude = Exclude.None, Order order = Order.Ascending, long skip = 0, long take = -1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long?> SortedSetRankAsync(RedisKey key, RedisValue member, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SortedSetRemoveAsync(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetRemoveAsync(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetRemoveRangeByRankAsync(RedisKey key, long start, long stop, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetRemoveRangeByScoreAsync(RedisKey key, double start, double stop, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetRemoveRangeByValueAsync(RedisKey key, RedisValue min, RedisValue max, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<SortedSetEntry> SortedSetScanAsync(RedisKey key, RedisValue pattern = default, int pageSize = 250, long cursor = 0, int pageOffset = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double?> SortedSetScoreAsync(RedisKey key, RedisValue member, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double?[]> SortedSetScoresAsync(RedisKey key, RedisValue[] members, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SortedSetUpdateAsync(RedisKey key, RedisValue member, double score, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> SortedSetUpdateAsync(RedisKey key, SortedSetEntry[] values, SortedSetWhen when = SortedSetWhen.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetEntry?> SortedSetPopAsync(RedisKey key, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetEntry[]> SortedSetPopAsync(RedisKey key, long count, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<SortedSetPopResult> SortedSetPopAsync(RedisKey[] keys, long count, Order order = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StreamAcknowledgeAsync(RedisKey key, RedisValue groupName, RedisValue messageId, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StreamAcknowledgeAsync(RedisKey key, RedisValue groupName, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StreamAddAsync(RedisKey key, RedisValue streamField, RedisValue streamValue, RedisValue? messageId = null, int? maxLength = null, bool useApproximateMaxLength = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StreamAddAsync(RedisKey key, NameValueEntry[] streamPairs, RedisValue? messageId = null, int? maxLength = null, bool useApproximateMaxLength = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamAutoClaimResult> StreamAutoClaimAsync(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue startAtId, int? count = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamAutoClaimIdsOnlyResult> StreamAutoClaimIdsOnlyAsync(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue startAtId, int? count = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamEntry[]> StreamClaimAsync(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> StreamClaimIdsOnlyAsync(RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer, long minIdleTimeInMs, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StreamConsumerGroupSetPositionAsync(RedisKey key, RedisValue groupName, RedisValue position, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamConsumerInfo[]> StreamConsumerInfoAsync(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StreamCreateConsumerGroupAsync(RedisKey key, RedisValue groupName, RedisValue? position, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StreamCreateConsumerGroupAsync(RedisKey key, RedisValue groupName, RedisValue? position = null, bool createStream = true, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StreamDeleteAsync(RedisKey key, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StreamDeleteConsumerAsync(RedisKey key, RedisValue groupName, RedisValue consumerName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StreamDeleteConsumerGroupAsync(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamGroupInfo[]> StreamGroupInfoAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamInfo> StreamInfoAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StreamLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamPendingInfo> StreamPendingAsync(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamPendingMessageInfo[]> StreamPendingMessagesAsync(RedisKey key, RedisValue groupName, int count, RedisValue consumerName, RedisValue? minId = null, RedisValue? maxId = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamEntry[]> StreamRangeAsync(RedisKey key, RedisValue? minId = null, RedisValue? maxId = null, int? count = null, Order messageOrder = Order.Ascending, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamEntry[]> StreamReadAsync(RedisKey key, RedisValue position, int? count = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisStream[]> StreamReadAsync(StreamPosition[] streamPositions, int? countPerStream = null, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<StreamEntry[]> StreamReadGroupAsync(RedisKey key, RedisValue groupName, RedisValue consumerName, RedisValue? position, int? count, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<StreamEntry[]> StreamReadGroupAsync(RedisKey key, RedisValue groupName, RedisValue consumerName, RedisValue? position = null, int? count = null, bool noAck = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisStream[]> StreamReadGroupAsync(StreamPosition[] streamPositions, RedisValue groupName, RedisValue consumerName, int? countPerStream, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<RedisStream[]> StreamReadGroupAsync(StreamPosition[] streamPositions, RedisValue groupName, RedisValue consumerName, int? countPerStream = null, bool noAck = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StreamTrimAsync(RedisKey key, int maxLength, bool useApproximateMaxLength = false, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringAppendAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringBitCountAsync(RedisKey key, long start, long end, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringBitCountAsync(RedisKey key, long start = 0, long end = -1, StringIndexType indexType = StringIndexType.Byte, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringBitOperationAsync(Bitwise operation, RedisKey destination, RedisKey first, RedisKey second = default, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringBitOperationAsync(Bitwise operation, RedisKey destination, RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringBitPositionAsync(RedisKey key, bool bit, long start, long end, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringBitPositionAsync(RedisKey key, bool bit, long start = 0, long end = -1, StringIndexType indexType = StringIndexType.Byte, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringDecrementAsync(RedisKey key, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double> StringDecrementAsync(RedisKey key, double value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue[]> StringGetAsync(RedisKey[] keys, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<Lease<byte>?> StringGetLeaseAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StringGetBitAsync(RedisKey key, long offset, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringGetRangeAsync(RedisKey key, long start, long end, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringGetSetAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringGetSetExpiryAsync(RedisKey key, TimeSpan? expiry, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringGetSetExpiryAsync(RedisKey key, DateTime expiry, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringGetDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValueWithExpiry> StringGetWithExpiryAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringIncrementAsync(RedisKey key, long value = 1, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<double> StringIncrementAsync(RedisKey key, double value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringLengthAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<string?> StringLongestCommonSubsequenceAsync(RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<long> StringLongestCommonSubsequenceLengthAsync(RedisKey first, RedisKey second, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<LCSMatchResult> StringLongestCommonSubsequenceWithMatchesAsync(RedisKey first, RedisKey second, long minLength = 0, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry, When when)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry, When when, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, bool keepTtl = false, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StringSetAsync(KeyValuePair<RedisKey, RedisValue>[] values, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringSetAndGetAsync(RedisKey key, RedisValue value, TimeSpan? expiry, When when, CommandFlags flags)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringSetAndGetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, bool keepTtl = false, When when = When.Always, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<bool> StringSetBitAsync(RedisKey key, long offset, bool bit, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<RedisValue> StringSetRangeAsync(RedisKey key, long offset, RedisValue value, CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None)
        {
            throw new NotImplementedException();
        }

        public int Database => 0;
        public IConnectionMultiplexer Multiplexer => null;
    }

    // --- STEP 4: Add a static UI helper class for beautiful console output ---
    public static class ConsoleUI
    {
        public static void WriteHeader(string text)
        {
            Console.ResetColor();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"--- ╔═════════════════════════════════════╗ ---");
            Console.WriteLine($"--- ║   {text.PadRight(35)[..35]} ║ ---");
            Console.WriteLine($"--- ╚═════════════════════════════════════╝ ---");
            Console.ResetColor();
        }

        public static void WriteSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✅ {message}");
            Console.ResetColor();
        }

        public static void WriteInfo(string message)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"ℹ️ {message}");
            Console.ResetColor();
        }

        public static void WriteWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠️ {message}");
            Console.ResetColor();
        }

        public static void WriteError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ {message}");
            Console.ResetColor();
        }
    }
}