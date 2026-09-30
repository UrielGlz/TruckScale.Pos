using System;
using System.Collections.Generic;

namespace TruckScale.Pos.Sync
{
    // ============================================================================
    // SYNC MODELS
    // ============================================================================
    // Modelos y DTOs para el sistema de sincronización LOCAL_DB ↔ MAIN_DB.
    // Creado: 2024-12
    // Autor: Development Team
    // ============================================================================

    /// <summary>
    /// Estado de la cola de sincronización.
    /// FREE  = No hay transacciones pendientes
    /// DIRTY = Hay transacciones que necesitan subirse a MAIN_DB
    /// </summary>
    public enum QueueStatus
    {
        FREE,
        DIRTY
    }

    /// <summary>
    /// Resultado de una operación de sincronización.
    /// Incluye éxito/fallo, registros afectados y mensajes de error.
    /// </summary>
    public sealed class SyncResult
    {
        public bool Success { get; set; }
        public int RecordsSynced { get; set; }
        public List<string> Errors { get; } = new();
        public string? Message { get; set; }

        /// <summary>
        /// Crea un resultado exitoso.
        /// </summary>
        public static SyncResult Ok(int records, string? message = null)
            => new() { Success = true, RecordsSynced = records, Message = message };

        /// <summary>
        /// Crea un resultado fallido.
        /// </summary>
        public static SyncResult Fail(string error)
        {
            var r = new SyncResult { Success = false };
            r.Errors.Add(error);
            return r;
        }

        /// <summary>
        /// Agrega un error adicional a la lista.
        /// </summary>
        public void AddError(string error) => Errors.Add(error);
    }

    /// <summary>
    /// Información del estado actual de la cola de sincronización.
    /// </summary>
    public sealed class QueueInfo
    {
        public QueueStatus Status { get; set; }
        public int PendingSales { get; set; }
        public int PendingPayments { get; set; }
        public int PendingAxles { get; set; }
        public int PendingDrivers { get; set; }
        public int PendingTickets { get; set; }
        public int PendingLogs { get; set; }
        public int PendingSqcuences { get; set; }
        public int PendingSignatures { get; set; }


        public DateTime? LastPushAttempt { get; set; }
        public DateTime? LastPushSuccess { get; set; }

        /// <summary>
        /// Total de registros pendientes que DISPARAN un push.
        /// PendingLogs NO cuenta: cada push/pull escribe una fila en sync_logs, y si esa fila
        /// disparara el siguiente push el sync correría en cada tick solo para subir sus propios
        /// logs. Los logs pendientes viajan a MAIN junto con el siguiente push de datos reales.
        /// </summary>
        public int TotalPending =>
            PendingSales + PendingPayments + PendingAxles +
            PendingDrivers + PendingTickets + PendingSqcuences + PendingSignatures;
    }

    /// <summary>
    /// Estadísticas detalladas de una operación de sincronización.
    /// Útil para logs y debugging.
    /// </summary>
    public sealed class SyncStats
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;

        public int AxlesSynced { get; set; }
        public int SalesSynced { get; set; }
        public int LinesSynced { get; set; }
        public int DriversSynced { get; set; }
        public int PaymentsSynced { get; set; }
        public int TicketsSynced { get; set; }
        public int LogsSynced { get; set; }
        public int WhateverSynced { get; set; }
        public int SecuencesSynced { get; set; }

        


        public int TotalSynced =>
            AxlesSynced + SalesSynced + LinesSynced +
            DriversSynced + PaymentsSynced + TicketsSynced + LogsSynced + WhateverSynced + SecuencesSynced;

        public List<string> Errors { get; } = new();

        public bool HasErrors => Errors.Count > 0;
    }

    /// <summary>
    /// Registro individual de tabla pendiente de sincronizar.
    /// Usado internamente por SyncService para trackear qué tablas procesar.
    /// </summary>
    internal sealed class SyncBatch
    {
        public string TableName { get; init; } = "";
        public List<string> Uids { get; } = new();
        public int Count => Uids.Count;
    }

    /// <summary>
    /// Error al escribir UNA fila durante el push o el pull.
    /// Lleva la tabla, la clave de la fila y su sale_uid (si la tiene) para que el
    /// PUSH_ERROR/PULL_ERROR en sync_logs pueda llenar related_entity / related_uid.
    /// </summary>
    public sealed class SyncRowException : Exception
    {
        public string Table { get; }
        public string? RowKey { get; }
        public string? SaleUid { get; }

        /// <summary>sale_uid si la fila pertenece a una venta; si no, la clave de la fila.</summary>
        public string? RelatedUid => !string.IsNullOrWhiteSpace(SaleUid) ? SaleUid : RowKey;

        public SyncRowException(string table, string? rowKey, string? saleUid, Exception inner)
            : base($"{table} row {rowKey ?? "?"}" +
                   (string.IsNullOrWhiteSpace(saleUid) ? "" : $" (sale_uid={saleUid})") +
                   $": {inner.Message}", inner)
        {
            Table = table;
            RowKey = rowKey;
            SaleUid = saleUid;
        }
    }
}