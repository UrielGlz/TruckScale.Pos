using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TruckScale.Pos.Reports
{
    /// <summary>
    /// Construye un <see cref="FlowDocument"/> WPF tamaño CARTA (8.5 × 11")
    /// para el reporte "Corte de Caja / Apertura y Cierre".
    /// Sin dependencias externas — sólo System.Windows.Documents.
    /// </summary>
    public static class CashierCloseoutBuilder
    {
        // ── Constantes de página ─────────────────────────────────────────────
        private const double DPI    = 96.0;
        private const double PAGE_W = 8.5  * DPI;   // 816 px
        private const double PAGE_H = 11.0 * DPI;   // 1056 px
        private const double MARGIN = 0.5  * DPI;   // 48 px  (½")

        // ── Estilo ────────────────────────────────────────────────────────────
        private static readonly CultureInfo  _money  = new("en-US");
        private static readonly System.Windows.Media.FontFamily _font = new("Segoe UI");
        private static readonly SolidColorBrush _headerBg   = new(Color.FromRgb(50,  50,  50 ));
        private static readonly SolidColorBrush _headerFg   = Brushes.White;
        private static readonly SolidColorBrush _altRowBg   = new(Color.FromRgb(245, 247, 250));
        private static readonly SolidColorBrush _voidRowBg  = new(Color.FromRgb(255, 240, 240));
        private static readonly SolidColorBrush _subtotalBg = new(Color.FromRgb(230, 235, 245));
        private static readonly SolidColorBrush _borderColor = new(Color.FromRgb(190, 190, 190));

        // ── Columnas del detalle ─────────────────────────────────────────────
        // (ancho en GridUnitType.Star — total = 8.5)
        private static readonly (string Label, double W, TextAlignment Align)[] _cols =
        {
            ("Hora",       1.0, TextAlignment.Left),
            ("Ticket",     0.9, TextAlignment.Left),
            ("Chofer",     2.2, TextAlignment.Left),
            ("Placas",     0.9, TextAlignment.Left),
            ("Servicio",   1.3, TextAlignment.Left),
            ("Método",     1.2, TextAlignment.Left),
            ("Monto",      0.9, TextAlignment.Right),
        };

        // ────────────────────────────────────────────────────────────────────
        public static FlowDocument Build(CashierCloseoutData data)
        {
            var doc = new FlowDocument
            {
                PageWidth   = PAGE_W,
                PageHeight  = PAGE_H,
                PagePadding = new Thickness(MARGIN),
                ColumnWidth = double.MaxValue, // una sola columna
                FontFamily  = _font,
                FontSize    = 8.5,
                LineHeight  = 13,
                Foreground  = Brushes.Black,
                Background  = Brushes.White,
            };

            doc.Blocks.Add(BuildHeader(data));
            doc.Blocks.Add(BuildSummarySection(data));
            doc.Blocks.Add(HRule());
            doc.Blocks.Add(DetailTitle());
            doc.Blocks.Add(BuildDetailTable(data));
            doc.Blocks.Add(BuildFooter(data));

            return doc;
        }

        // ── Encabezado ───────────────────────────────────────────────────────
        private static Block BuildHeader(CashierCloseoutData d)
        {
            var section = new Section { Margin = new Thickness(0, 0, 0, 6) };

            section.Blocks.Add(Para(d.CompanyName, 15, bold: true, center: true, margin: new Thickness(0, 0, 0, 1)));
            section.Blocks.Add(Para(d.CompanyAddress, 8.5, center: true, margin: new Thickness(0)));
            section.Blocks.Add(Para(d.CompanyPhone, 8.5, center: true, margin: new Thickness(0, 0, 0, 4)));
            section.Blocks.Add(Para("CORTE DE CAJA / APERTURA Y CIERRE", 13, bold: true, center: true, margin: new Thickness(0, 0, 0, 5)));

            // Tabla 2 columnas: info de sesión
            var tbl = InfoTable();
            var rg = new TableRowGroup();
            tbl.RowGroups.Add(rg);

            string openedStr  = d.OpenedAt.HasValue  ? d.OpenedAt.Value.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture)  : "--";
            string closedStr  = d.ClosedAt.HasValue  ? d.ClosedAt.Value.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture)   : "--";
            string generatedStr = d.GeneratedAt.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture);

            AddInfoRow(rg, "Generado:",    generatedStr,       "Terminal:",    d.TerminalId.ToString());
            AddInfoRow(rg, "Impreso por:", d.OperatorName,     "Session UID:", TruncateUid(d.SessionUid));
            AddInfoRow(rg, "Apertura:",    openedStr,          "Cierre:",      closedStr);

            section.Blocks.Add(tbl);
            return section;
        }

        // ── Resumen ──────────────────────────────────────────────────────────
        private static Block BuildSummarySection(CashierCloseoutData d)
        {
            var section = new Section { Margin = new Thickness(0, 4, 0, 4) };

            // ── Tabla de totales por método ──────────────────────────────────
            var methodTbl = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 4) };
            methodTbl.Columns.Add(Col(3.0));  // Método
            methodTbl.Columns.Add(Col(1.2));  // Cobrado
            methodTbl.Columns.Add(Col(1.2));  // Cancelados
            methodTbl.Columns.Add(Col(1.2));  // Neto

            var rg = new TableRowGroup();
            methodTbl.RowGroups.Add(rg);

            // Header row
            var hdr = new TableRow { Background = _headerBg };
            rg.Rows.Add(hdr);
            hdr.Cells.Add(SummaryHdrCell("Método de Pago"));
            hdr.Cells.Add(SummaryHdrCell("Cobrado (Tx)",  TextAlignment.Right));
            hdr.Cells.Add(SummaryHdrCell("Cancelados",    TextAlignment.Right));
            hdr.Cells.Add(SummaryHdrCell("Neto",          TextAlignment.Right));

            // Data rows
            decimal grandCompleted = 0;
            decimal grandCancelled = 0;
            foreach (var r in d.TotalsByMethod)
            {
                decimal completedAmt = r.CompletedCount > 0 ? r.NetTotal + (r.CancelledCount > 0 ? Math.Abs(r.NetTotal - r.NetTotal) : 0) : 0;
                // Para mostrar: cobrado bruto = neto + abs(cancelado). Pero no tenemos cobrado bruto separado.
                // Mostramos: Cobrado (neto positivos) y Cancelado (neto negativos) por método.
                // En la query, net_total ya viene como neto (positivos completado, negativos cancelado).
                // Aquí simplificamos: column "Cobrado" = sum completed (si net_total >0, es net), "Cancelados" = count.
                var dataRow = new TableRow();
                rg.Rows.Add(dataRow);
                dataRow.Cells.Add(DataCell(r.MethodName));
                dataRow.Cells.Add(DataCell($"{r.CompletedCount} tx", TextAlignment.Right));
                dataRow.Cells.Add(DataCell(r.CancelledCount > 0 ? $"{r.CancelledCount} void" : "—", TextAlignment.Right));
                dataRow.Cells.Add(DataCell(r.NetTotal.ToString("C", _money), TextAlignment.Right,
                                           bold: true, fg: r.NetTotal < 0 ? Brushes.DarkRed : null));
                grandCompleted += r.CompletedCount;
                grandCancelled += r.CancelledCount;
            }

            // Fila total
            var totRow = new TableRow { Background = _subtotalBg };
            rg.Rows.Add(totRow);
            totRow.Cells.Add(DataCell($"TOTAL DEL CORTE  ({d.TotalTransactions} transacciones)", bold: true));
            totRow.Cells.Add(DataCell($"{d.CompletedCount} completadas", TextAlignment.Right, bold: true));
            totRow.Cells.Add(DataCell(d.CancelledCount > 0 ? $"{d.CancelledCount} void" : "—",
                                      TextAlignment.Right, bold: true));
            totRow.Cells.Add(DataCell(d.GrandTotal.ToString("C", _money), TextAlignment.Right,
                                      bold: true, fg: d.GrandTotal < 0 ? Brushes.DarkRed : null));

            section.Blocks.Add(Para("RESUMEN DEL CORTE", 10, bold: true, margin: new Thickness(0, 0, 0, 3)));
            section.Blocks.Add(methodTbl);

            // ── Línea de diferencia de caja (cash diff) ──────────────────────
            if (d.OpeningCash != 0 || d.ClosingCash != 0)
            {
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2), FontSize = 8.5 };
                p.Inlines.Add(new Run("Efectivo apertura: ") { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(d.OpeningCash.ToString("C", _money) + "    "));
                p.Inlines.Add(new Run("Efectivo esperado: ") { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(d.ExpectedCash.ToString("C", _money) + "    "));
                p.Inlines.Add(new Run("Efectivo real: ") { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(d.ClosingCash.ToString("C", _money) + "    "));

                var diffStr = d.DiffCash.ToString("C", _money);
                var diffRun = new Run($"Diferencia: {(d.DiffCash >= 0 ? "+" : "")}{diffStr}");
                if (d.DiffCash < 0)
                    diffRun.Foreground = Brushes.DarkRed;
                p.Inlines.Add(new Bold(diffRun));

                section.Blocks.Add(p);
            }

            return section;
        }

        // ── Tabla de detalle ─────────────────────────────────────────────────
        private static Block BuildDetailTable(CashierCloseoutData d)
        {
            var tbl = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };

            foreach (var (_, w, _) in _cols)
                tbl.Columns.Add(Col(w));

            var rg = new TableRowGroup();
            tbl.RowGroups.Add(rg);

            // ── Header row ──────────────────────────────────────────────────
            var hdr = new TableRow { Background = _headerBg };
            rg.Rows.Add(hdr);
            foreach (var (label, _, align) in _cols)
                hdr.Cells.Add(DetailHdrCell(label, align));

            // ── Data rows ───────────────────────────────────────────────────
            bool alt = false;
            foreach (var r in d.Rows)
            {
                var bg = r.IsCancelled ? _voidRowBg
                                       : (alt ? _altRowBg : Brushes.White);
                alt = !alt;

                var row = new TableRow { Background = bg };
                rg.Rows.Add(row);

                string timeStr   = r.SaleDateTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
                string amountStr = r.NetAmount.ToString("C", _money);
                var    amountFg  = r.IsCancelled ? Brushes.DarkRed : (Brush)Brushes.Black;

                row.Cells.Add(DetailDataCell(timeStr));
                row.Cells.Add(DetailDataCell(r.TicketNumber));
                row.Cells.Add(DetailDataCell(r.DriverName));
                row.Cells.Add(DetailDataCell(PlatesCell(r)));
                row.Cells.Add(DetailDataCell(r.ServiceName + (r.IsCancelled ? " [VOID]" : "")));
                row.Cells.Add(DetailDataCell(r.MethodNames));
                row.Cells.Add(DetailDataCell(amountStr, TextAlignment.Right, fg: amountFg));
            }

            // ── Pie de tabla: totales ────────────────────────────────────────
            var footRow = new TableRow { Background = _subtotalBg };
            rg.Rows.Add(footRow);
            var footCell = new TableCell(new Paragraph(
                new Run($"Total neto: {d.GrandTotal.ToString("C", _money)}   " +
                        $"({d.CompletedCount} completadas, {d.CancelledCount} canceladas)")
                {
                    FontWeight = FontWeights.Bold
                })
            {
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(2, 1, 4, 1),
                FontSize = 8.5
            });
            footCell.ColumnSpan = _cols.Length;
            footCell.Padding = new Thickness(3, 2, 3, 2);
            footRow.Cells.Add(footCell);

            return tbl;
        }

        // ── Footer ───────────────────────────────────────────────────────────
        private static Block BuildFooter(CashierCloseoutData d)
        {
            return Para(
                $"Generado: {d.GeneratedAt:MM/dd/yyyy h:mm tt} — TruckScale POS",
                8, center: true, margin: new Thickness(0, 4, 0, 0));
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers de construcción
        // ────────────────────────────────────────────────────────────────────

        private static Paragraph Para(string text, double size = 8.5,
            bool bold = false, bool center = false, Thickness? margin = null)
        {
            return new Paragraph(new Run(text))
            {
                FontSize      = size,
                FontWeight    = bold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
                Margin        = margin ?? new Thickness(0, 1, 0, 1),
            };
        }

        private static Block HRule()
        {
            return new Paragraph
            {
                BorderBrush     = _borderColor,
                BorderThickness = new Thickness(0, 0, 0, 0.7),
                Margin          = new Thickness(0, 4, 0, 4),
            };
        }

        private static Block DetailTitle() =>
            Para("DETALLE DE TRANSACCIONES", 9.5, bold: true, margin: new Thickness(0, 0, 0, 3));

        private static TableColumn Col(double stars) =>
            new() { Width = new GridLength(stars, GridUnitType.Star) };

        // ── Info table (header 2-col key/value grid) ─────────────────────────
        private static Table InfoTable()
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 2) };
            t.Columns.Add(Col(0.8));  // label left
            t.Columns.Add(Col(2.0));  // value left
            t.Columns.Add(Col(0.8));  // label right
            t.Columns.Add(Col(2.0));  // value right
            return t;
        }

        private static void AddInfoRow(TableRowGroup rg,
            string lbl1, string val1, string lbl2, string val2)
        {
            var row = new TableRow();
            rg.Rows.Add(row);
            row.Cells.Add(InfoLabelCell(lbl1));
            row.Cells.Add(InfoValueCell(val1));
            row.Cells.Add(InfoLabelCell(lbl2));
            row.Cells.Add(InfoValueCell(val2));
        }

        private static TableCell InfoLabelCell(string text) => new(
            new Paragraph(new Run(text) { FontWeight = FontWeights.Bold })
            { Margin = new Thickness(0), FontSize = 8.5 })
        { Padding = new Thickness(0, 1, 4, 1) };

        private static TableCell InfoValueCell(string text) => new(
            new Paragraph(new Run(text))
            { Margin = new Thickness(0), FontSize = 8.5 })
        { Padding = new Thickness(0, 1, 8, 1) };

        // ── Summary table cells ───────────────────────────────────────────────
        private static TableCell SummaryHdrCell(string text, TextAlignment align = TextAlignment.Left) => new(
            new Paragraph(new Run(text) { Foreground = _headerFg })
            {
                FontWeight = FontWeights.Bold, TextAlignment = align,
                Margin = new Thickness(2, 0, 2, 0), FontSize = 8.5
            })
        {
            Padding = new Thickness(4, 3, 4, 3),
            BorderBrush = Brushes.DarkGray, BorderThickness = new Thickness(0, 0, 0, 0.5)
        };

        private static TableCell DataCell(string text,
            TextAlignment align = TextAlignment.Left,
            bool bold = false, Brush? fg = null) => new(
            new Paragraph(new Run(text)
            {
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = fg ?? Brushes.Black
            })
            {
                TextAlignment = align, Margin = new Thickness(2, 0, 2, 0), FontSize = 8.5
            })
        {
            Padding         = new Thickness(4, 2, 4, 2),
            BorderBrush     = _borderColor,
            BorderThickness = new Thickness(0, 0, 0, 0.5)
        };

        // ── Detail table cells ────────────────────────────────────────────────
        private static TableCell DetailHdrCell(string text, TextAlignment align = TextAlignment.Left) => new(
            new Paragraph(new Run(text) { Foreground = _headerFg })
            {
                FontWeight = FontWeights.Bold, TextAlignment = align,
                Margin = new Thickness(2, 0, 2, 0), FontSize = 8
            })
        {
            Padding         = new Thickness(3, 3, 3, 3),
            BorderBrush     = Brushes.DarkGray,
            BorderThickness = new Thickness(0, 0, 0.3, 0.5)
        };

        private static TableCell DetailDataCell(string text,
            TextAlignment align = TextAlignment.Left, Brush? fg = null) => new(
            new Paragraph(new Run(text) { Foreground = fg ?? Brushes.Black })
            {
                TextAlignment = align, Margin = new Thickness(2, 0, 2, 0), FontSize = 8
            })
        {
            Padding         = new Thickness(3, 1, 3, 1),
            BorderBrush     = _borderColor,
            BorderThickness = new Thickness(0, 0, 0.3, 0.3)
        };

        // ── Utilidades ────────────────────────────────────────────────────────
        private static string PlatesCell(CashierCloseoutRow r)
        {
            var p = r.Plates;
            if (!string.IsNullOrEmpty(r.LicState))
                p += $" {r.LicState}";
            return p.Trim();
        }

        private static string TruncateUid(string uid) =>
            uid.Length > 16 ? uid[..16] + "…" : uid;
    }
}
