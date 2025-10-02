using System.Threading;
using System.Threading.Tasks;

namespace TruckScale.Pos.Domain
{
    public interface IWeightLogger
    {
        Task<(ulong id, int daySeq)> LogAsync(WeightTelemetryInput input);

        // NUEVO: guardar un evento/sesión de ejes
        Task<long> LogAxleSessionAsync(AxleSession session, CancellationToken ct = default);
    }
}
