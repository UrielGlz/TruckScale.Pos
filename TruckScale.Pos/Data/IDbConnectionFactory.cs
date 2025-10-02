using MySqlConnector;
using System.Threading;
using System.Threading.Tasks;

namespace TruckScale.Pos.Data
{
    public interface IDbConnectionFactory
    {
        string TimeZoneId { get; }
        Task<MySqlConnection> CreateOpenConnectionAsync();
        Task<MySqlConnection> CreateOpenConnectionAsync(CancellationToken ct);
    }
}
