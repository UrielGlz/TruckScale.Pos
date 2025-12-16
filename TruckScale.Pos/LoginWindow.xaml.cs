using System.Windows;
using System.Windows.Input;
using MySqlConnector;
using TruckScale.Pos.Config;
using System.Security.Cryptography;
using System.Text;
using TruckScale.Pos.Config;


namespace TruckScale.Pos
{
    internal sealed class DbUser
    {
        public int UserId { get; init; }
        public string Username { get; init; } = "";
        public string FullName { get; init; } = "";
        public string RoleCode { get; init; } = "";
        public string PasswordHash { get; init; } = "";
        public string PasswordAlgo { get; init; } = "";
        public bool IsActive { get; init; }
    }

    public partial class LoginWindow : Window
    {
        private const string AdminConfigPassword = "Lun1s090513@";
        private bool _dbConfigured = false;
        private bool _isLoggingIn = false;


        public LoginWindow()
        {
            InitializeComponent();

            // Cargar configuración al iniciar la app
            ConfigManager.Load();

            if (!ConfigManager.HasMainConnection)
            {
                //ConfigHintText.Text = "Database not configured. Press CTRL + ALT + F8 to configure.";
            }
        }
        private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var cfg = PosConfigService.Load();
            var mainConn = cfg.MainDbStrCon;
            UserTextBox.Focus();
            UserTextBox.SelectAll();
            if (string.IsNullOrWhiteSpace(mainConn))
            {
                // No hay cadena en config.json
                _dbConfigured = false;
                //ConfigHintText.Text = "Database not configured. Press CTRL + ALT + F8 for configuration.";
                //ConfigHintText.Visibility = Visibility.Visible;
                return;
            }

            // Probar conexión rápida
            if (await CanConnectAsync(mainConn))
            {
                _dbConfigured = true;
                //ConfigHintText.Visibility = Visibility.Collapsed;
            }
            else
            {
                _dbConfigured = false;
                //ConfigHintText.Text = "Database connection failed. Press CTRL + ALT + F8 to configure.";
                //ConfigHintText.Visibility = Visibility.Visible;
            }
        }

