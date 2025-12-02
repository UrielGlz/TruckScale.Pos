using System.Text.Json.Serialization;

namespace TruckScale.Pos.Tickets
{
    public class QrPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("ticket_uid")]
        public string TicketUid { get; set; } = "";

        [JsonPropertyName("sale_uid")]
        public string SaleUid { get; set; } = "";

        [JsonPropertyName("ticket_number")]
        public string TicketNumber { get; set; } = "";

        [JsonPropertyName("total")]
        public decimal Total { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "USD";

        [JsonPropertyName("dt")]
        public string DateTimeIso { get; set; } = "";
    }
}
