using System.Text.Json.Serialization;

namespace TruckScale.Pos.Tickets
{
    /// <summary>
    /// Minimal QR payload. Built and parsed exclusively via <see cref="QrBuilder"/>.
    /// </summary>
    internal sealed class QrPayload
    {
        [JsonPropertyName("ticket_uid")]
        public string TicketUid { get; set; } = "";

        [JsonPropertyName("ticket_number")]
        public string TicketNumber { get; set; } = "";

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = "";
    }
}
