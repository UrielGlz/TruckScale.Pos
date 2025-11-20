using System;
using System.IO;
using System.Text.Json;
using TruckScale.Pos.Security;

namespace TruckScale.Pos.Config
{
    public static class ConfigManager
    {
        private static readonly string ConfigFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "TruckScale");

        private static readonly string ConfigPath =
            Path.Combine(ConfigFolder, "config.json");

        public static AppConfig Current { get; private set; } = new();

        public static void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Current = new AppConfig();
                    return;
                }

                var json = File.ReadAllText(ConfigPath);
                var raw = JsonSerializer.Deserialize<RawConfig>(json) ?? new RawConfig();

                Current = new AppConfig
                {
                    MainDbStrCon = string.IsNullOrWhiteSpace(raw.main_db_str_con)
                        ? ""
                        : CryptoHelper.Unprotect(raw.main_db_str_con),
                    LocalDbStrCon = string.IsNullOrWhiteSpace(raw.local_db_str_con)
                        ? ""
                        : CryptoHelper.Unprotect(raw.local_db_str_con),
                };
            }
            catch
            {
                // Si hay error al leer, mejor dejarlo vacío y que el usuario configure
                Current = new AppConfig();
            }
        }

        public static void Save(string mainPlain, string localPlain)
        {
            Directory.CreateDirectory(ConfigFolder);

            var raw = new RawConfig
            {
                main_db_str_con = string.IsNullOrWhiteSpace(mainPlain)
                    ? ""
                    : CryptoHelper.Protect(mainPlain),
                local_db_str_con = string.IsNullOrWhiteSpace(localPlain)
                    ? ""
                    : CryptoHelper.Protect(localPlain),
            };

            var json = JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);

            Current.MainDbStrCon = mainPlain;
            Current.LocalDbStrCon = localPlain;
        }

        public static bool HasMainConnection =>
            !string.IsNullOrWhiteSpace(Current.MainDbStrCon);

        public static bool HasLocalConnection =>
            !string.IsNullOrWhiteSpace(Current.LocalDbStrCon);
    }
}
