using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MySqlConnector;
using TruckScale.Pos.Config;

namespace TruckScale.Pos.Sync
{
    public sealed class SyncService
    {
        private readonly string _mainConnStr;
        private readonly string _localConnStr;
        private readonly int _batchSize;

        // Fixed identity of the only sequence we reconcile in this stage.
        private const int    SEQ_SITE_ID = 1;
        private const string SEQ_SCOPE   = "TICKETS.NUMBER";

        public SyncService(int batchSize = 50)
        {
            ConfigManager.Load();

            var primaryConn = ConfigManager.Current.MainDbStrCon;
            var localConn   = ConfigManager.Current.LocalDbStrCon;

            if (string.IsNullOrWhiteSpace(primaryConn))
                throw new InvalidOperationException("MainDbStrCon is not configured in config.json.");
            if (string.IsNullOrWhiteSpace(localConn))
                throw new InvalidOperationException("LocalDbStrCon is not configured in config.json.");

            _mainConnStr  = primaryConn;
            _localConnStr = localConn;
            _batchSize    = batchSize;
        }

        private MySqlConnection CreateLocalConnection()
        {
            var csb = new MySqlConnectionStringBuilder(_localConnStr)
            {
                AllowZeroDateTime = true,
                ConvertZeroDateTime = true,
                GuidFormat = MySqlGuidFormat.None
            };
            return new MySqlConnection(csb.ConnectionString);
        }

        private MySqlConnection CreateMainConnection()
        {
            var csb = new MySqlConnectionStringBuilder(_mainConnStr)
            {
                AllowZeroDateTime = true,
                ConvertZeroDateTime = true,
                GuidFormat = MySqlGuidFormat.None
            };
            return new MySqlConnection(csb.ConnectionString);
        }

        // ========================================================================
        // PUBLIC API
        // ========================================================================

