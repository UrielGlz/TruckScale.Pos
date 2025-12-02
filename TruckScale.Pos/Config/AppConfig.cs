namespace TruckScale.Pos.Config
{
    public class AppConfig
    {
        public string MainDbStrCon { get; set; } = "";
        public string LocalDbStrCon { get; set; } = "";

        // NEW: configuración básica de impresión del ticket
        public string TicketPrinterName { get; set; } = "";  // si está vacío usamos la impresora predeterminada
        public bool TicketLandscape { get; set; } = true;    // media carta horizontal
        public double TicketMarginInches { get; set; } = 0.25; // margen default
    }

    // Para mapear directo el JSON (con nombres exactos)
    internal class RawConfig
    {
        public string main_db_str_con { get; set; } = "";
        public string local_db_str_con { get; set; } = "";
        // NEW: campos opcionales
        public string? ticket_printer_name { get; set; } = "";
        public bool? ticket_landscape { get; set; } = null;
        public double? ticket_margin_inches { get; set; } = null;
        // NUEVO: campos opcionales para no romper configs viejos
        public string? receipt_printer_name { get; set; }
    }
}
