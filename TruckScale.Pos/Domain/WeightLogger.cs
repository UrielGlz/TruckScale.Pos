// TruckScale.Pos/Domain/WeightLogger.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using TruckScale.Pos.Data;

namespace TruckScale.Pos.Domain
{
    /// <summary>
    /// Cliente mínimo para los SP:
    ///   - sp_scale_session_insert
    ///   - sp_scale_axle_insert
    /// Usa la conexión que ya tienes vía IDbConnectionFactory.
    /// </summary>
    public sealed class WeightLogger
    {
        private readonly IDbConnectionFactory _factory;

        public WeightLogger(IDbConnectionFactory factory) => _factory = factory;

        // ---------------------------- DTOs de resultado ----------------------------
        public readonly record struct SessionResult(ulong SessionId, string Uuid10);
        public readonly record struct AxleResult(ulong AxleId, ulong SessionId, decimal AxlesSumLb, decimal DiffVsTotal);

        // DTO para conveniencia al registrar una sesión completa
        public readonly record struct AxleSpec(int Index, decimal WeightLb, string RawLine);

        // -------------------------- SP: insertar sesión ----------------------------
        /// <summary>
        /// Inserta la sesión (peso total estable de un camión).
        /// El SP setea fechas en MySQL a zona -06:00 (definido en el SP).
        /// </summary>
        public async Task<(ulong SessionId, string Uuid10)> InsertSessionAsync(decimal totalLb, CancellationToken ct = default)
        {
            await using var con = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);

            // 1) Ejecuta el SP
            await using (var cmd = new MySqlCommand("sp_scale_session_insert", con)
            {
                CommandType = CommandType.StoredProcedure
            })
            {
                cmd.Parameters.AddWithValue("p_total_lb", totalLb);

                var pOutId = new MySqlParameter("o_session_id", MySqlDbType.UInt64)
                { Direction = ParameterDirection.Output };
                cmd.Parameters.Add(pOutId);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                var sessionId = (ulong)Convert.ToUInt64(pOutId.Value ?? 0UL);

                // 2) Leemos el uuid10 tras el INSERT
                string uuid10 = "";
                await using (var cmd2 = new MySqlCommand(
                    "SELECT uuid10 FROM scale_sessions WHERE id=@id LIMIT 1;", con))
                {
                    cmd2.Parameters.AddWithValue("@id", sessionId);
                    var obj = await cmd2.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    uuid10 = obj?.ToString() ?? "";
                }

                return (sessionId, uuid10);
            }
        }



        // --------------------------- SP: insertar eje ------------------------------
        /// <summary>
        /// Inserta un eje en la sesión indicada. Debes pasar al menos uno:
        ///   - sessionId (preferible) o
        ///   - sessionUuid10
        /// </summary>
        public async Task<AxleResult> InsertAxleAsync(
            int axleIndex,
            decimal weightLb,
            string rawLine,
            ulong? sessionId = null,
            string? sessionUuid10 = null,
            CancellationToken ct = default)
        {
            if (sessionId is null && string.IsNullOrWhiteSpace(sessionUuid10))
                throw new ArgumentException("Debes proporcionar sessionId o sessionUuid10.");

            await using var con = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);

            await using var cmd = new MySqlCommand("sp_scale_axle_insert", con)
            {
                CommandType = CommandType.StoredProcedure
            };

            // IN
            cmd.Parameters.Add(new MySqlParameter("p_session_uuid10", MySqlDbType.VarChar, 10)
            { Value = (object?)sessionUuid10 ?? DBNull.Value });

            cmd.Parameters.Add(new MySqlParameter("p_session_id", MySqlDbType.UInt64)
            { Value = (object?)sessionId ?? DBNull.Value });

            cmd.Parameters.Add(new MySqlParameter("p_axle_index", MySqlDbType.Int32) { Value = axleIndex });

            cmd.Parameters.Add(new MySqlParameter("p_weight_lb", MySqlDbType.NewDecimal)
            { Precision = 12, Scale = 3, Value = weightLb });

            cmd.Parameters.Add(new MySqlParameter("p_raw_line", MySqlDbType.VarChar, 128)
            { Value = rawLine ?? string.Empty });

            // OUT
            var pAxleId = new MySqlParameter("o_axle_id", MySqlDbType.UInt64) { Direction = ParameterDirection.Output };
            var pSessId = new MySqlParameter("o_session_id", MySqlDbType.UInt64) { Direction = ParameterDirection.Output };
            var pSumAxles = new MySqlParameter("o_axles_sum_lb", MySqlDbType.NewDecimal) { Direction = ParameterDirection.Output, Precision = 12, Scale = 3 };
            var pDiff = new MySqlParameter("o_diff_vs_total", MySqlDbType.NewDecimal) { Direction = ParameterDirection.Output, Precision = 12, Scale = 3 };

            cmd.Parameters.Add(pAxleId);
            cmd.Parameters.Add(pSessId);
            cmd.Parameters.Add(pSumAxles);
            cmd.Parameters.Add(pDiff);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var axleId = (ulong)Convert.ToUInt64(pAxleId.Value);
            var sessId = (ulong)Convert.ToUInt64(pSessId.Value);

