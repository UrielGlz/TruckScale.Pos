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
        public DateTime? LastPushAttempt { get; set; }
        public DateTime? LastPushSuccess { get; set; }

        /// <summary>
        /// Total de registros pendientes en todas las tablas.
        /// </summary>
        public int TotalPending =>
            PendingSales + PendingPayments + PendingAxles +
            PendingDrivers + PendingTickets + PendingLogs;
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

        public int TotalSynced =>
            AxlesSynced + SalesSynced + LinesSynced +
            DriversSynced + PaymentsSynced + TicketsSynced + LogsSynced;

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
}