        /// <summary>
        /// Pushes all pending LOCAL records to MAIN using INSERT IGNORE (never overwrites).
        /// Tables are pushed in FK-dependency order.
        /// </summary>
        public async Task<SyncResult> PushTransactionsAsync()
        {
            var stats = new SyncStats { StartTime = DateTime.UtcNow };

            try
            {
                var queueInfo = await GetQueueInfoAsync();
                if (queueInfo.TotalPending == 0)
                    return SyncResult.Ok(0, "Nothing to sync (queue empty).");

                await using var localConn = CreateLocalConnection();
                await using var mainConn  = CreateMainConnection();
                await localConn.OpenAsync();
                await mainConn.OpenAsync();

                stats.customerSynced  += await SyncTableAsync(localConn, mainConn, "customers",          "id_customer");
                stats.AxlesSynced      = await SyncTableAsync(localConn, mainConn, "scale_session_axles", "uuid_weight");
                stats.SecuencesSynced += await SyncTableAsync(localConn, mainConn, "number_sequences",    "sequence_id");
                stats.SalesSynced      = await SyncTableAsync(localConn, mainConn, "sales",               "sale_uid");
                stats.LinesSynced      = await SyncTableAsync(localConn, mainConn, "sale_lines",          "sale_uid");
                stats.DriversSynced    = await SyncTableAsync(localConn, mainConn, "sale_driver_info",    "sale_uid");
                stats.PaymentsSynced   = await SyncTableAsync(localConn, mainConn, "payments",            "payment_uid");
                stats.TicketsSynced    = await SyncTableAsync(localConn, mainConn, "tickets",             "ticket_uid");
                stats.WhateverSynced  += await SyncTableAsync(localConn, mainConn, "ticket_signatures",   "sig_uid");
                stats.WhateverSynced  += await SyncTableAsync(localConn, mainConn, "customer_credit",     "credit_id");

                await DatabaseHelper.UpdateQueueStatusAsync(_localConnStr, QueueStatus.FREE);
                await UpdateLastSyncTimestampAsync(localConn, "sync.last_push_success");

                stats.EndTime = DateTime.UtcNow;

                var detail = $"Push {stats.Duration.TotalSeconds:0.0}s" +
                    (stats.customerSynced  > 0 ? $" | customers:{stats.customerSynced}"  : "") +
                    (stats.AxlesSynced     > 0 ? $" | axles:{stats.AxlesSynced}"         : "") +
                    (stats.SecuencesSynced > 0 ? $" | sequences:{stats.SecuencesSynced}" : "") +
                    (stats.SalesSynced     > 0 ? $" | sales:{stats.SalesSynced}"         : "") +
                    (stats.LinesSynced     > 0 ? $" | lines:{stats.LinesSynced}"         : "") +
                    (stats.DriversSynced   > 0 ? $" | drivers:{stats.DriversSynced}"     : "") +
                    (stats.PaymentsSynced  > 0 ? $" | payments:{stats.PaymentsSynced}"   : "") +
                    (stats.TicketsSynced   > 0 ? $" | tickets:{stats.TicketsSynced}"     : "") +
                    (stats.WhateverSynced  > 0 ? $" | other:{stats.WhateverSynced}"      : "") +
                    $" | total:{stats.TotalSynced}";

                await LogSyncEventAsync("PUSH_SUCCESS", detail, stats.TotalSynced);
                return SyncResult.Ok(stats.TotalSynced, $"Successfully synced {stats.TotalSynced} records.");
            }
            catch (Exception ex)
            {
                stats.Errors.Add(ex.Message);
                stats.EndTime = DateTime.UtcNow;
                await LogSyncEventAsync("PUSH_ERROR", ex.Message, 0, FormatError(ex));
                return SyncResult.Fail($"Sync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reconciles the TICKETS.NUMBER sequence between MAIN and LOCAL.
        /// Must be called after PushTransactionsAsync so that offline tickets
        /// are already in MAIN when computing the safe_value.
        /// </summary>
        public async Task<SyncResult> PullUpdatesAsync()
        {
            try
            {
                await using var mainConn  = CreateMainConnection();
                await using var localConn = CreateLocalConnection();
                await mainConn.OpenAsync();
                await localConn.OpenAsync();

                var (safeValue, mainUpdated, localUpdated) =
                    await ReconcileTicketNumberSequenceAsync(mainConn, localConn);

                int dbsUpdated = (mainUpdated ? 1 : 0) + (localUpdated ? 1 : 0);
                return SyncResult.Ok(dbsUpdated, $"Sequence reconciled. safe_value={safeValue}.");
            }
            catch (Exception ex)
            {
                await LogSyncEventAsync("PULL_ERROR", ex.Message, 0, FormatError(ex));
                return SyncResult.Fail($"Pull failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns pending record counts per table from LOCAL.
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

                info.PendingAxles      = await CountPendingAsync(conn, "scale_session_axles");
                info.PendingSales      = await CountPendingAsync(conn, "sales");
                info.PendingDrivers    = await CountPendingAsync(conn, "sale_driver_info");
                info.PendingTickets    = await CountPendingAsync(conn, "tickets");
                info.PendingLogs       = await CountPendingAsync(conn, "sync_logs");
                info.PendingSqcuences  = await CountPendingAsync(conn, "number_sequences");
                info.PendingPayments   = await CountPendingAsync(conn, "payments");
                info.PendingSignatures = await CountPendingAsync(conn, "ticket_signatures");

                info.LastPushAttempt = await GetTimestampAsync(conn, "sync.last_push_attempt");
                info.LastPushSuccess = await GetTimestampAsync(conn, "sync.last_push_success");
            }
            catch { }

            return info;
        }

        // ========================================================================
        // PUSH ENGINE
        // ========================================================================

        private async Task<int> SyncTableAsync(
            MySqlConnection localConn,
            MySqlConnection mainConn,
            string tableName,
            string pkColumn)
        {
            try
            {
                var pendingUids = await GetPendingUidsAsync(localConn, tableName, pkColumn);
                if (pendingUids.Count == 0)
                    return 0;

                int totalSynced = 0;
                foreach (var batch in pendingUids.Chunk(_batchSize))
                    totalSynced += await SyncBatchAsync(localConn, mainConn, tableName, pkColumn, batch);

                return totalSynced;
            }
            catch (MySqlException ex) when (ex.Number == 1146)
            {
                await LogSyncEventAsync("PUSH_SKIP", $"Table '{tableName}' not found, skipped.", 0);
                return 0;
            }
        }

        private async Task<int> SyncBatchAsync(
            MySqlConnection localConn,
            MySqlConnection mainConn,
            string tableName,
            string pkColumn,
            string[] uids)
        {
            if (uids.Length == 0) return 0;

            var inParams  = string.Join(",", Enumerable.Range(0, uids.Length).Select(i => $"@p{i}"));
            var sqlSelect = $"SELECT * FROM `{tableName}` WHERE `{pkColumn}` IN ({inParams});";
            var rows      = new List<Dictionary<string, object?>>();

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

            // INSERT IGNORE in batches of 200
            const int BATCH_SIZE = 200;
            foreach (var batch in rows.Chunk(BATCH_SIZE))
            {
                var cols    = batch[0].Keys.ToList();
                var columns = string.Join(", ", cols.Select(k => $"`{k}`"));

                await using var cmd = new MySqlCommand { Connection = mainConn, CommandTimeout = 60 };

                var valueClauses = new List<string>(batch.Length);
                for (int r = 0; r < batch.Length; r++)
                {
                    var paramClause = new List<string>(cols.Count);
                    for (int c = 0; c < cols.Count; c++)
                    {
                        var pname = $"@q{r}x{c}";
                        paramClause.Add(pname);
                        var val = batch[r].TryGetValue(cols[c], out var v) ? v : null;
                        cmd.Parameters.AddWithValue(pname, val ?? DBNull.Value);
                    }
                    valueClauses.Add($"({string.Join(",", paramClause)})");
                }

                cmd.CommandText =
                    $"INSERT IGNORE INTO `{tableName}` ({columns}) VALUES {string.Join(",", valueClauses)};";
                await cmd.ExecuteNonQueryAsync();
            }

            // Confirm which UIDs actually landed in MAIN (INSERT IGNORE silently drops FK violations)
            var verifyParams    = string.Join(",", Enumerable.Range(0, uids.Length).Select(i => $"@v{i}"));
            var confirmedInMain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using (var cmd = new MySqlCommand(
                $"SELECT `{pkColumn}` FROM `{tableName}` WHERE `{pkColumn}` IN ({verifyParams});", mainConn))
            {
                for (int i = 0; i < uids.Length; i++)
                    cmd.Parameters.AddWithValue($"@v{i}", uids[i]);
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    confirmedInMain.Add(rd.GetValue(0)?.ToString() ?? "");
            }

            var confirmed = uids.Where(u => confirmedInMain.Contains(u)).ToArray();
            int skipped   = uids.Length - confirmed.Length;

            if (skipped > 0)
                await LogSyncEventAsync("PUSH_WARN",
                    $"{tableName}: {skipped} of {uids.Length} rows NOT confirmed in MAIN " +
                    "(FK violation or constraint rejected). Will retry next push.", skipped);

            if (confirmed.Length == 0) return 0;

            // Mark confirmed rows as synced in LOCAL
            var updateParams = string.Join(",", Enumerable.Range(0, confirmed.Length).Select(i => $"@c{i}"));
            await using (var cmd = new MySqlCommand(
                $"UPDATE `{tableName}` SET synced=1, synced_at=NOW() WHERE `{pkColumn}` IN ({updateParams});",
                localConn))
            {
                for (int i = 0; i < confirmed.Length; i++)
                    cmd.Parameters.AddWithValue($"@c{i}", confirmed[i]);
                await cmd.ExecuteNonQueryAsync();
            }

            return confirmed.Length;
        }

        private async Task<List<string>> GetPendingUidsAsync(
            MySqlConnection conn, string tableName, string pkColumn)
        {
            var sql  = $"SELECT {pkColumn} FROM {tableName} WHERE (synced = 0 OR synced IS NULL) LIMIT {_batchSize * 10};";
            var uids = new List<string>();

            await using var cmd = new MySqlCommand(sql, conn);
            await using var rd  = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var uid = rd.GetValue(0)?.ToString();
                if (!string.IsNullOrWhiteSpace(uid))
                    uids.Add(uid);
            }

            return uids;
        }

        // ========================================================================
        // SEQUENCE RECONCILIATION  (site_id=1, scope='TICKETS.NUMBER')
        // ========================================================================

        /// <summary>
        /// Computes the safe current_value for the TICKETS.NUMBER sequence and
        /// updates whichever DB(s) are behind — without ever decreasing a counter.
        ///
        /// safe_value = MAX(
        ///     MAIN.number_sequences.current_value,
        ///     LOCAL.number_sequences.current_value,
        ///     MAX numeric ticket_number in MAIN.tickets,
        ///     MAX numeric ticket_number in LOCAL.tickets
        /// )
        ///
        /// Must be called after PushTransactionsAsync so offline tickets are
        /// already present in MAIN when reading the max ticket evidence.
        /// Non-numeric ticket_number values are logged as warnings and excluded.
        /// The row is never created or deleted — only current_value is updated.
        /// </summary>
        private async Task<(long safeValue, bool mainUpdated, bool localUpdated)>
            ReconcileTicketNumberSequenceAsync(MySqlConnection mainConn, MySqlConnection localConn)
        {
            // 1. Read current counter values
            long mainCurrent  = await ReadSeqCurrentValueAsync(mainConn);
            long localCurrent = await ReadSeqCurrentValueAsync(localConn);

            // 2. Warn on any non-numeric ticket_numbers (excluded from calculation)
            await WarnNonNumericTicketsAsync(mainConn,  "MAIN");
            await WarnNonNumericTicketsAsync(localConn, "LOCAL");

            // 3. Read evidence from tickets table in both DBs
            long mainMaxTicket  = await ReadMaxNumericTicketAsync(mainConn);
            long localMaxTicket = await ReadMaxNumericTicketAsync(localConn);

            // 4. Compute safe_value: the floor no counter must fall below
            long safeValue = Math.Max(
                Math.Max(mainCurrent,  localCurrent),
                Math.Max(mainMaxTicket, localMaxTicket));

            // 5. Update MAIN if its counter lags (MAIN has no synced columns)
            bool mainUpdated = false;
            if (safeValue > mainCurrent)
            {
                const string SQL = @"UPDATE number_sequences
                                     SET    current_value = @v
                                     WHERE  site_id = @sid
                                       AND  scope   = @scope
                                       AND  current_value < @v;";
                await using var cmd = new MySqlCommand(SQL, mainConn);
                cmd.Parameters.AddWithValue("@v",     safeValue);
                cmd.Parameters.AddWithValue("@sid",   SEQ_SITE_ID);
                cmd.Parameters.AddWithValue("@scope", SEQ_SCOPE);
                mainUpdated = await cmd.ExecuteNonQueryAsync() > 0;
            }

            // 6. Update LOCAL if its counter lags (LOCAL has sync columns)
            bool localUpdated = false;
            if (safeValue > localCurrent)
            {
                const string SQL = @"UPDATE number_sequences
                                     SET    current_value = @v,
                                            synced        = 1,
                                            synced_at     = NOW()
                                     WHERE  site_id = @sid
                                       AND  scope   = @scope
                                       AND  current_value < @v;";
                await using var cmd = new MySqlCommand(SQL, localConn);
                cmd.Parameters.AddWithValue("@v",     safeValue);
                cmd.Parameters.AddWithValue("@sid",   SEQ_SITE_ID);
                cmd.Parameters.AddWithValue("@scope", SEQ_SCOPE);
                localUpdated = await cmd.ExecuteNonQueryAsync() > 0;
            }

            // 7. Log outcome
            var detail =
                $"safe={safeValue}" +
                $" | main={mainCurrent}{(mainUpdated   ? $"→{safeValue}" : " (no change)")}" +
                $" | local={localCurrent}{(localUpdated ? $"→{safeValue}" : " (no change)")}" +
                $" | evidence(mainTickets={mainMaxTicket}, localTickets={localMaxTicket})";

            await LogSyncEventAsync("SEQ_RECONCILE", detail,
                (mainUpdated ? 1 : 0) + (localUpdated ? 1 : 0));

            return (safeValue, mainUpdated, localUpdated);
        }

        private async Task<long> ReadSeqCurrentValueAsync(MySqlConnection conn)
        {
            const string SQL = @"SELECT current_value FROM number_sequences
                                 WHERE site_id = @sid AND scope = @scope
                                 LIMIT 1;";
            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@sid",   SEQ_SITE_ID);
            cmd.Parameters.AddWithValue("@scope", SEQ_SCOPE);
            var result = await cmd.ExecuteScalarAsync();
            return result is null or DBNull ? 0L : Convert.ToInt64(result);
        }

        private static async Task<long> ReadMaxNumericTicketAsync(MySqlConnection conn)
        {
            const string SQL = @"SELECT COALESCE(MAX(CAST(ticket_number AS UNSIGNED)), 0)
                                 FROM   tickets
                                 WHERE  ticket_number IS NOT NULL
                                   AND  ticket_number REGEXP '^[0-9]+$';";
            await using var cmd = new MySqlCommand(SQL, conn);
            var result = await cmd.ExecuteScalarAsync();
            return result is null or DBNull ? 0L : Convert.ToInt64(result);
        }

        private async Task WarnNonNumericTicketsAsync(MySqlConnection conn, string dbLabel)
        {
            const string SQL = @"SELECT ticket_number FROM tickets
                                 WHERE  ticket_number IS NOT NULL
                                   AND  ticket_number NOT REGEXP '^[0-9]+$'
                                 LIMIT  50;";
            await using var cmd = new MySqlCommand(SQL, conn);
            await using var rd  = await cmd.ExecuteReaderAsync();

            var found = new List<string>();
            while (await rd.ReadAsync())
                found.Add(rd.GetString(0));

            if (found.Count > 0)
                await LogSyncEventAsync("SEQ_WARN",
                    $"[{dbLabel}] Non-numeric ticket_numbers excluded from safe_value: " +
                    string.Join(", ", found),
                    found.Count);
        }

        // ========================================================================
        // HELPERS
        // ========================================================================

        private static bool ShouldSkipAutoId(string tableName, string colName)
        {
            tableName = tableName?.ToLowerInvariant() ?? "";
            colName   = colName?.ToLowerInvariant()   ?? "";

            return (tableName == "sales"            && colName == "sale_id")
                || (tableName == "payments"          && colName == "payment_id")
                || (tableName == "sale_lines"        && colName == "line_id")
                || (tableName == "sale_driver_info"  && colName == "id_driver_info")
                || (tableName == "ticket_signatures" && colName == "sig_id")
                || (tableName == "tickets"           && colName == "ticket_id");
        }

        private async Task<int> CountPendingAsync(MySqlConnection conn, string tableName)
        {
            var sql = $"SELECT COUNT(*) FROM {tableName} WHERE synced = 0 OR synced IS NULL;";
            await using var cmd = new MySqlCommand(sql, conn);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
        }

        private async Task<DateTime?> GetTimestampAsync(MySqlConnection conn, string key)
        {
            const string SQL = "SELECT value FROM settings WHERE `key` = @k LIMIT 1;";
            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@k", key);
            var result = await cmd.ExecuteScalarAsync();
            if (result is null or DBNull) return null;
            return DateTime.TryParse(result.ToString(), out var dt) ? dt : (DateTime?)null;
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

        private async Task LogSyncEventAsync(
            string logType, string message, int recordsAffected, string? errorDetails = null)
        {
            const string SQL_FULL = @"
                INSERT INTO sync_logs
                    (log_uid, log_type, severity, message, records_affected, error_details, created_at, synced, synced_at)
                VALUES (@uid, @type, @sev, @msg, @recs, @err, NOW(), 1, NOW());";

            const string SQL_SHORT = @"
                INSERT INTO sync_logs
                    (log_uid, log_type, severity, message, records_affected, created_at, synced, synced_at)
                VALUES (@uid, @type, @sev, @msg, @recs, NOW(), 1, NOW());";

            const int MAX_ERR_LEN = 4000;

            try
            {
                await using var conn = CreateLocalConnection();
                await conn.OpenAsync();

                var sev     = logType.Contains("ERR") ? "ERROR" : "INFO";
                var uid     = Guid.NewGuid().ToString();
                var msgSafe = message.Length > 1000 ? message[..1000] : message;

                try
                {
                    var errVal = errorDetails == null ? null
                        : errorDetails.Length > MAX_ERR_LEN
                            ? errorDetails[..MAX_ERR_LEN] + "\n[truncated]"
                            : errorDetails;

                    await using var cmd = new MySqlCommand(SQL_FULL, conn);
                    cmd.Parameters.AddWithValue("@uid",  uid);
                    cmd.Parameters.AddWithValue("@type", logType);
                    cmd.Parameters.AddWithValue("@sev",  sev);
                    cmd.Parameters.AddWithValue("@msg",  msgSafe);
                    cmd.Parameters.AddWithValue("@recs", recordsAffected);
                    cmd.Parameters.AddWithValue("@err",  (object?)errVal ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    await using var cmd2 = new MySqlCommand(SQL_SHORT, conn);
                    cmd2.Parameters.AddWithValue("@uid",  Guid.NewGuid().ToString());
                    cmd2.Parameters.AddWithValue("@type", logType);
                    cmd2.Parameters.AddWithValue("@sev",  sev);
                    cmd2.Parameters.AddWithValue("@msg",  msgSafe);
                    cmd2.Parameters.AddWithValue("@recs", recordsAffected);
                    await cmd2.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }

        private static string FormatError(Exception ex)
        {
            var sb  = new System.Text.StringBuilder();
            var cur = (Exception?)ex;
            int depth = 0;
            while (cur != null && depth < 5)
            {
                if (depth > 0) sb.Append("\n→ ");
                sb.Append($"[{cur.GetType().Name}] {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            if (ex.StackTrace != null)
            {
                var lines = ex.StackTrace.Split('\n');
                sb.Append("\n--- Stack ---\n");
                sb.Append(string.Join("\n", lines.Take(8)));
            }
            return sb.ToString();
        }
    }
}
