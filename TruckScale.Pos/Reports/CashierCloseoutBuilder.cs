using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TruckScale.Pos.Reports
{
    /// <summary>
    /// Builds a WPF <see cref="FlowDocument"/> LETTER (8.5 × 11")
    /// for the "Cashier Closeout / Open &amp; Close" report.
    /// No external dependencies — System.Windows.Documents only.
    /// </summary>
    public static class CashierCloseoutBuilder
    {
        // ── Page constants ───────────────────────────────────────────────────
        private const double DPI    = 96.0;
        private const double PAGE_W = 8.5  * DPI;   // 816 px
        private const double PAGE_H = 11.0 * DPI;   // 1056 px
        private const double MARGIN = 0.5  * DPI;   // 48 px  (½")

        // ── Style ────────────────────────────────────────────────────────────
        private static readonly CultureInfo  _money  = new("en-US");
        private static readonly System.Windows.Media.FontFamily _font = new("Segoe UI");
        private static readonly SolidColorBrush _headerBg    = new(Color.FromRgb(50,  50,  50));
        private static readonly SolidColorBrush _headerFg    = Brushes.White;
        private static readonly SolidColorBrush _altRowBg    = new(Color.FromRgb(245, 247, 250));
        private static readonly SolidColorBrush _voidRowBg   = new(Color.FromRgb(255, 235, 235));
        private static readonly SolidColorBrush _subtotalBg  = new(Color.FromRgb(230, 235, 245));
        private static readonly SolidColorBrush _borderColor = new(Color.FromRgb(190, 190, 190));
        private static readonly SolidColorBrush _canceledFg  = Brushes.DarkRed;

        // ── Detail table columns ─────────────────────────────────────────────
        // Width in GridLength stars (proportional). Total ≈ 8.7 stars.
        private static readonly (string Label, double W, TextAlignment Align)[] _cols =
        {
            ("Time",    0.80, TextAlignment.Left),
            ("Ticket",  0.85, TextAlignment.Left),
            ("Driver",  1.80, TextAlignment.Left),
            ("Plates",  0.85, TextAlignment.Left),
            ("Service", 1.20, TextAlignment.Left),
            ("Payment", 1.10, TextAlignment.Left),
            ("Amount",  0.85, TextAlignment.Right),
            ("Status",  1.00, TextAlignment.Left),
        };

        // ────────────────────────────────────────────────────────────────────
        public static FlowDocument Build(CashierCloseoutData data)
        {
            var doc = new FlowDocument
            {
                PageWidth   = PAGE_W,
                PageHeight  = PAGE_H,
                PagePadding = new Thickness(MARGIN),
                ColumnWidth = double.MaxValue,   // single column
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

        // ── Header ───────────────────────────────────────────────────────────
        private static Block BuildHeader(CashierCloseoutData d)
        {
            var section = new Section { Margin = new Thickness(0, 0, 0, 6) };

            section.Blocks.Add(Para(d.CompanyName,    15,   bold: true,  center: true, margin: new Thickness(0, 0, 0, 1)));
            section.Blocks.Add(Para(d.CompanyAddress, 8.5,              center: true, margin: new Thickness(0)));
            section.Blocks.Add(Para(d.CompanyPhone,   8.5,              center: true, margin: new Thickness(0, 0, 0, 4)));
            section.Blocks.Add(Para("CASHIER CLOSEOUT / OPEN & CLOSE", 13, bold: true, center: true, margin: new Thickness(0, 0, 0, 5)));

            // 2-column key/value table for session info
            var tbl = InfoTable();
            var rg  = new TableRowGroup();
            tbl.RowGroups.Add(rg);

            var openedStr  = d.OpenedAt.HasValue ? d.OpenedAt.Value.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture) : "--";
            var closedStr  = d.ClosedAt.HasValue ? d.ClosedAt.Value.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture) : "--";
            var genStr     = d.GeneratedAt.ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture);

            AddInfoRow(rg, "Generated:",  genStr,          "Terminal:", d.TerminalId.ToString());
            AddInfoRow(rg, "Printed by:", d.OperatorName,  "",          "");
            AddInfoRow(rg, "Opened:",     openedStr,       "Closed:",   closedStr);

            section.Blocks.Add(tbl);
            return section;
        }

        // ── Summary ──────────────────────────────────────────────────────────
        private static Block BuildSummarySection(CashierCloseoutData d)
        {
            var section = new Section { Margin = new Thickness(0, 4, 0, 4) };

            // Totals-by-method table
            var methodTbl = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 4) };
            methodTbl.Columns.Add(Col(3.0));   // Payment Method
            methodTbl.Columns.Add(Col(1.2));   // Collected (Tx)
            methodTbl.Columns.Add(Col(1.2));   // Cancelled
            methodTbl.Columns.Add(Col(1.2));   // Net

            var rg = new TableRowGroup();
            methodTbl.RowGroups.Add(rg);

            // Header row
            var hdr = new TableRow { Background = _headerBg };
            rg.Rows.Add(hdr);
            hdr.Cells.Add(SummaryHdrCell("Payment Method"));
            hdr.Cells.Add(SummaryHdrCell("Collected (Tx)", TextAlignment.Right));
            hdr.Cells.Add(SummaryHdrCell("Cancelled",      TextAlignment.Right));
            hdr.Cells.Add(SummaryHdrCell("Net",            TextAlignment.Right));

            // Data rows
            foreach (var r in d.TotalsByMethod)
            {
                var dataRow = new TableRow();
                rg.Rows.Add(dataRow);
                dataRow.Cells.Add(DataCell(r.MethodName));
                dataRow.Cells.Add(DataCell($"{r.CompletedCount} tx",                                       TextAlignment.Right));
                dataRow.Cells.Add(DataCell(r.CancelledCount > 0 ? $"{r.CancelledCount} void" : "—",       TextAlignment.Right));
                dataRow.Cells.Add(DataCell(r.NetTotal.ToString("C", _money),                               TextAlignment.Right,
                                           bold: true, fg: r.NetTotal < 0 ? _canceledFg : null));
            }

            // Session total row
            var totRow = new TableRow { Background = _subtotalBg };
            rg.Rows.Add(totRow);
            totRow.Cells.Add(DataCell($"SESSION TOTAL  ({d.TotalTransactions} transactions)", bold: true));
            totRow.Cells.Add(DataCell($"{d.CompletedCount} completed",                        TextAlignment.Right, bold: true));
            totRow.Cells.Add(DataCell(d.CancelledCount > 0 ? $"{d.CancelledCount} void" : "—",
                                      TextAlignment.Right, bold: true));
            totRow.Cells.Add(DataCell(d.GrandTotal.ToString("C", _money), TextAlignment.Right,
                                      bold: true, fg: d.GrandTotal < 0 ? _canceledFg : null));

            section.Blocks.Add(Para("SUMMARY", 10, bold: true, margin: new Thickness(0, 0, 0, 3)));
            section.Blocks.Add(methodTbl);

            // Cash reconciliation line
            if (d.OpeningCash != 0 || d.ClosingCash != 0)
            {
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2), FontSize = 8.5 };
                p.Inlines.Add(new Run("Opening Cash: ")  { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(d.OpeningCash.ToString("C", _money)  + "    "));
                p.Inlines.Add(new Run("Expected Cash: ") { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(d.ExpectedCash.ToString("C", _money) + "    "));
                p.Inlines.Add(new Run("Closing Cash: ")  { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(d.ClosingCash.ToString("C", _money)  + "    "));

                var diffStr = d.DiffCash.ToString("C", _money);
                var diffRun = new Run($"Difference: {(d.DiffCash >= 0 ? "+" : "")}{diffStr}");
                if (d.DiffCash < 0) diffRun.Foreground = _canceledFg;
                p.Inlines.Add(new Bold(diffRun));

                section.Blocks.Add(p);
            }

            return section;
        }

        // ── Detail table ─────────────────────────────────────────────────────
        private static Block BuildDetailTable(CashierCloseoutData d)
        {
            var tbl = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };

            foreach (var (_, w, _) in _cols)
                tbl.Columns.Add(Col(w));

            var rg = new TableRowGroup();
            tbl.RowGroups.Add(rg);

            // Header row
            var hdr = new TableRow { Background = _headerBg };
            rg.Rows.Add(hdr);
            foreach (var (label, _, align) in _cols)
                hdr.Cells.Add(DetailHdrCell(label, align));

            // Data rows
            bool alt = false;
            foreach (var r in d.Rows)
            {
                var bg = r.IsCancelled ? _voidRowBg : (alt ? _altRowBg : Brushes.White);
                alt = !alt;

                var row = new TableRow { Background = bg };
                rg.Rows.Add(row);

                var amountFg   = r.IsCancelled ? _canceledFg : (Brush)Brushes.Black;
                var statusFg   = r.IsCancelled ? _canceledFg : (Brush)Brushes.Black;

                row.Cells.Add(DetailDataCell(r.SaleDateTime.ToString("h:mm tt", CultureInfo.InvariantCulture)));
                row.Cells.Add(DetailDataCell(r.TicketNumber));
                row.Cells.Add(DetailDataCell(r.DriverName));
                row.Cells.Add(DetailDataCell(PlatesText(r)));
                row.Cells.Add(DetailDataCell(r.ServiceName));
                row.Cells.Add(DetailDataCell(r.MethodNames));
                row.Cells.Add(DetailDataCell(r.NetAmount.ToString("C", _money), TextAlignment.Right, fg: amountFg));
                row.Cells.Add(DetailDataCell(r.StatusLabel,                     TextAlignment.Left,  fg: statusFg, bold: r.IsCancelled));
            }

            // Footer totals row
            var footRow  = new TableRow { Background = _subtotalBg };
            rg.Rows.Add(footRow);
            var footCell = new TableCell(new Paragraph(
                new Run($"Net total: {d.GrandTotal.ToString("C", _money)}   " +
                        $"({d.CompletedCount} completed, {d.CancelledCount} cancelled)")
                { FontWeight = FontWeights.Bold })
            {
                TextAlignment = TextAlignment.Right,
                Margin        = new Thickness(2, 1, 4, 1),
                FontSize      = 8.5
            })
            {
                ColumnSpan = _cols.Length,
                Padding    = new Thickness(3, 2, 3, 2)
            };
            footRow.Cells.Add(footCell);

            return tbl;
        }

        // ── Footer ───────────────────────────────────────────────────────────
        private static Block BuildFooter(CashierCloseoutData d) =>
            Para($"Generated: {d.GeneratedAt:MM/dd/yyyy h:mm tt} — TruckScale POS",
                 8, center: true, margin: new Thickness(0, 4, 0, 0));

        // ────────────────────────────────────────────────────────────────────
        // Construction helpers
        // ────────────────────────────────────────────────────────────────────

        private static Paragraph Para(string text, double size = 8.5,
            bool bold = false, bool center = false, Thickness? margin = null) =>
            new(new Run(text))
            {
                FontSize      = size,
                FontWeight    = bold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
                Margin        = margin ?? new Thickness(0, 1, 0, 1),
            };

        private static Block HRule() =>
            new Paragraph
            {
                BorderBrush     = _borderColor,
                BorderThickness = new Thickness(0, 0, 0, 0.7),
                Margin          = new Thickness(0, 4, 0, 4),
            };

        private static Block DetailTitle() =>
            Para("TRANSACTION DETAILS", 9.5, bold: true, margin: new Thickness(0, 0, 0, 3));

        private static TableColumn Col(double stars) =>
            new() { Width = new GridLength(stars, GridUnitType.Star) };

        // ── Info table (2-col key/value) ─────────────────────────────────────
        private static Table InfoTable()
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 2) };
            t.Columns.Add(Col(0.8));   // label left
            t.Columns.Add(Col(2.0));   // value left
            t.Columns.Add(Col(0.8));   // label right
            t.Columns.Add(Col(2.0));   // value right
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
                FontWeight    = FontWeights.Bold,
                TextAlignment = align,
                Margin        = new Thickness(2, 0, 2, 0),
                FontSize      = 8.5
            })
        {
            Padding         = new Thickness(4, 3, 4, 3),
            BorderBrush     = Brushes.DarkGray,
            BorderThickness = new Thickness(0, 0, 0, 0.5)
        };

        private static TableCell DataCell(string text,
            TextAlignment align = TextAlignment.Left,
            bool bold = false, Brush? fg = null) => new(
            new Paragraph(new Run(text)
            {
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = fg ?? Brushes.Black
            })
            { TextAlignment = align, Margin = new Thickness(2, 0, 2, 0), FontSize = 8.5 })
        {
            Padding         = new Thickness(4, 2, 4, 2),
            BorderBrush     = _borderColor,
            BorderThickness = new Thickness(0, 0, 0, 0.5)
        };

        // ── Detail table cells ────────────────────────────────────────────────
        private static TableCell DetailHdrCell(string text, TextAlignment align = TextAlignment.Left) => new(
            new Paragraph(new Run(text) { Foreground = _headerFg })
            {
                FontWeight    = FontWeights.Bold,
                TextAlignment = align,
                Margin        = new Thickness(2, 0, 2, 0),
                FontSize      = 8
            })
        {
            Padding         = new Thickness(3, 3, 3, 3),
            BorderBrush     = Brushes.DarkGray,
            BorderThickness = new Thickness(0, 0, 0.3, 0.5)
        };

        private static TableCell DetailDataCell(string text,
            TextAlignment align = TextAlignment.Left,
            Brush? fg = null, bool bold = false) => new(
            new Paragraph(new Run(text)
            {
                Foreground = fg ?? Brushes.Black,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            })
            { TextAlignment = align, Margin = new Thickness(2, 0, 2, 0), FontSize = 8 })
        {
            Padding         = new Thickness(3, 1, 3, 1),
            BorderBrush     = _borderColor,
            BorderThickness = new Thickness(0, 0, 0.3, 0.3)
        };

        // ── Utilities ────────────────────────────────────────────────────────
        private static string PlatesText(CashierCloseoutRow r)
        {
            var p = r.Plates;
            if (!string.IsNullOrEmpty(r.LicState)) p += $" {r.LicState}";
            return p.Trim();
        }

        private static string TruncateUid(string uid) =>
            uid.Length > 16 ? uid[..16] + "…" : uid;
    }
}
