#pragma warning disable 1591

using System;
using Pokemon_3D_Server_Core.Server_Client_Listener.Commands;
using Pokemon_3D_Server_Core.Server_Client_Listener.Loggers;
using Pokemon_3D_Server_Core.Server_Client_Listener.Settings;
using Pokemon_3D_Server_Core.Shared.jianmingyong;
using Pokemon_3D_Server_Core.Shared.jianmingyong.Modules;
// add: resolves GameJoltHttpServer wrapper
using Pokemon_3D_Server_Core.GameJolt;

namespace Pokemon_3D_Server_Core
{
    /// <summary>
    /// Main server initialization and lifecycle management.
    /// </summary>
    public class Core
    {
        public static Setting Setting { get; private set; }
        public static LoggerCollection Logger { get; private set; }
        public static Updater Updater { get; private set; }
        public static Server_Client_Listener.Servers.Listener Listener { get; private set; }
        public static RCON_Client_Listener.Servers.Listener RCONListener { get; private set; }
        public static CommandCollection Command { get; private set; }

        #region Pokémon 3D Listener
        public static Server_Client_Listener.Players.PlayerCollection Player { get; } =
            new Server_Client_Listener.Players.PlayerCollection();
        public static Server_Client_Listener.Worlds.World World { get; } =
            new Server_Client_Listener.Worlds.World();
        #endregion

        #region RCON Listener
        public static RCON_Client_Listener.Players.PlayerCollection RCONPlayer { get; } =
            new RCON_Client_Listener.Players.PlayerCollection();
        public static RCON_Client_Listener.Uploader.UploaderQueue RCONUploadQueue { get; } =
            new RCON_Client_Listener.Uploader.UploaderQueue();
        #endregion

        #region RCON GUI Listener
        public static RCON_GUI_Client_Listener.Servers.Listener RCONGUIListener { get; set; }
        public static RCON_GUI_Client_Listener.Downloader.DownloaderQueue RCONGUIDownloadQueue { get; } =
            new RCON_GUI_Client_Listener.Downloader.DownloaderQueue();
        #endregion

        #region GameJolt HTTP Listener
        /// <summary>
        /// GameJolt-compatible HTTP server wrapper. Initialized in Start() (no hardcoded port here).
        /// </summary>
        public static GameJoltHttpServer GameJoltServer { get; private set; }
        #endregion

        /// <summary>
        /// Entry point for the server.
        /// </summary>
        public static void Start(string directory)
        {
            try
            {
                // Load configuration & logger
                Setting = new Setting(directory);
                Logger = new LoggerCollection();
                Logger.Start();

                if (!Setting.Load())
                {
                    Setting.Save();
                    Console.WriteLine("[Core] Settings created. Please configure before running again.");
                    Environment.Exit(0);
                    return;
                }

                Setting.Save();
                Console.WriteLine("[Core] Settings loaded and verified.");

                // Optional updater
                if (Setting.CheckForUpdate)
                {
                    Updater = new Updater();
                    Updater.Update();
                }

                // --- Start GameJolt HTTP service (port via env var or default 8080) ---
                int httpPort = 8080;
                try
                {
                    string env = Environment.GetEnvironmentVariable("P3D_GJ_HTTP_PORT");
                    if (!string.IsNullOrWhiteSpace(env) &&
                        int.TryParse(env, out var parsed) &&
                        parsed > 0 && parsed < 65536)
                    {
                        httpPort = parsed;
                    }
                }
                catch
                {
                    // ignore and keep default
                }

                GameJoltServer = new GameJoltHttpServer(httpPort);
                LogActivity($"GameJolt HTTP online at http://localhost:{httpPort}/ (site + API)");

                // Launch Pokémon 3D server
                if (Setting.MainEntryPoint == Setting.MainEntryPointType.jianmingyong_Server)
                {
                    Listener = new Server_Client_Listener.Servers.Listener();
                    Listener.Start();
                    Console.WriteLine("[Core] Main listener started.");

                    // Start RCON if enabled
                    if (Setting.RCONEnable)
                    {
                        RCONListener = new RCON_Client_Listener.Servers.Listener();
                        RCONListener.Start();
                        Console.WriteLine("[RCON] Listener started successfully.");
                    }
                }

                // Initialize commands
                Command = new CommandCollection();
                Command.AddCommand();

                Console.WriteLine("[Core] Server initialization complete.");
            }
            catch (Exception ex)
            {
                ex.CatchError();
            }
        }

        /// <summary>
        /// Stops and disposes all running background services.
        /// </summary>
        public static void Dispose()
        {
            try
            {
                Listener?.Dispose();
                RCONListener?.Dispose();
                Logger?.Dispose();

                try { GameJoltServer?.Stop(); } catch { /* ignore */ }

                Console.WriteLine("[Core] Server shutdown complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Core] Dispose error: " + ex.Message);
            }
        }

        /// <summary>
        /// Write an activity line to both Logger (if available) and console, for real-time visibility.
        /// Use this for user registrations, logins, joins, disconnects, etc.
        /// </summary>
        public static void LogActivity(string message)
        {
            string consoleLine = $"[{DateTime.Now:G}] [Activity] {message}";
            try
            {
                // Mirror into the existing logger as Info
                Logger?.Log(message, Server_Client_Listener.Loggers.Logger.LogTypes.Info);
            }
            catch
            {
                // ignore logger errors
            }

            // Always echo to console for live server visibility
            Console.WriteLine(consoleLine);
        }
    }
}

#pragma warning restore 1591