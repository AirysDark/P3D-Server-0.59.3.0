#pragma warning disable 1591

using System;
using System.IO;
using Newtonsoft.Json;
using Pokemon_3D_Server_Core.Server_Client_Listener.Commands;
using Pokemon_3D_Server_Core.Server_Client_Listener.Loggers;
using Pokemon_3D_Server_Core.Server_Client_Listener.Settings;
using Pokemon_3D_Server_Core.Shared.jianmingyong;
using Pokemon_3D_Server_Core.Shared.jianmingyong.Modules;
using Pokemon_3D_Server_Core.GameJolt;

namespace Pokemon_3D_Server_Core
{
    public class Core
    {
        public static Setting Setting { get; private set; }
        public static ServerIdentity Identity { get; private set; }
        public static LoggerCollection Logger { get; private set; }
        public static Updater Updater { get; private set; }
        public static Server_Client_Listener.Servers.Listener Listener { get; private set; }
        public static RCON_Client_Listener.Servers.Listener RCONListener { get; private set; }
        public static CommandCollection Command { get; private set; }

        public static Server_Client_Listener.Players.PlayerCollection Player { get; } =
            new Server_Client_Listener.Players.PlayerCollection();
        public static Server_Client_Listener.Worlds.World World { get; } =
            new Server_Client_Listener.Worlds.World();
        public static RCON_Client_Listener.Players.PlayerCollection RCONPlayer { get; } =
            new RCON_Client_Listener.Players.PlayerCollection();
        public static RCON_Client_Listener.Uploader.UploaderQueue RCONUploadQueue { get; } =
            new RCON_Client_Listener.Uploader.UploaderQueue();
        public static RCON_GUI_Client_Listener.Servers.Listener RCONGUIListener { get; set; }
        public static RCON_GUI_Client_Listener.Downloader.DownloaderQueue RCONGUIDownloadQueue { get; } =
            new RCON_GUI_Client_Listener.Downloader.DownloaderQueue();

        /// <summary>
        /// Legacy GameJolt-compatible HTTP implementation exposed through a project-neutral ServerLogin identity.
        /// </summary>
        public static GameJoltHttpServer ServerLogin { get; private set; }

        /// <summary>
        /// Compatibility alias retained for existing integrations.
        /// </summary>
        public static GameJoltHttpServer GameJoltServer => ServerLogin;

        public static void Start(string directory)
        {
            try
            {
                Identity = ServerIdentity.Load(directory);
                Setting = new Setting(directory);
                Logger = new LoggerCollection();
                Logger.Start();

                Console.WriteLine($"[Core] Starting {Identity.ProductName} ({Identity.ServerName}) using protocol profile '{Identity.ProtocolProfile}'.");

                if (!Setting.Load())
                {
                    Setting.Save();
                    Console.WriteLine("[Core] Settings created. Please configure before running again.");
                    Environment.Exit(0);
                    return;
                }

                Setting.Save();
                Console.WriteLine("[Core] Settings loaded and verified.");

                if (Identity.UpdaterEnabled && Setting.CheckForUpdate)
                {
                    Updater = new Updater();
                    Updater.Update();
                }
                else
                {
                    Console.WriteLine("[Core] Updater disabled by server.identity.json or legacy settings.");
                }

                if (Identity.GameJoltCompatibilityEnabled)
                {
                    ServerLogin = new GameJoltHttpServer(Identity.LoginServicePort, Identity.LoginServiceName);
                    LogActivity($"{Identity.LoginServiceName} online at http://localhost:{Identity.LoginServicePort}/ (legacy GameJolt-compatible API enabled)");
                }
                else
                {
                    LogActivity($"{Identity.LoginServiceName} compatibility service is disabled.");
                }

                if (Setting.MainEntryPoint == Setting.MainEntryPointType.jianmingyong_Server)
                {
                    Listener = new Server_Client_Listener.Servers.Listener();
                    Listener.Start();
                    Console.WriteLine("[Core] Main listener started.");

                    if (Setting.RCONEnable)
                    {
                        RCONListener = new RCON_Client_Listener.Servers.Listener();
                        RCONListener.Start();
                        Console.WriteLine("[RCON] Listener started successfully.");
                    }
                }

                Command = new CommandCollection();
                Command.AddCommand();
                Console.WriteLine("[Core] Server initialization complete.");
            }
            catch (Exception ex)
            {
                ex.CatchError();
            }
        }

        public static void Dispose()
        {
            try
            {
                Listener?.Dispose();
                RCONListener?.Dispose();
                Logger?.Dispose();
                try { ServerLogin?.Stop(); } catch { }
                Console.WriteLine("[Core] Server shutdown complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Core] Dispose error: " + ex.Message);
            }
        }

        public static void LogActivity(string message)
        {
            string consoleLine = $"[{DateTime.Now:G}] [Activity] {message}";
            try { Logger?.Log(message, Server_Client_Listener.Loggers.Logger.LogTypes.Info); }
            catch { }
            Console.WriteLine(consoleLine);
        }
    }

    /// <summary>
    /// Central project-neutral identity and optional service configuration.
    /// Stored in server.identity.json next to the server configuration.
    /// </summary>
    public sealed class ServerIdentity
    {
        public string ServerName { get; set; } = "2D-3D Style Server";
        public string ProductName { get; set; } = "2D-3D-style.engine Server";
        public string ProtocolProfile { get; set; } = "legacy-p3d";
        public bool GameJoltCompatibilityEnabled { get; set; } = true;
        public bool UpdaterEnabled { get; set; } = true;
        public string UpdateManifestSource { get; set; } = string.Empty;
        public string LoginServiceName { get; set; } = "ServerLogin";
        public int LoginServicePort { get; set; } = 8080;
        public bool SwearFilterExternalSourceEnabled { get; set; } = false;
        public string SwearFilterSource { get; set; } = string.Empty;

        public static ServerIdentity Load(string directory)
        {
            var path = Path.Combine(directory ?? string.Empty, "server.identity.json");
            var identity = new ServerIdentity();
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<ServerIdentity>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        loaded.Normalize();
                        return loaded;
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                    File.WriteAllText(path, JsonConvert.SerializeObject(identity, Formatting.Indented));
                }
            }
            catch { }
            identity.Normalize();
            return identity;
        }

        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ServerName)) ServerName = "2D-3D Style Server";
            if (string.IsNullOrWhiteSpace(ProductName)) ProductName = "2D-3D-style.engine Server";
            if (string.IsNullOrWhiteSpace(ProtocolProfile)) ProtocolProfile = "legacy-p3d";
            if (string.IsNullOrWhiteSpace(LoginServiceName)) LoginServiceName = "ServerLogin";
            if (LoginServicePort < 1 || LoginServicePort > 65535) LoginServicePort = 8080;
        }
    }
}

#pragma warning restore 1591