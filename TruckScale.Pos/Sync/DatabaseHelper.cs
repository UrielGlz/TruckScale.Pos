using System;
using System.Threading.Tasks;
using MySqlConnector;

namespace TruckScale.Pos.Sync
{
    // ============================================================================
    // DATABASE HELPER
    // ============================================================================
    // Maneja el patrón de fallback: MAIN_DB → LOCAL_DB
    // Si falla la conexión principal, automáticamente usa la local.
    // Registra errores en sync_logs para posterior sincronización.
    // 
    // Uso típico:
    //   var result = await DatabaseHelper.ExecuteWithFallbackAsync(
    //       mainConn, localConn, 
    //       async (conn) => { /* tu operación */ }
    //   );
    // 
    // Creado: 2024-12
    // Autor: Development Team
    // ============================================================================

    public static class DatabaseHelper
    {
        /// <summary>
        /// Ejecuta una operación de BD con fallback automático.
        /// 1. Intenta usar MAIN_DB (online)
        /// 2. Si falla, usa LOCAL_DB (offline)
        /// 3. Marca queue_status = DIRTY si usa local
        /// 4. Registra el error en sync_logs
        /// </summary>
        /// <typeparam name="T">Tipo de resultado de la operación</typeparam>
        /// <param name="mainConnStr">Cadena de conexión a MAIN_DB</param>
        /// <param name="localConnStr">Cadena de conexión a LOCAL_DB</param>
        /// <param name="operation">Función que ejecuta la operación de BD</param>
        /// <returns>Tupla (resultado, usedLocal)</returns>
        public static async Task<(T result, bool usedLocal)> ExecuteWithFallbackAsync<T>(
            string mainConnStr,
            string localConnStr,
            Func<MySqlConnection, Task<T>> operation)
        {
            // === INTENTO 1: MAIN_DB (online) ===
            try
            {
                await using var conn = new MySqlConnection(mainConnStr);
                await conn.OpenAsync();

                var result = await operation(conn);
                return (result, usedLocal: false);
            }
            catch (Exception exMain)
            {
                // Log del error (no bloqueante)
                try
                {
                    await LogConnectionErrorAsync(localConnStr, "MAIN_DB", exMain.Message);
                }
                catch { /* Si falla el log, seguimos */ }

                // === INTENTO 2: LOCAL_DB (fallback) ===
                try
                {
                    await using var conn = new MySqlConnection(localConnStr);
                    await conn.OpenAsync();

                    var result = await operation(conn);

                    // Marcar queue como DIRTY (hay datos locales sin subir)
                    await SetQueueDirtyAsync(conn);

                    return (result, usedLocal: true);
                }
                catch (Exception exLocal)
                {
                    // Ambas BDs fallaron → propagar el error
                    throw new InvalidOperationException(
                        $"Failed to connect to both MAIN_DB and LOCAL_DB. " +
                        $"Main: {exMain.Message}. Local: {exLocal.Message}",
                        exLocal);
                }
            }
        }

        /// <summary>
        /// Marca la cola de sincronización como DIRTY.
        /// Esto indica que hay transacciones locales pendientes de subir a MAIN_DB.
        /// </summary>
        private static async Task SetQueueDirtyAsync(MySqlConnection conn)
        {
            const string SQL = @"
                INSERT INTO settings (site_id, `key`, value, is_active)
                VALUES (1, 'sync.queue_status', 'DIRTY', 1)
                ON DUPLICATE KEY UPDATE 
                    value = 'DIRTY',
                    updated_at = NOW();";

            try
            {
                await using var cmd = new MySqlCommand(SQL, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // No bloqueamos la operación principal si falla esto
            }
        }

        /// <summary>
        /// Registra un error de conexión en sync_logs (LOCAL_DB).
        /// Esto permite auditar cuándo y por qué se usó el modo offline.
        /// </summary>
        private static async Task LogConnectionErrorAsync(
            string localConnStr,
            string dbName,
            string errorMsg)
        {
            const string SQL = @"
                INSERT INTO sync_logs 
                    (log_uid, log_type, severity, message, created_at)
                VALUES 
                    (@uid, 'CONNECTION_ERROR', 'ERROR', @msg, NOW());";

            try
            {
                await using var conn = new MySqlConnection(localConnStr);
                await conn.OpenAsync();

                await using var cmd = new MySqlCommand(SQL, conn);
                cmd.Parameters.AddWithValue("@uid", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@msg", $"{dbName} connection failed: {errorMsg}");
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Si no podemos loguear, no rompemos el flujo
            }
        }

        /// <summary>
        /// Obtiene el estado actual de la cola de sincronización.
        /// </summary>
        public static async Task<QueueStatus> GetQueueStatusAsync(string connStr)
        {
            const string SQL = @"
                SELECT value 
                FROM settings 
                WHERE site_id = 1 
                  AND `key` = 'sync.queue_status' 
                LIMIT 1;";

            try
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                await using var cmd = new MySqlCommand(SQL, conn);
                var result = await cmd.ExecuteScalarAsync();

                var status = result?.ToString()?.ToUpperInvariant();
                return status == "DIRTY" ? QueueStatus.DIRTY : QueueStatus.FREE;
            }
            catch
            {
                // Si no podemos leer, asumimos FREE (no bloqueamos la app)
                return QueueStatus.FREE;
            }
        }

        /// <summary>
        /// Actualiza el estado de la cola de sincronización.
        /// </summary>
        public static async Task UpdateQueueStatusAsync(
            string connStr,
            QueueStatus status)
        {
            const string SQL = @"
                INSERT INTO settings (site_id, `key`, value, is_active)
                VALUES (1, 'sync.queue_status', @status, 1)
                ON DUPLICATE KEY UPDATE 
                    value = @status,
                    updated_at = NOW();";

            try
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                await using var cmd = new MySqlCommand(SQL, conn);
                cmd.Parameters.AddWithValue("@status", status.ToString());
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // No bloqueamos si falla (el timer lo reintentará)
            }
        }
    }
}