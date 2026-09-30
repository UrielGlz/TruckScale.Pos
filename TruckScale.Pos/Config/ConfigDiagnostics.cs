using System;
using System.IO;
using MySqlConnector;

namespace TruckScale.Pos.Config
{
    /// <summary>
    /// Escribe en el log de arranque qué configuración se leyó, de qué ruta y a qué servidor/base
    /// apunta cada conexión. Nunca escribe contraseñas: solo servidor, puerto, base y usuario.
    /// </summary>
    public static class ConfigDiagnostics
    {
        /// <summary>
        /// "server=10.0.0.5 port=3306 db=truckscale user=pos" a partir de una cadena de conexión.
        /// </summary>
        public static string Describe(string? connStr)
        {
            if (string.IsNullOrWhiteSpace(connStr))
                return "(vacia)";
            try
            {
                var b = new MySqlConnectionStringBuilder(connStr);
                return $"server={b.Server} port={b.Port} db={b.Database} user={b.UserID}";
            }
            catch (Exception ex)
            {
                return $"(no se pudo interpretar la cadena: {ex.GetType().Name})";
            }
        }

        private static string FileInfoText(string path)
        {
            try
            {
                return File.Exists(path)
                    ? $"existe=si, modificado={File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}"
                    : "existe=NO";
            }
            catch (Exception ex)
            {
                return $"existe=? ({ex.GetType().Name})";
            }
        }

        /// <summary>
        /// Una vez por arranque. No lanza excepciones.
        /// </summary>
        public static void LogStartup()
        {
            try
            {
                var version = typeof(ConfigDiagnostics).Assembly.GetName().Version;
                AppLogger.Log($"[Config] ===== Arranque TruckScale.Pos v{version} | exe dir={AppContext.BaseDirectory} | usuario Windows={Environment.UserName} =====");

                // 1) Config real (Ctrl+Alt+F8): C:\ProgramData\TruckScale\config.json
                ConfigManager.Load();
                var cfg = ConfigManager.Current;
                var path = ConfigManager.ConfigFilePath;
                AppLogger.Log($"[Config] config.json principal: {path} | {FileInfoText(path)}" +
                              (ConfigManager.LastLoadError != null ? $" | ERROR al leer: {ConfigManager.LastLoadError}" : ""));
                AppLogger.Log($"[Config]   MAIN : {Describe(cfg.MainDbStrCon)}  (el POS fuerza ConnectionTimeout=8 al usarla)");
                AppLogger.Log($"[Config]   LOCAL: {Describe(cfg.LocalDbStrCon)}");
                if (File.Exists(path) && string.IsNullOrWhiteSpace(cfg.MainDbStrCon) && string.IsNullOrWhiteSpace(cfg.LocalDbStrCon))
                    AppLogger.Log("[Config]   AVISO: el archivo existe pero no se obtuvo ninguna cadena (JSON danado o cifrado DPAPI de otro usuario o PC).");

                // 2) Config legado (PosConfigService): <carpeta del exe>\config.json
                var legacyPath = PosConfigService.LegacyConfigPath;
                var legacy = PosConfigService.Load();
                bool legacyHasLocal = !string.IsNullOrWhiteSpace(legacy.LocalDbStrCon);
                AppLogger.Log($"[Config] config.json legado (PosConfigService): {legacyPath} | {FileInfoText(legacyPath)}");
                AppLogger.Log($"[Config]   GetLocalConn() (solo intervalo del timer de sync y fallback de tipos de vehiculo) -> " +
                              (legacyHasLocal
                                  ? $"legado: {Describe(legacy.LocalDbStrCon)}"
                                  : $"CADENA FIJA en codigo: {Describe(PosConfigService.LegacyLocalFallback)}"));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Config] No se pudo escribir el diagnostico de arranque: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
