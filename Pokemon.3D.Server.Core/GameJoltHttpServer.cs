#pragma warning disable 1591

using System;
using Pokemon_3D_Server_Core.Server_Client_Listener.Loggers;

namespace Pokemon_3D_Server_Core.GameJolt
{
    /// <summary>
    /// Lifecycle wrapper for the legacy GameJolt-compatible HTTP implementation.
    /// The user-facing service name is configurable and defaults to ServerLogin.
    /// </summary>
    public sealed class GameJoltHttpServer
    {
        public int Port { get; private set; }
        public string ServiceName { get; private set; }
        public bool IsRunning { get; private set; }

        public GameJoltHttpServer() : this(ReadPortFromEnvironment(), "ServerLogin") { }
        public GameJoltHttpServer(int port) : this(port, "ServerLogin") { }

        public GameJoltHttpServer(int port, string serviceName)
        {
            if (port <= 0 || port >= 65536)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

            Port = port;
            ServiceName = string.IsNullOrWhiteSpace(serviceName) ? "ServerLogin" : serviceName.Trim();
            Start();
        }

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                Pokemon_3D_Server_Core.GameJoltHttp.Start(Port);
                IsRunning = true;
                LogInfo($"[{ServiceName}] Listening at http://localhost:{Port}/ (legacy compatibility API active)");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LogWarn($"[{ServiceName}] Startup failed: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try
            {
                Pokemon_3D_Server_Core.GameJoltHttp.Stop();
                IsRunning = false;
                LogInfo($"[{ServiceName}] Stopped (port {Port}).");
            }
            catch (Exception ex)
            {
                LogWarn($"[{ServiceName}] Stop failed: {ex.Message}");
            }
        }

        public static void LogActivity(string message)
        {
            var name = Core.Identity?.LoginServiceName ?? "ServerLogin";
            try { Core.Logger?.Log($"[{name}] {message}", Logger.LogTypes.Info); }
            catch { Console.WriteLine($"[{name}] {message}"); }
        }

        public static void LogUserRegistered(string username)
            => LogActivity($"New user registered: {username}");
        public static void LogLoginSuccess(string username)
            => LogActivity($"User login success: {username}");
        public static void LogLoginFailed(string username)
            => LogActivity($"User login failed: {username}");

        private static int ReadPortFromEnvironment()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("P3D_GJ_HTTP_PORT");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var p) && p > 0 && p < 65536)
                    return p;
            }
            catch { }
            return 8080;
        }

        private static void LogInfo(string msg)
        {
            try { Core.Logger?.Log(msg, Logger.LogTypes.Info); }
            catch { Console.WriteLine(msg); }
        }

        private static void LogWarn(string msg)
        {
            try { Core.Logger?.Log(msg, Logger.LogTypes.Warning); }
            catch { Console.WriteLine(msg); }
        }
    }
}

#pragma warning restore 1591