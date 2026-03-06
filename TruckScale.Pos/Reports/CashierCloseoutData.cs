using System;
using System.Collections.Generic;
using System.Linq;

namespace TruckScale.Pos.Reports
{
    public sealed class CashierCloseoutData
    {
        // ── Encabezado ────────────────────────────────────────────────────
        public string    CompanyName    { get; set; } = "McAllen Foreign Trade Zone";
        public string    CompanyAddress { get; set; } = "6401 S. 33rd Street · McAllen, Texas 78503";
        public string    CompanyPhone   { get; set; } = "(956) 682-4306";//· (956) 882-9111 Fax
        public DateTime  GeneratedAt    { get; set; } = DateTime.Now;
        public string    OperatorName   { get; set; } = "";   // quien imprime
        public int       TerminalId     { get; set; }
        public string    SessionUid     { get; set; } = "";
        /// <summary>Nombre de la app/empresa para el footer. Lee de settings key 'reports.company_name'.</summary>
        public string    ReportBrand    { get; set; } = "TruckScale POS";

        // ── Info de la sesión de caja ─────────────────────────────────────
        public DateTime? OpenedAt      { get; set; }
        public DateTime? ClosedAt      { get; set; }
        public decimal   OpeningCash   { get; set; }
        public decimal   ClosingCash   { get; set; }
        public decimal   ExpectedCash  { get; set; }
        public decimal   DiffCash      { get; set; }

        // ── Totales calculados ────────────────────────────────────────────
        public List<PaymentTotalRow>    TotalsByMethod { get; set; } = new();
        public decimal GrandTotal      => TotalsByMethod.Sum(r => r.NetTotal);
        public int     TotalTransactions { get; set; }
        public int     CompletedCount  { get; set; }
        public int     CancelledCount  { get; set; }

        // ── Detalle (1 fila por sale_uid) ─────────────────────────────────
        public List<CashierCloseoutRow> Rows { get; set; } = new();
    }

    public sealed class PaymentTotalRow
    {
        public string  MethodCode     { get; set; } = "";
        public string  MethodName     { get; set; } = "";
        /// <summary>Neto: positivo = cobrado, negativo = cancelado con ese método.</summary>
        public decimal NetTotal       { get; set; }
        public int     CompletedCount { get; set; }
        public int     CancelledCount { get; set; }
    }

    public sealed class CashierCloseoutRow
    {
        public string   SaleUid      { get; set; } = "";
        public DateTime SaleDateTime { get; set; }
        public string   TicketNumber { get; set; } = "";
        public string   DriverName   { get; set; } = "";
        public string   Plates       { get; set; } = "";
        public string   LicState     { get; set; } = "";
        public string   Tractor      { get; set; } = "";
        public string   Trailer      { get; set; } = "";
        public string   ServiceName  { get; set; } = "";
        public bool     IsCancelled  { get; set; }
        /// <summary>"COMPLETED" or "CANCELLED"</summary>
        public string   StatusLabel  { get; set; } = "COMPLETED";
        /// <summary>Concatenated payment methods: "Cash", "Card", "Cash + Card".</summary>
        public string   MethodNames  { get; set; } = "";
        /// <summary>Negative when IsCancelled = true.</summary>
        public decimal  NetAmount    { get; set; }
        public string   OperatorName { get; set; } = "";
    }
}
