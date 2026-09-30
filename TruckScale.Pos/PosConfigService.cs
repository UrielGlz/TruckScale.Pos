using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TruckScale.Pos
{
    public sealed class PosConfig
    {
        public string? MainDbStrCon { get; set; }
        public string? LocalDbStrCon { get; set; }
    }

    /// <summary>
    /// LEGADO. Lee un config.json JUNTO AL .EXE (no el de C:\ProgramData\TruckScale).
    /// Nadie escribe ese archivo (Save no tiene llamadores). Hoy solo lo usan:
    ///   - LoginWindow_Loaded (asigna _dbConfigured, que nunca se lee)
    ///   - MainWindow.GetLocalConn() (intervalo del timer de sync y fallback de tipos de vehículo)
    /// Si el archivo no existe, GetLocalConn usa LegacyLocalFallback (cadena fija).
    /// Ver docs/bugs-pos.md, sección "Configuración: hay DOS config.json".
    /// </summary>
    public static class PosConfigService
    {
        private static readonly string ConfigPath =
            Path.Combine(AppContext.BaseDirectory, "config.json");

        /// <summary>Ruta del config.json legado (carpeta del ejecutable).</summary>
        public static string LegacyConfigPath => ConfigPath;

        /// <summary>Cadena fija que usa MainWindow.GetLocalConn() cuando el config legado no da LOCAL.</summary>
        public const string LegacyLocalFallback =
            "Server=127.0.0.1;Port=3306;Database=truckscale;Uid=localuser;Pwd=localpass;SslMode=None;";

        private const string MAIN_KEY = "main_db_str_con";
        private const string LOCAL_KEY = "local_db_str_con";

        // ===== Cargar config.json =====
        public static PosConfig Load()
        {
            if (!File.Exists(ConfigPath))
                return new PosConfig();

            try
            {
                string json = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? mainEnc = root.TryGetProperty(MAIN_KEY, out var m) ? m.GetString() : null;
                string? localEnc = root.TryGetProperty(LOCAL_KEY, out var l) ? l.GetString() : null;

                return new PosConfig
                {
                    MainDbStrCon = DecryptFromBase64(mainEnc),
                    LocalDbStrCon = DecryptFromBase64(localEnc)
                };
            }
            catch
            {
                return new PosConfig();
            }
        }

        // ===== Guardar config.json =====
        public static void Save(PosConfig cfg)
        {
            var payload = new
            {
                main_db_str_con = EncryptToBase64(cfg.MainDbStrCon),
                local_db_str_con = EncryptToBase64(cfg.LocalDbStrCon)
            };

            var json = JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(ConfigPath, json);
        }

        // ===== Helpers DPAPI + Base64 =====
        private static string? EncryptToBase64(string? plain)
        {
            if (string.IsNullOrWhiteSpace(plain))
                return null;

            byte[] data = Encoding.UTF8.GetBytes(plain);
            byte[] protectedBytes = ProtectedData.Protect(
                data, null, DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(protectedBytes);
        }

        private static string? DecryptFromBase64(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return null;

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(base64);
                byte[] clear = ProtectedData.Unprotect(
                    protectedBytes, null, DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(clear);
            }
            catch
            {
                return null;
            }
        }
    }
}
