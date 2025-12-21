using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MySqlConnector;
using TruckScale.Pos.Config;
namespace TruckScale.Pos.Sync
{
    // ============================================================================
    // SYNC SERVICE
    // ============================================================================
    // Motor principal de sincronización LOCAL_DB → MAIN_DB.
    // 
    // Responsabilidades:
    // 1. Detectar transacciones pendientes (synced = 0)
    // 2. Subirlas a MAIN_DB en el orden correcto (respetando FKs)
    // 3. Marcarlas como sincronizadas (synced = 1)
    // 4. Actualizar queue_status (DIRTY → FREE)
    // 5. Registrar logs de éxito/error
    // 
    // Orden de sincronización (respeta dependencias):
    //   1. scale_session_axles (sin FKs)
    //   2. sales (sin FKs en transacciones)
    //   3. sale_lines (FK → sales)
    //   4. sale_driver_info (FK → sales)
    //   5. payments (FK → sales)
    //   6. tickets (FK → sales)
    //   7. sync_logs (logs de sincronización)
    // 
    // Uso típico:
    //   var service = new SyncService(mainConn, localConn);
    //   var result = await service.PushTransactionsAsync();
    //   if (result.Success) { /* todo bien */ }
    // 
    // Creado: 2024-12
    // Autor:UG
    // ============================================================================

    public sealed class SyncService
    {
        private readonly string _mainConnStr;
        private readonly string _localConnStr;
        private readonly int _batchSize;

        // Ahora el servicio se configura SOLO desde config.json
        public SyncService(int batchSize = 50)
        {
            // Aseguramos que la config esté cargada
            ConfigManager.Load();

            var primaryConn = ConfigManager.Current.MainDbStrCon;
            var localConn = ConfigManager.Current.LocalDbStrCon;

            if (string.IsNullOrWhiteSpace(primaryConn))
                throw new InvalidOperationException("MainDbStrCon is not configured in config.json.");

            if (string.IsNullOrWhiteSpace(localConn))
                throw new InvalidOperationException("LocalDbStrCon is not configured in config.json.");

            _mainConnStr = primaryConn;
            _localConnStr = localConn;
            _batchSize = batchSize;
        }
        // Helpers para crear conexiones con AllowZeroDateTime/ConvertZeroDateTime
        private MySqlConnection CreateLocalConnection()
        {
            var csb = new MySqlConnectionStringBuilder(_localConnStr)
            {
                AllowZeroDateTime = true,
                ConvertZeroDateTime = true
            };
            return new MySqlConnection(csb.ConnectionString);
        }
        private MySqlConnection CreateMainConnection()
        {
            var csb = new MySqlConnectionStringBuilder(_mainConnStr)
            {
                AllowZeroDateTime = true,
                ConvertZeroDateTime = true
            };
            return new MySqlConnection(csb.ConnectionString);
        }
        // ========================================================================
        // API PÚBLICA
        // ========================================================================

