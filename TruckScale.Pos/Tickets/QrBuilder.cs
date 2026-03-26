using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TruckScale.Pos.Tickets
{
    /// <summary>
    /// Centralized QR payload logic — the single place that defines how the QR
    /// is built and how a scan/manual entry is parsed.
    ///
    /// Format: {"ticket_uid":"…","ticket_number":"…","created_at":"…"}
    /// </summary>
    public static class QrBuilder
    {
        private static readonly JsonSerializerOptions _serOpts =
            new() { WriteIndented = false };

        // ── Build ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the QR payload string for a printed ticket.
        /// </summary>
        /// <param name="ticketUid">UUID of the ticket row (preferred lookup key).</param>
        /// <param name="ticketNumber">Human-readable ticket number (manual lookup fallback).</param>
        /// <param name="createdAt">Sale creation datetime from <c>sales.created_at</c>.</param>
        public static string Build(string ticketUid, string ticketNumber, DateTime createdAt)
        {
            var payload = new QrPayload
            {
                TicketUid    = ticketUid,
                TicketNumber = ticketNumber,
                CreatedAt    = createdAt > DateTime.MinValue
                    ? createdAt.ToUniversalTime()
                               .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                    : ""
            };
            return JsonSerializer.Serialize(payload, _serOpts);
        }

        // ── Parse ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses raw scanner input (QR JSON) or a manually typed ticket number.
        /// </summary>
        /// <returns>
        /// (ticketUid, ticketNumber) — either value may be null.
        /// QR scans return a non-null <paramref name="ticketUid"/> for precise DB lookup.
        /// Manual entries return only <paramref name="ticketNumber"/>.
        /// </returns>
        public static (string? ticketUid, string? ticketNumber) Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return (null, null);

            raw = raw.Trim();

            return raw.StartsWith("{", StringComparison.Ordinal)
                ? ParseJson(raw)
                : (null, NormalizeNumber(Regex.Replace(raw, @"\s+", "").ToUpperInvariant()));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static (string? ticketUid, string? ticketNumber) ParseJson(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                string? GetStr(params string[] names)
                {
                    foreach (var n in names)
                    {
                        if (root.TryGetProperty(n, out var p) &&
                            p.ValueKind == JsonValueKind.String)
                        {
                            var v = p.GetString();
                            if (!string.IsNullOrWhiteSpace(v))
                                return v!.Trim();
                        }
                    }
                    return null;
                }

                // Accept both new (ticket_uid) and legacy (ticketUid) key names
                var uid = GetStr("ticket_uid", "ticketUid");
                var num = NormalizeNumber(GetStr("ticket_number", "ticketNumber", "ticket"));
                return (uid, num);
            }
            catch
            {
                // Malformed JSON — salvage ticket_number via regex, discard any UID
                var m = Regex.Match(raw, @"ticket_number[^0-9]{1,15}(\d+)",
                                    RegexOptions.IgnoreCase);
                return m.Success ? (null, NormalizeNumber(m.Groups[1].Value)) : (null, null);
            }
        }

        private static string? NormalizeNumber(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var s = input.Trim().ToUpperInvariant();
            if (s.StartsWith("TS-", StringComparison.Ordinal))      s = s[3..];
            else if (s.StartsWith("TS", StringComparison.Ordinal))  s = s[2..];

            return long.TryParse(s, out var n) ? n.ToString() : s;
        }
    }
}
