#pragma warning disable 1591

using System;
using Pokemon_3D_Server_Core.Server_Client_Listener.Loggers;

namespace Pokemon_3D_Server_Core.GameJolt
{
    /// <summary>
    /// Thin lifecycle wrapper for <see cref="Pokemon_3D_Server_Core.GameJoltHttp"/>.
    /// Handles start/stop and logs via Core.Logger (with Console fallback).
    /// Also exposes simple activity logging helpers for user registration/login events.
    /// </summary>
    public sealed class GameJoltHttpServer
    {
        /// <summary>TCP port the HTTP server listens on.</summary>
        public int Port { get; private set; }

        /// <summary>True if the underlying listener is running.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Create and start the server using the P3D_GJ_HTTP_PORT environment variable
        /// (defaults to 8080 if not set or invalid).
        /// </summary>
        public GameJoltHttpServer() : this(ReadPortFromEnvironment()) { }

        /// <summary>
        /// Create and start the server on a specific port.
        /// </summary>
        public GameJoltHttpServer(int port)
        {
            if (port <= 0 || port >= 65536)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

            Port = port;
            Start();
        }

        /// <summary>
        /// Start the HTTP server if not already running.
        /// </summary>
        public void Start()
        {
            if (IsRunning) return;

            try
            {
                // Fully-qualify to the existing static server class.
                Pokemon_3D_Server_Core.GameJoltHttp.Start(Port);
                IsRunning = true;
                LogInfo($"[GameJoltHttp] Listening at http://localhost:{Port}/ (site + API active)");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LogWarn($"[GameJoltHttp] Startup failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stop the HTTP server if running (safe to call multiple times).
        /// </summary>
        public void Stop()
        {
            if (!IsRunning) return;

            try
            {
                Pokemon_3D_Server_Core.GameJoltHttp.Stop();
                IsRunning = false;
                LogInfo($"[GameJoltHttp] Stopped (port {Port}).");
            }
            catch (Exception ex)
            {
                LogWarn($"[GameJoltHttp] Stop failed: {ex.Message}");
            }
        }

        // -------------------------
        // Activity logging helpers
        // -------------------------

        /// <summary>
        /// General-purpose GameJolt activity log entry.
        /// </summary>
        public static void LogActivity(string message)
        {
            try { Core.Logger?.Log($"[GameJolt] {message}", Logger.LogTypes.Info); }
            catch { Console.WriteLine($"[GameJolt] {message}"); }
        }

        /// <summary>
        /// Convenience wrapper: call when a new user is registered.
        /// </summary>
        public static void LogUserRegistered(string username)
            => LogActivity($"New user registered: {username}");

        /// <summary>
        /// Convenience wrapper: call on successful login.
        /// </summary>
        public static void LogLoginSuccess(string username)
            => LogActivity($"User login success: {username}");

        /// <summary>
        /// Convenience wrapper: call on failed login attempt.
        /// </summary>
        public static void LogLoginFailed(string username)
            => LogActivity($"User login failed: {username}");

        // ---- helpers ----

        private static int ReadPortFromEnvironment()
        {
            const int fallback = 8080;
            try
            {
                var env = Environment.GetEnvironmentVariable("P3D_GJ_HTTP_PORT");
                if (!string.IsNullOrWhiteSpace(env) &&
                    int.TryParse(env, out var p) &&
                    p > 0 && p < 65536)
                {
                    return p;
                }
            }
            catch { /* ignore and use fallback */ }
            return fallback;
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