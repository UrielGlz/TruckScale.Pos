using System;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace TruckScale.Pos.Data
{
    public sealed class MySqlConnectionFactory : IDbConnectionFactory
    {
        private readonly DatabaseConfig _cfg;
        public string TimeZoneId => _cfg.TimeZoneId;

        public MySqlConnectionFactory(DatabaseConfig cfg) => _cfg = cfg;

        // Implementa la firma de la interfaz (sin parámetros)
        public Task<MySqlConnection> CreateOpenConnectionAsync()
            => CreateOpenConnectionAsync(CancellationToken.None);

        // Overload opcional con CancellationToken
        public async Task<MySqlConnection> CreateOpenConnectionAsync(CancellationToken ct)
        {
            MySqlConnection? con = null;
            try
            {
                con = new MySqlConnection(_cfg.ConnectionString);
                await con.OpenAsync(ct).ConfigureAwait(false);

                // Si quieres setear variables de sesión, descomenta:
                // using var cmd = new MySqlCommand("SET SESSION sql_mode='STRICT_TRANS_TABLES';", con);
                // await cmd.ExecuteNonQueryAsync(ct);

                return con;
            }
            catch (MySqlException ex) when (ex.Number == 1045) // access denied
            {
                con?.Dispose();
                throw new InvalidOperationException("MySQL: credenciales inválidas (1045).", ex);
            }
            catch (MySqlException ex) when (ex.Number == 1049) // unknown database
            {
                con?.Dispose();
                throw new InvalidOperationException("MySQL: la base especificada no existe (1049).", ex);
            }
            catch (MySqlException ex) when (ex.Number == 1042 || ex.Number == 2002) // host/port
            {
                con?.Dispose();
                throw new InvalidOperationException("MySQL: no se pudo conectar al host/puerto (1042/2002).", ex);
            }
            catch (MySqlException ex) when (ex.Number == 1040) // too many connections
            {
                con?.Dispose();
                throw new InvalidOperationException("MySQL: demasiadas conexiones (1040).", ex);
            }
            catch (MySqlException ex)
            {
                con?.Dispose();
                throw new InvalidOperationException($"MySQL: error {ex.Number}.", ex);
            }
            catch (TimeoutException ex)
            {
                con?.Dispose();
                throw new InvalidOperationException("MySQL: timeout abriendo la conexión.", ex);
            }
            catch (Exception ex)
            {
                con?.Dispose();
                throw new InvalidOperationException("MySQL: error inesperado al abrir la conexión.", ex);
            }
        }
    }
}
