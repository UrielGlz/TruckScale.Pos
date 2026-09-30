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

        /// <summary>Ruta del config.json real (C:\ProgramData\TruckScale\config.json).</summary>
        public static string ConfigFilePath => ConfigPath;

        /// <summary>Error del último Load() (null si leyó bien o si el archivo no existe). Solo diagnóstico.</summary>
        public static string? LastLoadError { get; private set; }

        public static void Load()
        {
            LastLoadError = null;
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
                    // NEW – si en el JSON no vienen, tomamos defaults
                    TicketPrinterName = raw.ticket_printer_name ?? "",
                    TicketLandscape = raw.ticket_landscape ?? true,
                    TicketMarginInches = raw.ticket_margin_inches ?? 0.25,
                    LogRetentionDays = raw.log_retention_days is int d && d > 0
                        ? d
                        : AppConfig.DefaultLogRetentionDays
                };
            }
            catch (Exception ex)
            {
                // Si hay error al leer, mejor dejarlo vacío y que el admin configure
                // (se conserva el comportamiento; el error queda disponible para el log de arranque)
                LastLoadError = $"{ex.GetType().Name}: {ex.Message}";
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
                // NEW: se guardan los valores que ya estén en Current
                ticket_printer_name = Current.TicketPrinterName,
                ticket_landscape = Current.TicketLandscape,
                ticket_margin_inches = Current.TicketMarginInches,
                log_retention_days = Current.LogRetentionDays
            };

            var json = JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);

            Current.MainDbStrCon = mainPlain;
            Current.LocalDbStrCon = localPlain;
        }

        // Helper para guardar solo la impresora
        public static void SavePrinter(string printerName)
        {
            Current.TicketPrinterName = printerName ?? "";
            Save(Current.MainDbStrCon, Current.LocalDbStrCon);
        }
        public static bool HasMainConnection =>
            !string.IsNullOrWhiteSpace(Current.MainDbStrCon);

        public static bool HasLocalConnection =>
            !string.IsNullOrWhiteSpace(Current.LocalDbStrCon);
    }
}