            // MySqlConnector devuelve decimal en object; conviértelo con Convert.ToDecimal.
            var sumAxles = Convert.ToDecimal(pSumAxles.Value);
            var diff = Convert.ToDecimal(pDiff.Value);

            return new AxleResult(axleId, sessId, sumAxles, diff);
        }

        // --------------- Conveniencia: sesión + ejes en transacción ----------------
        /// <summary>
        /// Crea la sesión y registra todos los ejes en UNA transacción.
        /// Regresa (SessionResult, lista de AxleResult).
        /// </summary>
        public async Task<(SessionResult session, List<AxleResult> axles)> InsertFullSessionAsync(
            decimal totalLb,
            IEnumerable<AxleSpec> axles,
            CancellationToken ct = default)
        {
            await using var con = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await con.BeginTransactionAsync(ct).ConfigureAwait(false);

            // 1) Cabecera
            SessionResult session;
            await using (var cmd = new MySqlCommand("sp_scale_session_insert", con, tx)
            {
                CommandType = CommandType.StoredProcedure
            })
            {
                cmd.Parameters.Add(new MySqlParameter("p_total_lb", MySqlDbType.NewDecimal)
                { Precision = 12, Scale = 3, Value = totalLb });

                var pOutId = new MySqlParameter("o_session_id", MySqlDbType.UInt64) { Direction = ParameterDirection.Output };
                var pOutUuid = new MySqlParameter("o_uuid10", MySqlDbType.VarChar, 10) { Direction = ParameterDirection.Output };
                cmd.Parameters.Add(pOutId);
                cmd.Parameters.Add(pOutUuid);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                session = new SessionResult(
                    (ulong)Convert.ToUInt64(pOutId.Value),
                    Convert.ToString(pOutUuid.Value) ?? string.Empty
                );
            }

            // 2) Detalles
            var results = new List<AxleResult>();
            foreach (var a in axles)
            {
                await using var cmd = new MySqlCommand("sp_scale_axle_insert", con, tx)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.Add(new MySqlParameter("p_session_uuid10", MySqlDbType.VarChar, 10) { Value = session.Uuid10 });
                cmd.Parameters.Add(new MySqlParameter("p_session_id", MySqlDbType.UInt64) { Value = session.SessionId });
                cmd.Parameters.Add(new MySqlParameter("p_axle_index", MySqlDbType.Int32) { Value = a.Index });
                cmd.Parameters.Add(new MySqlParameter("p_weight_lb", MySqlDbType.NewDecimal) { Precision = 12, Scale = 3, Value = a.WeightLb });
                cmd.Parameters.Add(new MySqlParameter("p_raw_line", MySqlDbType.VarChar, 128) { Value = a.RawLine ?? string.Empty });

                var pAxleId = new MySqlParameter("o_axle_id", MySqlDbType.UInt64) { Direction = ParameterDirection.Output };
                var pSessId = new MySqlParameter("o_session_id", MySqlDbType.UInt64) { Direction = ParameterDirection.Output };
                var pSumAxles = new MySqlParameter("o_axles_sum_lb", MySqlDbType.NewDecimal) { Direction = ParameterDirection.Output, Precision = 12, Scale = 3 };
                var pDiff = new MySqlParameter("o_diff_vs_total", MySqlDbType.NewDecimal) { Direction = ParameterDirection.Output, Precision = 12, Scale = 3 };
                cmd.Parameters.Add(pAxleId);
                cmd.Parameters.Add(pSessId);
                cmd.Parameters.Add(pSumAxles);
                cmd.Parameters.Add(pDiff);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                results.Add(new AxleResult(
                    (ulong)Convert.ToUInt64(pAxleId.Value),
                    (ulong)Convert.ToUInt64(pSessId.Value),
                    Convert.ToDecimal(pSumAxles.Value),
                    Convert.ToDecimal(pDiff.Value)
                ));
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (session, results);
        }
        public async Task LogEventAsync(
        string kind,
        int? ch = null, double? w = null, string? tail = null,
        double? e0 = null, double? e1 = null, double? e2 = null, double? total = null,
        double? sumAxles = null, double? deltaSumTot = null, double? deltaVsLast = null,
        int? axlesFreshMs = null, string? rawLine = null, string? note = null)
        {
            using var conn = await _factory.CreateOpenConnectionAsync().ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
        INSERT INTO scale_debug
        (kind,ch,w,tail,e0,e1,e2,total,sum_axles,delta_sum_tot,delta_vs_last,axles_fresh_ms,raw_line,note)
        VALUES
        (@kind,@ch,@w,@tail,@e0,@e1,@e2,@total,@sum,@deltasum,@deltalast,@fresh,@raw,@note)";
            void P(string n, object? v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value; cmd.Parameters.Add(p); }
            P("@kind", kind); P("@ch", ch); P("@w", w); P("@tail", tail);
            P("@e0", e0); P("@e1", e1); P("@e2", e2); P("@total", total);
            P("@sum", sumAxles); P("@deltasum", deltaSumTot); P("@deltalast", deltaVsLast);
            P("@fresh", axlesFreshMs); P("@raw", rawLine); P("@note", note);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }


    }

}
