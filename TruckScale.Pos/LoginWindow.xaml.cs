using System.Windows;
using System.Windows.Input;
using MySqlConnector;
using TruckScale.Pos.Config;

namespace TruckScale.Pos
{
    public partial class LoginWindow : Window
    {
        private const string AdminConfigPassword = "Lun1s090513@";

        public LoginWindow()
        {
            InitializeComponent();

            // Cargar configuración al iniciar la app
            ConfigManager.Load();

            if (!ConfigManager.HasMainConnection)
            {
                ConfigHintText.Text = "Database not configured. Press CTRL + ALT + F8 to configure.";
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
            LoginErrorText.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(UserTextBox.Text) ||
                string.IsNullOrWhiteSpace(PasswordBox.Password))
            {
                LoginErrorText.Text = "Enter user and password.";
                LoginErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (!ConfigManager.HasMainConnection)
            {
                LoginErrorText.Text = "Database not configured. Press CTRL + ALT + F8.";
                LoginErrorText.Visibility = Visibility.Visible;
                return;
            }

            // TODO: aquí va la validación real contra la DB principal
            // Por ahora, puedes dejar un login dummy:
            bool ok = await FakeValidateAsync(UserTextBox.Text, PasswordBox.Password);
            if (!ok)
            {
                LoginErrorText.Text = "Invalid user or password.";
                LoginErrorText.Visibility = Visibility.Visible;
                return;
            }

            var main = new MainWindow();
            main.Show();
            Close();
        }

        private Task<bool> FakeValidateAsync(string user, string pass)
        {
            // Por ahora acepta solo admin/admin o todo distinto de vacío
            bool ok = user.Equals("admin", StringComparison.OrdinalIgnoreCase)
                      && pass == "admin";
            return Task.FromResult(ok);
        }
    }
}