        private async Task<bool> CanConnectAsync(string connStr)
        {
            try
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F8 &&
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                e.Handled = true;
                ShowConfigPasswordDialog();
            }

        }

        private void ShowConfigPasswordDialog()
        {
            // Ventanita sencilla para pedir la clave admin
            var dlg = new AdminPasswordWindow(); // la definimos abajo
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                if (dlg.Password == AdminConfigPassword)
                {
                    var cfgWin = new DbConfigWindow();
                    cfgWin.Owner = this;
                    cfgWin.ShowDialog();
                }
                else
                {
                    MessageBox.Show("Incorrect admin password.", "TruckScale POS",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoggingIn) return;
            _isLoggingIn = true;
            LoginButton.IsEnabled = false;

            LoginErrorText.Visibility = Visibility.Collapsed;

            var username = UserTextBox.Text.Trim();
            var password = PasswordBox.Password;

            try
            {
                var user = await AuthenticateAsync(username, password);

                // Caso 1: conexión OK, pero credenciales de app (admin / operador) malas
                if (user == null)
                {
                    LoginErrorText.Text = "Invalid username or password.";
                    LoginErrorText.Visibility = Visibility.Visible;
                    return;
                }

                // Login correcto
                PosSession.UserId = user.UserId;
                PosSession.Username = user.Username;
                PosSession.FullName = user.FullName;
                PosSession.RoleCode = user.RoleCode;

                var main = new MainWindow();
                Application.Current.MainWindow = main;
                main.Show();
                this.Close();
            }
            catch (MySqlException dbEx)
            {
                // Caso 2: problemas con la BD (credenciales del servidor, red, etc.)
                string msg;

                switch (dbEx.Number)
                {
                    case 1042: // Can't connect to MySQL server
                        msg = "Unable to connect to the database server. Please check your network connection or contact IT Support.";
                        break;
                    case 1045: // Access denied for user 'xxx'@'xxx'
                        msg = "Database access denied. Please contact IT Support to review the configuration.";
                        break;
                    default:
                        msg = "A database error occurred. Please contact IT Support.";
                        break;
                }

                LoginErrorText.Text = msg;
                LoginErrorText.Visibility = Visibility.Visible;

                // TODO (opcional): loguear dbEx.ToString() a un archivo o EventLog
            }
            catch (Exception)
            {
                // Caso 3: error raro no previsto
                LoginErrorText.Text = "An unexpected error occurred while logging in. Please contact IT Support.";
                LoginErrorText.Visibility = Visibility.Visible;

                // TODO (opcional): loguear el detalle
            }
            finally
            {
                if (IsLoaded)
                {
                    _isLoggingIn = false;
                    LoginButton.IsEnabled = true;
                }
            }
        }


        private async Task<DbUser?> AuthenticateAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return null;

            // Cargar config (online + local)
            ConfigManager.Load();

            bool anyConnectionSucceeded = false;
            Exception? lastConnException = null;

            // 1) ONLINE (MainDbStrCon)
            if (!string.IsNullOrWhiteSpace(ConfigManager.Current.MainDbStrCon))
            {
                try
                {
                    var userOnline = await AuthenticateWithConnectionAsync(
                        ConfigManager.Current.MainDbStrCon,
                        username,
                        password);

                    // Si la conexión abrió y la query corrió, marcamos éxito de conexión
                    anyConnectionSucceeded = true;

                    // Si el usuario existe y el password es correcto, regresamos
                    if (userOnline != null)
                        return userOnline;

                    // Si es null, seguimos para probar local (puede haber usuario solo local)
                }
                catch (Exception ex)
                {
                    lastConnException = ex;
                }
            }

            // 2) LOCAL (LocalDbStrCon)
            if (!string.IsNullOrWhiteSpace(ConfigManager.Current.LocalDbStrCon))
            {
                try
                {
                    var userLocal = await AuthenticateWithConnectionAsync(
                        ConfigManager.Current.LocalDbStrCon,
                        username,
                        password);

                    anyConnectionSucceeded = true;

                    if (userLocal != null)
                        return userLocal;
                }
                catch (Exception ex)
                {
                    lastConnException = ex;
                }
            }

            // === Decisión final ===

            if (anyConnectionSucceeded)
            {
                // Al menos una BD respondió, pero en ninguna hubo match de user/pass
                // -> para el operador es claramente "usuario o contraseña incorrectos"
                return null;
            }

            // Si llegamos aquí, NINGUNA BD se pudo conectar (online ni local)
            if (lastConnException != null)
                throw lastConnException;

            // Caso extremo: ni siquiera hay cadenas de conexión configuradas
            throw new InvalidOperationException("No database connection is configured.");
        }

        // Helper reutilizable para no duplicar lógica
        private static async Task<DbUser?> AuthenticateWithConnectionAsync(
            string connStr, string username, string password)
        {
            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            const string SQL = @"SELECT user_id, username, full_name, role_code,
                                password_hash, password_algo, is_active
                         FROM users
                         WHERE username = @u
                         LIMIT 1;";

            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@u", username);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
                return null;

            bool isActive = rd.GetBoolean("is_active");
            if (!isActive) return null;

            string storedHash = rd.GetString("password_hash");
            string algo = rd.GetString("password_algo");

            string inputHash;
            if (string.Equals(algo, "SHA256", StringComparison.OrdinalIgnoreCase))
            {
                inputHash = ComputeSha256(password);
            }
            else
            {
                // por ahora solo soportamos SHA256
                return null;
            }

            if (!string.Equals(storedHash, inputHash, StringComparison.OrdinalIgnoreCase))
                return null;

            return new DbUser
            {
                UserId = rd.GetInt32("user_id"),
                Username = rd.GetString("username"),
                FullName = rd.GetString("full_name"),
                RoleCode = rd.GetString("role_code"),
                PasswordHash = storedHash,
                PasswordAlgo = algo,
                IsActive = isActive
            };
        }


        private static string ComputeSha256(string raw)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(raw);
            var hashBytes = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hashBytes.Length * 2);
            foreach (var b in hashBytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
