using System;
using System.Collections.Generic;

namespace TruckScale.Pos.Domain
{
    public sealed class AxleReading
    {
        public int Index { get; init; }
        public DateTime UtcTime { get; init; }
        public double WeightLb { get; init; }
        public double WeightKg => WeightLb / 2.20462262185;
    }

    public sealed class AxleSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public DateTime UtcStart { get; internal set; }
        public DateTime? UtcEnd { get; internal set; }
        public List<AxleReading> Axles { get; } = new();
        public double TotalLb { get; internal set; }
        public double TotalKg => TotalLb / 2.20462262185;
        public bool IsCompleted => UtcEnd != null;
    }
}
