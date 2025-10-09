using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpCompress.Archives;       // ? new API
using SharpCompress.Common;         // ? ExtractionOptions
using System.Linq;                  // ? for .Where(...)

namespace Pokemon_3D_Server_Client_Updater
{
    /// <summary>
    /// Class containing the Main Access point.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main Access Point.
        /// </summary>
        /// <param name="args">Program Start Argument.</param>
        public static void Main(string[] args)
        {
            // Allow parent process to exit / release files
            Thread.Sleep(5000);

            if (args.Length > 0)
            {
                // Decode any %20 etc. and normalize the directory path
                var baseDir = Uri.UnescapeDataString(args[0]);
                if (Directory.Exists(baseDir))
                {
                    var zipPath = Path.Combine(baseDir, "Pokemon.3D.Server.Client.GUI.zip");
                    var exePath = Path.Combine(baseDir, "Pokemon.3D.Server.Client.GUI.exe");
                    var extractTo = baseDir;

                    try
                    {
                        if (File.Exists(zipPath))
                        {
                            // Open the archive (auto-detects type)
                            using (var archive = ArchiveFactory.Open(zipPath))
                            {
                                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                                {
                                    // Extract preserving folder structure; overwrite existing files
                                    entry.WriteToDirectory(extractTo, new ExtractionOptions
                                    {
                                        ExtractFullPath = true,
                                        Overwrite = true
                                    });
                                }
                            }

                            // Clean up the zip after successful extraction
                            try { File.Delete(zipPath); } catch { /* ignore */ }
                        }
                    }
                    catch
                    {
                        // Swallow per original behavior; keep going to try launching
                    }

                    // Launch the client GUI regardless (original behavior)
                    try
                    {
                        Process.Start(exePath);
                    }
                    catch
                    {
                        // Ignore launch failure to mirror original silent handling
                    }
                }
            }
        }
    }
}