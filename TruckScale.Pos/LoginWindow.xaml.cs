using System.Windows;
using System.Windows.Input;
using MySqlConnector;
using TruckScale.Pos.Config;
using System.Security.Cryptography;
using System.Text;


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
                if (user == null)
                {
                    LoginErrorText.Text = "Invalid user or password.";// Lun1s090513@ | Admin2025! | Operator2025!
                    LoginErrorText.Visibility = Visibility.Visible;
                    return;
                }

                PosSession.UserId = user.UserId;
                PosSession.Username = user.Username;
                PosSession.FullName = user.FullName;
                PosSession.RoleCode = user.RoleCode;

                var main = new MainWindow();
                Application.Current.MainWindow = main;
                main.Show();
                this.Close();
            }
            catch (Exception ex)
            {
                LoginErrorText.Text = "Error connecting to database. " + ex.Message;
                LoginErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                // Si la ventana sigue viva (no hubo login exitoso), reactivamos el botón
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

            string connStr = MainWindow.GetConnectionString(); // o el método que uses ahora

            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            const string SQL = @"SELECT user_id, username, full_name, role_code,password_hash, password_algo, is_active FROM users WHERE username = @u LIMIT 1;";

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