        /// <summary>
        /// Sincroniza todas las transacciones pendientes de LOCAL_DB a MAIN_DB.
        /// Respeta el orden de dependencias de las tablas (FKs).
        /// Solo se ejecuta si queue_status = DIRTY.
        /// </summary>
        public async Task<SyncResult> PushTransactionsAsync()
        {
            var stats = new SyncStats { StartTime = DateTime.UtcNow };

            try
            {
                // 1. Verificar si hay algo que sincronizar
                var queueInfo = await GetQueueInfoAsync();
                //if (queueInfo.Status == QueueStatus.FREE || queueInfo.TotalPending == 0)
                //{
                //    return SyncResult.Ok(0, "Queue is FREE, nothing to sync.");
                //}
                
                // El Status FREE ya no bloquea el push si hay registros con synced = 0.
                if (queueInfo.TotalPending == 0)
                {
                    return SyncResult.Ok(0, "Nothing to sync (queue empty).");
                }

                // 2. Abrir conexiones
                //await using var localConn = new MySqlConnection(_localConnStr);
                //await using var mainConn = new MySqlConnection(_mainConnStr);
                await using var localConn = CreateLocalConnection();
                await using var mainConn = CreateMainConnection();

                await localConn.OpenAsync();
                await mainConn.OpenAsync();

                // 3. Sincronizar en orden (respetando FKs)
                stats.AxlesSynced = await SyncTableAsync(localConn, mainConn, "scale_session_axles", "uuid_weight");
                stats.SalesSynced = await SyncTableAsync(localConn, mainConn, "sales", "sale_uid");
                stats.LinesSynced = await SyncTableAsync(localConn, mainConn, "sale_lines", "line_id");
                stats.DriversSynced = await SyncTableAsync(localConn, mainConn, "sale_driver_info", "id_driver_info");
               
                stats.TicketsSynced = await SyncTableAsync(localConn, mainConn, "tickets", "ticket_uid");
                stats.LogsSynced = await SyncTableAsync(localConn, mainConn, "sync_logs", "log_uid");
                stats.WhateverSynced += await SyncTableAsync(localConn, mainConn, "customer_credit", "credit_id");
                
                stats.SecuencesSynced += await SyncTableAsync(localConn, mainConn, "number_sequences", "sequence_id");
                stats.PaymentsSynced = await SyncTableAsync(localConn, mainConn, "payments", "payment_uid");


                // 4. Actualizar queue_status a FREE
                await DatabaseHelper.UpdateQueueStatusAsync(_localConnStr, QueueStatus.FREE);

                // 5. Registrar timestamp de éxito
                await UpdateLastSyncTimestampAsync(localConn, "sync.last_push_success");

                stats.EndTime = DateTime.UtcNow;

                // 6. Log de éxito
                await LogSyncEventAsync(
                    "PUSH_SUCCESS",
                    $"Synced {stats.TotalSynced} records in {stats.Duration.TotalSeconds:0.0}s",
                    stats.TotalSynced);

                return SyncResult.Ok(stats.TotalSynced,
                    $"Successfully synced {stats.TotalSynced} records.");
            }
            catch (Exception ex)
            {
                stats.Errors.Add(ex.Message);
                stats.EndTime = DateTime.UtcNow;

                // Log de error
                await LogSyncEventAsync("PUSH_ERROR", ex.Message, 0);

                return SyncResult.Fail($"Sync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtiene información detallada del estado de la cola.
        /// Útil para mostrar en la UI o para debugging.
        /// </summary>
        public async Task<QueueInfo> GetQueueInfoAsync()
        {
            var info = new QueueInfo
            {
                Status = await DatabaseHelper.GetQueueStatusAsync(_localConnStr)
            };

            try
            {
                await using var conn = CreateLocalConnection();
                await conn.OpenAsync();

                info.PendingAxles = await CountPendingAsync(conn, "scale_session_axles");
                info.PendingSales = await CountPendingAsync(conn, "sales");
               
                info.PendingDrivers = await CountPendingAsync(conn, "sale_driver_info");
                info.PendingTickets = await CountPendingAsync(conn, "tickets");
                info.PendingLogs = await CountPendingAsync(conn, "sync_logs");
                info.PendingSqcuences = await CountPendingAsync(conn, "number_sequences");
                info.PendingPayments = await CountPendingAsync(conn, "payments");


                info.LastPushAttempt = await GetTimestampAsync(conn, "sync.last_push_attempt");
                info.LastPushSuccess = await GetTimestampAsync(conn, "sync.last_push_success");

            }
            catch
            {
                // Si falla, devolvemos lo que tengamos
            }

            return info;
        }

        /// <summary>
        /// Sincroniza catálogos de MAIN_DB → LOCAL_DB.
        /// Útil para mantener actualizadas las listas de productos, clientes, etc.
        /// Se ejecuta periódicamente o manualmente.
        /// </summary>
        public async Task<SyncResult> SyncCatalogsAsync()
        {
            try
            {
                await using var mainConn = CreateMainConnection();
                await using var localConn = CreateLocalConnection();

                await mainConn.OpenAsync();
                await localConn.OpenAsync();

                // Catálogos "clásicos" → full refresh (sin filtro de fecha)
                DateTime? lastTs = null;
                int total = 0;

                total += await PullTableUpsertAsync(mainConn, localConn, "customers", "id_customer", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "products", "product_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "payment_methods", "method_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "license_states", "id_state", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "driver_products", "product_id", lastTs);

                await UpdateLastSyncTimestampAsync(localConn, "sync.last_catalog_sync");

                return SyncResult.Ok(total, $"Catalogs synced: {total} records.");
            }
            catch (Exception ex)
            {
                return SyncResult.Fail($"Catalog sync failed: {ex.Message}");
            }
        }


        // ========================================================================
        // NÚCLEO: Sincronización tabla por tabla
        // ========================================================================

        /// <summary>
        /// Sincroniza UNA tabla de LOCAL → MAIN.
        /// Usa INSERT IGNORE para evitar duplicados (los UIDs garantizan unicidad).
        /// Marca los registros sincronizados (synced = 1).
        /// </summary>
        private async Task<int> SyncTableAsync(
            MySqlConnection localConn,
            MySqlConnection mainConn,
            string tableName,
            string pkColumn)
        {
            // 1. Obtener registros pendientes (synced = 0)
            var pendingUids = await GetPendingUidsAsync(localConn, tableName, pkColumn);
            if (pendingUids.Count == 0)
                return 0;

            // 2. Procesar en lotes (para no saturar memoria)
            int totalSynced = 0;
            foreach (var batch in pendingUids.Chunk(_batchSize))
            {
                var synced = await SyncBatchAsync(localConn, mainConn, tableName, pkColumn, batch);
                totalSynced += synced;
            }

            return totalSynced;
        }

        /// <summary>
        /// Sincroniza un lote de registros.
        /// 1. INSERT IGNORE en MAIN_DB
        /// 2. UPDATE synced = 1 en LOCAL_DB
        /// </summary>
        private async Task<int> SyncBatchAsync(
            MySqlConnection localConn,
            MySqlConnection mainConn,
            string tableName,
            string pkColumn,
            string[] uids)
        {
            if (uids.Length == 0) return 0;

            // Generar placeholders para IN clause: @p0, @p1, @p2...
            var placeholders = string.Join(",", Enumerable.Range(0, uids.Length).Select(i => $"@p{i}"));

            // === PASO 1: Leer datos de LOCAL_DB ===
            var sqlSelect = $"SELECT * FROM {tableName} WHERE {pkColumn} IN ({placeholders});";
            var rows = new List<Dictionary<string, object?>>();

            await using (var cmd = new MySqlCommand(sqlSelect, localConn))
            {
                for (int i = 0; i < uids.Length; i++)
                    cmd.Parameters.AddWithValue($"@p{i}", uids[i]);

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        var colName = rd.GetName(i);
                        // Excluir campos de sync (no existen en MAIN_DB)
                        if (colName is "synced" or "synced_at" or "sync_attempts" or "last_sync_error")
                            continue;

                        row[colName] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    }
                    rows.Add(row);
                }
            }

            if (rows.Count == 0) return 0;

            // === PASO 2: INSERT IGNORE en MAIN_DB ===
            foreach (var row in rows)
            {
                var columns = string.Join(", ", row.Keys.Select(k => $"`{k}`"));
                var values = string.Join(", ", row.Keys.Select(k => $"@{k}"));
                var sqlInsert = $"INSERT IGNORE INTO {tableName} ({columns}) VALUES ({values});";

                await using var cmd = new MySqlCommand(sqlInsert, mainConn);
                foreach (var kvp in row)
                    cmd.Parameters.AddWithValue($"@{kvp.Key}", kvp.Value ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }

            // === PASO 3: Marcar como sincronizado en LOCAL_DB ===
            var sqlUpdate = $@"
                UPDATE {tableName} 
                SET synced = 1, synced_at = NOW() 
                WHERE {pkColumn} IN ({placeholders});";

            await using (var cmd = new MySqlCommand(sqlUpdate, localConn))
            {
                for (int i = 0; i < uids.Length; i++)
                    cmd.Parameters.AddWithValue($"@p{i}", uids[i]);

                await cmd.ExecuteNonQueryAsync();
            }

            return rows.Count;
        }

        /// <summary>
        /// Obtiene lista de UIDs pendientes de sincronizar (synced = 0).
        /// </summary>
        private async Task<List<string>> GetPendingUidsAsync(
            MySqlConnection conn,
            string tableName,
            string pkColumn)
        {
            var sql = $"SELECT {pkColumn} FROM {tableName} WHERE synced = 0 LIMIT {_batchSize * 10};";
            var uids = new List<string>();

            await using var cmd = new MySqlCommand(sql, conn);
            await using var rd = await cmd.ExecuteReaderAsync();

            while (await rd.ReadAsync())
            {
                var uid = rd.GetValue(0)?.ToString();
                if (!string.IsNullOrWhiteSpace(uid))
                    uids.Add(uid);
            }

            return uids;
        }


        // ========================================================================
        // MAIN → LOCAL (transaccional + catálogos) INCREMENTAL POR updated_at
        // ========================================================================

        /// <summary>
        /// PULL incremental desde MAIN_DB → LOCAL_DB.
        /// Usa updated_at y UPSERT (sin REPLACE).
        /// Se ejecuta después del Push (LOCAL → MAIN).
        /// </summary>
        public async Task<SyncResult> PullUpdatesAsync()
        {
            try
            {
                await using var mainConn = CreateMainConnection();
                await using var localConn = CreateLocalConnection();

                await mainConn.OpenAsync();
                await localConn.OpenAsync();

                // Último pull exitoso
                var lastTs = await GetTimestampAsync(localConn, "sync.last_pull_ts");
                
                // Protección: si sales local está vacía pero tenemos lastTs,
                // forzamos un FULL PULL (sales completa antes de payments).
                if (lastTs.HasValue)
                {
                    const string sqlCheckSales = "SELECT 1 FROM sales LIMIT 1;";
                    await using var checkCmd = new MySqlCommand(sqlCheckSales, localConn);
                    var anySales = await checkCmd.ExecuteScalarAsync();

                    if (anySales is null)
                    {
                        //AppendLog("[Sync] Local.sales está vacía con last_pull_ts definido. Forzando FULL PULL (lastTs = null).");
                        lastTs = null;
                    }
                }

                int total = 0;

                // 1) Tablas padre / catálogos base
                total += await PullTableUpsertAsync(mainConn, localConn, "sites", "site_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "status_catalogo", "status_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "tax_rates", "tax_rate_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "products", "product_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "customers", "id_customer", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "vehicle_types", "vehicle_type_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "driver_products", "product_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "license_states", "id_state", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "payment_methods", "method_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "number_sequences", "sequence_id", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "users", "user_id", lastTs);

                // NUEVO: customer_credit (depende de customers)
                // Como es tabla chica y muy sensible, hacemos full upsert (sin filtro por fecha).
                total += await PullTableUpsertAsync(mainConn, localConn, "customer_credit", "credit_id", null);



                // ⚠ Opcional: settings lleva llaves compuestas y claves 'sync.*'.
                // Si los tienes iguales en MAIN/LOCAL, puedes activar esta línea.
                // Si prefieres no tocarlos aún, déjala comentada.
                // total += await PullTableUpsertAsync(mainConn, localConn, "settings",        "setting_id",     lastTs);

                // 2) Tablas transaccionales (usamos UIDs)
                total += await PullTableUpsertAsync(mainConn, localConn, "sales", "sale_uid", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "sale_driver_info", "id_driver_info", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "sale_lines", "line_id", lastTs);



                total += await PullTableUpsertAsync(mainConn, localConn, "tickets", "ticket_uid", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "scale_session_axles", "uuid_weight", lastTs);
                total += await PullTableUpsertAsync(mainConn, localConn, "sync_logs", "log_uid", lastTs);
                // IMPORTANTE: payments → full upsert (sin depender de updated_at / last_pull_ts)
                total += await PullTableUpsertAsync(mainConn, localConn, "payments", "payment_uid", null);
                total += await PullTableUpsertAsync(mainConn, localConn, "number_sequences", "sequence_id", lastTs);



                // Nuevo timestamp de pull
                await UpdateLastSyncTimestampAsync(localConn, "sync.last_pull_ts");

                await LogSyncEventAsync("PULL_SUCCESS",
                    $"Pull completed. Records upserted: {total}.",
                    total);

                return SyncResult.Ok(total, $"Pull completed. Records upserted: {total}.");
            }
            catch (Exception ex)
            {
                await LogSyncEventAsync("PULL_ERROR", ex.Message, 0);
                return SyncResult.Fail($"Pull failed: {ex.Message}");
            }
        }


        // ========================================================================
        // HELPERS
        // ========================================================================

        /// <summary>
        /// Lee filas de MAIN_DB y hace UPSERT en LOCAL_DB:
        /// INSERT ... ON DUPLICATE KEY UPDATE (sin REPLACE).
        /// Si lastTs es null → full table.
        /// Si lastTs tiene valor → solo updated_at > lastTs.
        /// </summary>
        private async Task<int> PullTableUpsertAsync(
    MySqlConnection mainConn,
    MySqlConnection localConn,
    string tableName,
    string pkColumn,
    DateTime? lastTs)
        {
            var rows = new List<Dictionary<string, object?>>();

            // 0) Saber qué columnas de sync existen en la tabla LOCAL
            var syncCols = await GetLocalSyncColumnsAsync(localConn, tableName);

            // 1. Leer de MAIN (con filtro de updated_at si aplica)
            var where = lastTs.HasValue ? "WHERE updated_at > @ts" : "";
            var sqlSelect = $"SELECT * FROM {tableName} {where};";

            await using (var cmd = new MySqlCommand(sqlSelect, mainConn))
            {
                if (lastTs.HasValue)
                    cmd.Parameters.AddWithValue("@ts", lastTs.Value);

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        var colName = rd.GetName(i);
                        row[colName] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    }

                    // 🔹 Muy importante: si la tabla LOCAL tiene columnas de sync,
                    // marcamos estos registros como "ya sincronizados"
                    if (syncCols.Contains("synced") && !row.ContainsKey("synced"))
                        row["synced"] = 1;

                    if (syncCols.Contains("synced_at") && !row.ContainsKey("synced_at"))
                        row["synced_at"] = DateTime.UtcNow;

                    if (syncCols.Contains("sync_attempts") && !row.ContainsKey("sync_attempts"))
                        row["sync_attempts"] = 0;

                    if (syncCols.Contains("last_sync_error") && !row.ContainsKey("last_sync_error"))
                        row["last_sync_error"] = null;

                    rows.Add(row);
                }
            }

            if (rows.Count == 0)
                return 0;

            // 2. UPSERT en LOCAL (INSERT ... ON DUPLICATE KEY UPDATE)
            foreach (var row in rows)
            {
                var columns = string.Join(", ", row.Keys.Select(k => $"`{k}`"));
                var values = string.Join(", ", row.Keys.Select(k => $"@{k}"));

                var updateSet = string.Join(", ",
                    row.Keys
                       .Where(k => !string.Equals(k, pkColumn, StringComparison.OrdinalIgnoreCase))
                       .Select(k => $"`{k}` = VALUES(`{k}`)")
                );

                var sqlUpsert = $@"INSERT INTO {tableName} ({columns}) VALUES ({values}) ON DUPLICATE KEY UPDATE {updateSet};";

                await using var cmd = new MySqlCommand(sqlUpsert, localConn);
                cmd.Parameters.Clear();
                foreach (var kvp in row)
                    cmd.Parameters.AddWithValue($"@{kvp.Key}", kvp.Value ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }

            return rows.Count;
        }



        private async Task<int> CountPendingAsync(MySqlConnection conn, string tableName)
        {
            var sql = $"SELECT COUNT(*) FROM {tableName} WHERE synced = 0;";
            await using var cmd = new MySqlCommand(sql, conn);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result ?? 0);
        }

        private async Task<DateTime?> GetTimestampAsync(MySqlConnection conn, string key)
        {
            const string SQL = "SELECT value FROM settings WHERE `key` = @k LIMIT 1;";
            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@k", key);

            var result = await cmd.ExecuteScalarAsync();
            if (result is null || result is DBNull)
                return null;

            if (DateTime.TryParse(result.ToString(), out var dt))
                return dt;

            return null;
        }

        private async Task UpdateLastSyncTimestampAsync(MySqlConnection conn, string key)
        {
            const string SQL = @"
                INSERT INTO settings (site_id, `key`, value, is_active)
                VALUES (1, @k, NOW(), 1)
                ON DUPLICATE KEY UPDATE value = NOW();";

            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@k", key);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task LogSyncEventAsync(string logType, string message, int recordsAffected)
        {
            const string SQL = @"
                INSERT INTO sync_logs 
                    (log_uid, log_type, severity, message, records_affected, created_at)
                VALUES 
                    (@uid, @type, @sev, @msg, @recs, NOW());";

            try
            {
                await using var conn = CreateLocalConnection();
                await conn.OpenAsync();

                await using var cmd = new MySqlCommand(SQL, conn);
                cmd.Parameters.AddWithValue("@uid", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@type", logType);
                cmd.Parameters.AddWithValue("@sev", logType.Contains("ERROR") ? "ERROR" : "INFO");
                cmd.Parameters.AddWithValue("@msg", message);
                cmd.Parameters.AddWithValue("@recs", recordsAffected);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // No bloqueamos si falla el log
            }
        }
        /// <summary>
        /// Devuelve qué columnas de sync existen en la tabla local
        /// (synced, synced_at, sync_attempts, last_sync_error).
        /// Lo usamos para que PullTableAsync sepa qué rellenar.
        /// /// Lo podríam usar en el futuro si es necesario que PullTableUpsertAsync
        /// rellene también columnas de sync (synced, synced_at, etc.) según la tabla local
        /// </summary>
        private async Task<HashSet<string>> GetLocalSyncColumnsAsync(
            MySqlConnection localConn,
            string tableName)
        {
            const string SQL = @"SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tbl
                  AND COLUMN_NAME IN (
                        'synced',
                        'synced_at',
                        'sync_attempts',
                        'last_sync_error'
                  );";
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var cmd = new MySqlCommand(SQL, localConn);
            cmd.Parameters.AddWithValue("@tbl", tableName);

            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var colName = rd.GetString(0);
                result.Add(colName);
            }

            return result;
        }

    }
}