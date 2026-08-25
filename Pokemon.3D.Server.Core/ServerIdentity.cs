using System;
using System.IO;
using Newtonsoft.Json;

namespace Pokemon_3D_Server_Core
{
    /// <summary>
    /// Central, project-neutral identity and optional service configuration.
    /// This keeps user-facing server identity out of the legacy P3D protocol layer.
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

        [JsonIgnore]
        public string ConfigurationPath { get; private set; }

        public static ServerIdentity Load(string directory)
        {
            var path = Path.Combine(directory ?? string.Empty, "server.identity.json");
            var identity = new ServerIdentity();
            identity.ConfigurationPath = path;

            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = JsonConvert.DeserializeObject<ServerIdentity>(json);
                    if (loaded != null)
                    {
                        loaded.ConfigurationPath = path;
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
            catch
            {
                // Keep the server usable with safe defaults if optional identity config cannot be read.
            }

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
