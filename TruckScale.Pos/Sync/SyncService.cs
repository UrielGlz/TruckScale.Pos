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

            // Diagnóstico: paso en curso y filas por tabla (van al mensaje de sync_logs)
            string step = "queue-info";
            var detail = new List<string>();

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

                step = "open-local";
                await localConn.OpenAsync();
                step = "open-main";
                await mainConn.OpenAsync();

                async Task<int> PushStep(string table, string pk)
                {
                    step = table;
                    var n = await SyncTableAsync(localConn, mainConn, table, pk);
                    detail.Add($"{table}:{n}");
                    return n;
                }

                // 3. Sincronizar en orden (respetando FKs)
                stats.AxlesSynced = await PushStep("scale_session_axles", "uuid_weight");
                stats.SalesSynced = await PushStep("sales", "sale_uid");
                stats.LinesSynced = await PushStep("sale_lines", "line_id");
                stats.DriversSynced = await PushStep("sale_driver_info", "id_driver_info");

                stats.TicketsSynced = await PushStep("tickets", "ticket_uid");
                stats.WhateverSynced += await PushStep("ticket_signatures", "sig_uid");
                stats.LogsSynced = await PushStep("sync_logs", "log_uid");
                stats.WhateverSynced += await PushStep("customer_credit", "credit_id");

                stats.SecuencesSynced += await PushStep("number_sequences", "sequence_id");
                stats.PaymentsSynced = await PushStep("payments", "payment_uid");


                // 4. Actualizar queue_status a FREE
                step = "queue-status";
                await DatabaseHelper.UpdateQueueStatusAsync(_localConnStr, QueueStatus.FREE);

                // 5. Registrar timestamp de éxito
                step = "last_push_success";
                await UpdateLastSyncTimestampAsync(localConn, "sync.last_push_success");

                stats.EndTime = DateTime.UtcNow;

                // 6. Log de éxito (con filas por tabla)
                await LogSyncEventAsync(
                    "PUSH_SUCCESS",
                    $"Synced {stats.TotalSynced} records in {stats.Duration.TotalSeconds:0.0}s | {string.Join(" ", detail)}",
                    stats.TotalSynced);

                return SyncResult.Ok(stats.TotalSynced,
                    $"Successfully synced {stats.TotalSynced} records.");
            }
            catch (Exception ex)
            {
                stats.Errors.Add(ex.Message);
                stats.EndTime = DateTime.UtcNow;

                // Log de error: paso que falló, lo que alcanzó a subir y el error completo
                var msg = $"step={step} | done: {string.Join(" ", detail)} | {FormatError(ex)}";
                await LogSyncEventAsync("PUSH_ERROR", msg, 0, error: ex, step: step);

                return SyncResult.Fail($"Sync failed at '{step}': {ex.Message}");
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
                info.PendingSignatures = await CountPendingAsync(conn, "ticket_signatures");


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
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rd.FieldCount; i++)
                    {

                        var colName = rd.GetName(i);
                        // Excluir columnas que NO existen en MAIN_DB
                        if (colName is "synced" or "synced_at" or "sync_attempts" or "last_sync_error")
                            continue;

                        if (ShouldSkipAutoId(tableName, colName))
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

                await ExecuteRowAsync(cmd, tableName, pkColumn, row);
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
            var sql = $"SELECT {pkColumn} FROM {tableName} WHERE (synced = 0 OR synced IS NULL) LIMIT {_batchSize * 10};";
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
            // Diagnóstico (no cambia la lógica del pull): paso en curso y filas por tabla
            string step = "open-main";
            var detail = new List<string>();
            DateTime? lastTs = null;

            async Task<int> PullStep(string name, Func<Task<int>> run)
            {
                step = name;
                var n = await run();
                detail.Add($"{name}:{n}");
                return n;
            }

            try
            {
                await using var mainConn = CreateMainConnection();
                await using var localConn = CreateLocalConnection();

                await mainConn.OpenAsync();
                step = "open-local";
                await localConn.OpenAsync();

                // Último pull exitoso
                step = "read last_pull_ts";
                lastTs = await GetTimestampAsync(localConn, "sync.last_pull_ts");
                
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
                total += await PullStep("sites", () => PullTableUpsertAsync(mainConn, localConn, "sites", "site_id", lastTs));
                total += await PullStep("status_catalogo", () => PullTableUpsertAsync(mainConn, localConn, "status_catalogo", "status_id", lastTs));
                total += await PullStep("tax_rates", () => PullTableUpsertAsync(mainConn, localConn, "tax_rates", "tax_rate_id", lastTs));
                total += await PullStep("products", () => PullTableUpsertAsync(mainConn, localConn, "products", "product_id", lastTs));
                total += await PullStep("customers", () => PullTableUpsertAsync(mainConn, localConn, "customers", "id_customer", lastTs));
                total += await PullStep("vehicle_types", () => PullTableUpsertAsync(mainConn, localConn, "vehicle_types", "vehicle_type_id", lastTs));
                total += await PullStep("driver_products", () => PullTableUpsertAsync(mainConn, localConn, "driver_products", "product_id", lastTs));
                total += await PullStep("license_states", () => PullTableUpsertAsync(mainConn, localConn, "license_states", "id_state", lastTs));
                total += await PullStep("payment_methods", () => PullTableUpsertAsync(mainConn, localConn, "payment_methods", "method_id", lastTs));
                total += await PullStep("number_sequences", () => PullTableUpsertAsync(mainConn, localConn, "number_sequences", "sequence_id", lastTs));
                total += await PullStep("users", () => PullTableUpsertAsync(mainConn, localConn, "users", "user_id", lastTs));

                // NUEVO: customer_credit (depende de customers)
                // Como es tabla chica y muy sensible, hacemos full upsert (sin filtro por fecha).
                total += await PullStep("customer_credit", () => PullTableUpsertAsync(mainConn, localConn, "customer_credit", "credit_id", null));



                // settings: MAIN es la fuente de verdad.
                // Se excluyen las keys 'sync.*' (estado interno del sincronizador LOCAL).
                // Upsert por (site_id, key) — nunca por setting_id (auto-increment, no comparable cross-DB).
                total += await PullStep("settings", () => PullSettingsAsync(mainConn, localConn));

                // 2) Tablas transaccionales (usamos UIDs)
                total += await PullStep("sales", () => PullTableUpsertAsync(mainConn, localConn, "sales", "sale_uid", lastTs));
                total += await PullStep("sale_driver_info", () => PullTableUpsertAsync(mainConn, localConn, "sale_driver_info", "sale_uid", lastTs));

                total += await PullStep("sale_lines", () => PullTableUpsertAsync(mainConn, localConn, "sale_lines", "sale_uid", lastTs));



                total += await PullStep("tickets", () => PullTableUpsertAsync(mainConn, localConn, "tickets", "ticket_uid", lastTs));
                // ticket_signatures no tiene updated_at (usa captured_at) → full pull con null
                total += await PullStep("ticket_signatures", () => PullTableUpsertAsync(mainConn, localConn, "ticket_signatures", "sig_uid", null));
                total += await PullStep("scale_session_axles", () => PullTableUpsertAsync(mainConn, localConn, "scale_session_axles", "uuid_weight", lastTs));
                total += await PullStep("sync_logs", () => PullTableUpsertAsync(mainConn, localConn, "sync_logs", "log_uid", lastTs));
                // IMPORTANTE: payments → full upsert (sin depender de updated_at / last_pull_ts)
                //total += await PullTableUpsertAsync(mainConn, localConn, "payments", "payment_uid", null);
                total += await PullStep("payments", () => PullPaymentsWithParentsAsync(mainConn, localConn, lastTs));
                total += await PullStep("number_sequences(2)", () => PullTableUpsertAsync(mainConn, localConn, "number_sequences", "sequence_id", lastTs));



                // Nuevo timestamp de pull
                step = "write last_pull_ts";
                await UpdateLastSyncTimestampAsync(localConn, "sync.last_pull_ts");

                await LogSyncEventAsync("PULL_SUCCESS",
                    $"Pull completed. Records upserted: {total}. last_pull_ts(prev)={FormatTs(lastTs)} | {string.Join(" ", detail)}",
                    total);

                return SyncResult.Ok(total, $"Pull completed. Records upserted: {total}.");
            }
            catch (Exception ex)
            {
                // Mensaje completo: paso/tabla que falló, lo que sí se aplicó y el error con inner exceptions
                var msg = $"step={step} | last_pull_ts={FormatTs(lastTs)} | done: {string.Join(" ", detail)} | {FormatError(ex)}";
                await LogSyncEventAsync("PULL_ERROR", msg, 0, error: ex, step: step, lastPullTs: lastTs);
                return SyncResult.Fail($"Pull failed at '{step}': {ex.Message}");
            }
        }


        // ========================================================================
        // HELPERS
        // ========================================================================
        private async Task<int> PullPaymentsWithParentsAsync(
    MySqlConnection mainConn,
    MySqlConnection localConn,
    DateTime? lastTs)
        {
            // 0) Leer payments de MAIN (incremental si hay lastTs; full si no hay)
            var paymentRows = new List<Dictionary<string, object?>>();

            var syncColsPayments = await GetLocalSyncColumnsAsync(localConn, "payments");
            var where = lastTs.HasValue ? "WHERE COALESCE(updated_at, created_at) > @ts" : "";
            var sqlSelectPayments = $"SELECT * FROM payments {where};";

            await using (var cmd = new MySqlCommand(sqlSelectPayments, mainConn))
            {
                if (lastTs.HasValue)
                    cmd.Parameters.AddWithValue("@ts", lastTs.Value);

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        var col = rd.GetName(i);

                        // NO copiar auto IDs (payments.payment_id)
                        if (ShouldSkipAutoId("payments", col))
                            continue;

                        row[col] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    }

                    // marcar como ya sincronizado en LOCAL si existen esas columnas
                    if (syncColsPayments.Contains("synced") && !row.ContainsKey("synced"))
                        row["synced"] = 1;
                    if (syncColsPayments.Contains("synced_at") && !row.ContainsKey("synced_at"))
                        row["synced_at"] = DateTime.UtcNow;
                    if (syncColsPayments.Contains("sync_attempts") && !row.ContainsKey("sync_attempts"))
                        row["sync_attempts"] = 0;
                    if (syncColsPayments.Contains("last_sync_error") && !row.ContainsKey("last_sync_error"))
                        row["last_sync_error"] = null;

                    paymentRows.Add(row);
                }
            }

            if (paymentRows.Count == 0)
                return 0;

            // 1) Asegurar parents: sales para todos los sale_uid de esos payments
            var saleUids = paymentRows
                .Select(r => r.TryGetValue("sale_uid", out var v) ? v?.ToString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (saleUids.Count > 0)
                await EnsureSalesExistAsync(mainConn, localConn, saleUids);

            // 2) Upsert payments en LOCAL
            await UpsertRowsAsync(localConn, "payments", "payment_uid", paymentRows);

            return paymentRows.Count;
        }

        private async Task EnsureSalesExistAsync(
            MySqlConnection mainConn,
            MySqlConnection localConn,
            List<string> saleUids)
        {
            // A) detectar cuáles faltan en LOCAL.sales
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in saleUids.Chunk(500))
            {
                var placeholders = string.Join(",", batch.Select((_, i) => $"@p{i}"));
                var sql = $"SELECT sale_uid FROM sales WHERE sale_uid IN ({placeholders});";

                await using var cmd = new MySqlCommand(sql, localConn);
                for (int i = 0; i < batch.Length; i++)
                    cmd.Parameters.AddWithValue($"@p{i}", batch[i]);

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var raw = rd.GetValue(0);          // puede ser Guid o string
                    var uid = raw?.ToString();         // siempre string
                    if (!string.IsNullOrWhiteSpace(uid))
                        existing.Add(uid);
                }
            }

            var missing = saleUids.Where(u => !existing.Contains(u)).ToList();
            if (missing.Count == 0)
                return;

            //AppendLog($"[Sync] Missing parent sales in LOCAL: {missing.Count}. Pulling from MAIN...");

            // B) traer esas sales desde MAIN y upsert en LOCAL
            var saleRows = new List<Dictionary<string, object?>>();
            var syncColsSales = await GetLocalSyncColumnsAsync(localConn, "sales");

            foreach (var batch in missing.Chunk(300))
            {
                var placeholders = string.Join(",", batch.Select((_, i) => $"@s{i}"));
                var sql = $"SELECT * FROM sales WHERE sale_uid IN ({placeholders});";

                await using var cmd = new MySqlCommand(sql, mainConn);
                for (int i = 0; i < batch.Length; i++)
                    cmd.Parameters.AddWithValue($"@s{i}", batch[i]);

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        var col = rd.GetName(i);

                        // 👇 CLAVE: NO copiar auto IDs (sales.sale_id)
                        if (ShouldSkipAutoId("sales", col))
                            continue;

                        row[col] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    }

                    if (syncColsSales.Contains("synced") && !row.ContainsKey("synced"))
                        row["synced"] = 1;
                    if (syncColsSales.Contains("synced_at") && !row.ContainsKey("synced_at"))
                        row["synced_at"] = DateTime.UtcNow;
                    if (syncColsSales.Contains("sync_attempts") && !row.ContainsKey("sync_attempts"))
                        row["sync_attempts"] = 0;
                    if (syncColsSales.Contains("last_sync_error") && !row.ContainsKey("last_sync_error"))
                        row["last_sync_error"] = null;

                    saleRows.Add(row);
                }
            }

            if (saleRows.Count > 0)
                await UpsertRowsAsync(localConn, "sales", "sale_uid", saleRows);
        }

        private async Task UpsertRowsAsync(
            MySqlConnection localConn,
            string tableName,
            string pkColumn,
            List<Dictionary<string, object?>> rows)
        {
            foreach (var row in rows)
            {
                var columns = string.Join(", ", row.Keys.Select(k => $"`{k}`"));
                var values = string.Join(", ", row.Keys.Select(k => $"@{k}"));

                var updateSet = string.Join(", ",
                    row.Keys
                       .Where(k => !string.Equals(k, pkColumn, StringComparison.OrdinalIgnoreCase))
                       .Select(k => $"`{k}` = VALUES(`{k}`)")
                );

                var sqlUpsert =
                    $@"INSERT INTO {tableName} ({columns})
               VALUES ({values})
               ON DUPLICATE KEY UPDATE {updateSet};";

                await using var cmd = new MySqlCommand(sqlUpsert, localConn);
                foreach (var kvp in row)
                    cmd.Parameters.AddWithValue($"@{kvp.Key}", kvp.Value ?? DBNull.Value);

                await ExecuteRowAsync(cmd, tableName, pkColumn, row);
            }
        }

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
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        var colName = rd.GetName(i);

                        if (ShouldSkipAutoId(tableName, colName))
                            continue;

                        row[colName] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    }

                    // Muy importante: si la tabla LOCAL tiene columnas de sync,
                    // marcamos estos registros como "ya sincronizados" para que no
                    // se intenten subir de vuelta a MAIN en el siguiente Push.
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
                //0222.UG
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // SIEMPRE excluir PK
                skip.Add(pkColumn);

                // tablas especiales (además del PK)
                if (tableName == "sale_driver_info")
                {
                    skip.Add("sale_uid");        // UNIQUE key (no lo muevas)
                    skip.Add("id_driver_info");  // por si llega en el row
                }

                if (tableName == "sale_lines")
                {
                    skip.Add("sale_uid");        // parte de UNIQUE
                    skip.Add("seq");             // parte de UNIQUE
                    skip.Add("line_id");         // por si llega en el row
                }

                if (tableName == "ticket_signatures")
                {
                    skip.Add("ticket_uid");      // UNIQUE key (1 firma por ticket)
                    skip.Add("sig_id");          // por si llega en el row
                }

                var updateSet = string.Join(", ",
                    row.Keys
                       .Where(k => !skip.Contains(k))
                       .Select(k => $"`{k}` = VALUES(`{k}`)")
                );

                if (string.IsNullOrWhiteSpace(updateSet))
                    updateSet = $"`{pkColumn}` = `{pkColumn}`"; // no-op (evita SQL inválido)
                                                                // 

                var sqlUpsert = $@"INSERT INTO {tableName} ({columns}) VALUES ({values}) ON DUPLICATE KEY UPDATE {updateSet};";

                await using var cmd = new MySqlCommand(sqlUpsert, localConn);
                cmd.Parameters.Clear();
                foreach (var kvp in row)
                    cmd.Parameters.AddWithValue($"@{kvp.Key}", kvp.Value ?? DBNull.Value);

                await ExecuteRowAsync(cmd, tableName, pkColumn, row);
            }

            return rows.Count;
        }
        private static bool ShouldSkipAutoId(string tableName, string colName)
        {
            tableName = tableName?.ToLowerInvariant() ?? "";
            colName = colName?.ToLowerInvariant() ?? "";

            return (tableName == "sales" && colName == "sale_id")
                || (tableName == "payments" && colName == "payment_id")
                || (tableName == "sale_lines" && colName == "line_id")
                || (tableName == "sale_driver_info" && colName == "id_driver_info")
                || (tableName == "ticket_signatures" && colName == "sig_id");
        }


        private async Task<int> CountPendingAsync(MySqlConnection conn, string tableName)
        {
            var sql = $"SELECT COUNT(*) FROM {tableName} WHERE synced = 0 OR synced IS NULL;";
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

        // Límites de columnas de sync_logs (SHOW CREATE TABLE, 2026-09-29)
        private const int SYNC_LOGS_LOG_TYPE_MAX = 20;       // varchar(20)
        private const int SYNC_LOGS_SEVERITY_MAX = 10;       // varchar(10)
        private const int SYNC_LOGS_RELATED_ENTITY_MAX = 50; // varchar(50)
        private const int SYNC_LOGS_RELATED_UID_MAX = 36;    // char(36)

        // MySQL 1054 = columna desconocida; 3819 (MySQL) / 4025 (MariaDB) = CHECK violado
        private static bool IsSchemaMismatch(MySqlException ex) =>
            ex.Number is 1054 or 3819 or 4025;

        /// <summary>
        /// Registra un evento de sync.
        /// 1) SIEMPRE lo escribe completo en el log de archivo (C:\TruckScaleLogs).
        /// 2) Lo inserta en LOCAL.sync_logs:
        ///    - message: texto completo (columna TEXT, sin truncar);
        ///    - error_details: JSON válido generado con JsonSerializer (la columna tiene
        ///      CHECK json_valid), o NULL si no hay error o no se pudo serializar. Nunca texto crudo;
        ///    - related_entity / related_uid: tabla y sale_uid (o uid de la fila) cuando el
        ///      error se produjo en una fila concreta;
        ///    - log_type / severity / related_entity recortados a su tamaño de columna.
        ///    last_sync_error NO se escribe: es el error de subir ESA fila a MAIN (lo usa el push).
        /// 3) Si la columna extendida no existe o el CHECK la rechaza, reintenta con el INSERT básico.
        /// No relanza: un fallo al registrar no debe abortar el sync. El fallo queda en el archivo.
        /// </summary>
        private async Task LogSyncEventAsync(
            string logType,
            string message,
            int recordsAffected,
            Exception? error = null,
            string? step = null,
            DateTime? lastPullTs = null)
        {
            const string SQL_EXTENDED = @"
                INSERT INTO sync_logs
                    (log_uid, log_type, severity, message, records_affected,
                     related_entity, related_uid, error_details, created_at)
                VALUES
                    (@uid, @type, @sev, @msg, @recs, @rent, @ruid, @err, NOW());";

            const string SQL_BASIC = @"
                INSERT INTO sync_logs
                    (log_uid, log_type, severity, message, records_affected, created_at)
                VALUES
                    (@uid, @type, @sev, @msg, @recs, NOW());";

            var type = Fit(logType, SYNC_LOGS_LOG_TYPE_MAX);
            var severity = Fit(logType.Contains("ERROR") ? "ERROR" : "INFO", SYNC_LOGS_SEVERITY_MAX);

            var rowErr = FindRowException(error);
            string? relatedEntity = rowErr?.Table is { Length: > 0 } t ? Fit(t, SYNC_LOGS_RELATED_ENTITY_MAX) : null;
            string? relatedUid = rowErr?.RelatedUid is { Length: > 0 and <= SYNC_LOGS_RELATED_UID_MAX } u ? u : null;
            string? errorJson = error == null ? null : BuildErrorDetailsJson(error, step, lastPullTs, rowErr);

            AppLogger.Log($"[SyncLog] {type} ({severity}) records={recordsAffected}" +
                          (relatedEntity != null ? $" related={relatedEntity}:{relatedUid}" : "") +
                          $" | {message}");

            async Task InsertAsync(MySqlConnection conn, string sql)
            {
                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@uid", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@sev", severity);
                cmd.Parameters.AddWithValue("@msg", message);
                cmd.Parameters.AddWithValue("@recs", recordsAffected);
                cmd.Parameters.AddWithValue("@rent", (object?)relatedEntity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ruid", (object?)relatedUid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@err", (object?)errorJson ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            try
            {
                await using var conn = CreateLocalConnection();
                await conn.OpenAsync();

                try
                {
                    await InsertAsync(conn, SQL_EXTENDED);
                }
                catch (MySqlException ex) when (IsSchemaMismatch(ex))
                {
                    AppLogger.Log($"[SyncLog] extended INSERT rejected ({FormatError(ex)}); retrying basic INSERT.");
                    await InsertAsync(conn, SQL_BASIC);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[SyncLog] FAILED to write sync_logs ({type}): {FormatError(ex)}");
            }
        }

        private static string Fit(string value, int max) =>
            value.Length <= max ? value : value[..max];

        private static SyncRowException? FindRowException(Exception? ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
                if (cur is SyncRowException re) return re;
            return null;
        }

        /// <summary>
        /// error_details como JSON válido (lo exige el CHECK json_valid de la columna).
        /// Se construye con JsonSerializer, que escapa comillas y saltos de línea.
        /// Si algo falla, devuelve NULL en vez de texto crudo.
        /// </summary>
        private static string? BuildErrorDetailsJson(
            Exception error, string? step, DateTime? lastPullTs, SyncRowException? rowErr)
        {
            try
            {
                var chain = new List<Dictionary<string, object?>>();
                var cur = (Exception?)error;
                for (int depth = 0; cur != null && depth < 5; depth++, cur = cur.InnerException)
                {
                    chain.Add(new Dictionary<string, object?>
                    {
                        ["type"] = cur.GetType().Name,
                        ["mysql_number"] = cur is MySqlException mex ? mex.Number : null,
                        ["message"] = cur.Message
                    });
                }

                var payload = new Dictionary<string, object?>
                {
                    ["step"] = step,
                    ["last_pull_ts"] = lastPullTs?.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["table"] = rowErr?.Table,
                    ["row_key"] = rowErr?.RowKey,
                    ["sale_uid"] = rowErr?.SaleUid,
                    ["errors"] = chain,
                    ["stack"] = error.StackTrace
                };

                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                using var _ = System.Text.Json.JsonDocument.Parse(json); // defensa: validar antes de enviar
                return json;
            }
            catch
            {
                return null; // NULL pasa el CHECK; texto crudo no
            }
        }

        /// <summary>
        /// Ejecuta el INSERT/UPSERT de una fila del sync. Si falla, relanza envuelto en
        /// SyncRowException con la tabla, la clave de la fila y su sale_uid (si la tiene),
        /// para poder correlacionar el error con una venta. No cambia el flujo: la excepción
        /// sigue abortando el paso igual que antes.
        /// </summary>
        private static async Task ExecuteRowAsync(
            MySqlCommand cmd, string tableName, string pkColumn, Dictionary<string, object?> row)
        {
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                string? rowKey = row.TryGetValue(pkColumn, out var k) ? k?.ToString() : null;
                string? saleUid = row.TryGetValue("sale_uid", out var s) ? s?.ToString() : null;
                throw new SyncRowException(tableName, rowKey, saleUid, ex);
            }
        }

        private static string FormatTs(DateTime? ts) =>
            ts.HasValue ? ts.Value.ToString("yyyy-MM-dd HH:mm:ss") : "null(full)";

        /// <summary>
        /// Texto completo de una excepción: tipo, número de error MySQL, mensaje e inner exceptions.
        /// </summary>
        private static string FormatError(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            var cur = (Exception?)ex;
            int depth = 0;
            while (cur != null && depth < 5)
            {
                if (depth > 0) sb.Append(" → ");
                sb.Append($"[{cur.GetType().Name}");
                if (cur is MySqlException mex) sb.Append($" #{mex.Number}");
                sb.Append($"] {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
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

        /// <summary>
        /// PULL de settings desde MAIN → LOCAL.
        /// Reglas:
        ///   - MAIN es la fuente de verdad; LOCAL nunca sube settings a MAIN.
        ///   - Se excluyen keys 'sync.*' (estado interno del sincronizador LOCAL).
        ///   - Upsert por la constraint única real (site_id, key), NO por setting_id.
        ///   - setting_id se omite del SELECT para evitar colisiones de auto-increment cross-DB.
        ///   - Full pull (sin filtro de fecha): settings es pequeña y no tiene updated_at garantizado.
        /// </summary>
        private async Task<int> PullSettingsAsync(
            MySqlConnection mainConn,
            MySqlConnection localConn)
        {
            var rows = new List<Dictionary<string, object?>>();

            // Leer de MAIN, excluyendo siempre las keys de sync interno
            const string SQL_SELECT = @"
                SELECT *
                FROM settings
                WHERE `key` NOT LIKE 'sync.%';";

            await using (var cmd = new MySqlCommand(SQL_SELECT, mainConn))
            await using (var rd = await cmd.ExecuteReaderAsync())
            {
                while (await rd.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        var colName = rd.GetName(i);

                        // setting_id es auto-increment; no es comparable cross-DB
                        if (colName.Equals("setting_id", StringComparison.OrdinalIgnoreCase))
                            continue;

                        row[colName] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    }
                    rows.Add(row);
                }
            }

            if (rows.Count == 0)
                return 0;

            // Upsert en LOCAL por (site_id, key)
            // No actualizar site_id ni key (forman la constraint única).
            var skipOnUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "site_id", "key" };

            foreach (var row in rows)
            {
                var columns = string.Join(", ", row.Keys.Select(k => $"`{k}`"));
                var values  = string.Join(", ", row.Keys.Select(k => $"@{k}"));

                var updateSet = string.Join(", ",
                    row.Keys
                       .Where(k => !skipOnUpdate.Contains(k))
                       .Select(k => $"`{k}` = VALUES(`{k}`)"));

                // Protección: si todos los campos son parte de la llave, usar no-op
                if (string.IsNullOrWhiteSpace(updateSet))
                    updateSet = "`site_id` = `site_id`";

                var sqlUpsert = $@"
                    INSERT INTO settings ({columns})
                    VALUES ({values})
                    ON DUPLICATE KEY UPDATE {updateSet};";

                await using var cmd = new MySqlCommand(sqlUpsert, localConn);
                foreach (var kvp in row)
                    cmd.Parameters.AddWithValue($"@{kvp.Key}", kvp.Value ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }

            return rows.Count;
        }

    }
}