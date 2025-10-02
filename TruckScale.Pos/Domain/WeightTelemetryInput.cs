namespace TruckScale.Pos.Domain
{
    public sealed class WeightTelemetryInput
    {
        public string? PortName { get; init; }
        public string Brand { get; init; } = "Cardinal";
        public string Mode { get; init; } = "Continuous"; // o "Demand"
        public string Unit { get; init; } = "lb";         // unidad detectada en la trama
        public double WeightLb { get; init; }                 // valor final (ya en lb)
        public string RawLine { get; init; } = "";
        public string MetaJson { get; init; } = "{}";         // JSON con extras (baud, parity, etc.)
    }
}
