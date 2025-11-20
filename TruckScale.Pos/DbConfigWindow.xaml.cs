using System;
using System.Threading.Tasks;
using System.Windows;
using MySqlConnector;
using TruckScale.Pos.Config;

namespace TruckScale.Pos
{
    public partial class DbConfigWindow : Window
    {
        public DbConfigWindow()
        {
            InitializeComponent();

            MainConnText.Text = ConfigManager.Current.MainDbStrCon;
            LocalConnText.Text = ConfigManager.Current.LocalDbStrCon;
        }

        private async void TestMain_Click(object sender, RoutedEventArgs e)
        {
            MainTestResult.Text = "Testing...";
            if (await TestConnectionAsync(MainConnText.Text))
                MainTestResult.Text = "OK";
            else
                MainTestResult.Text = "Error";
        }

        private async void TestLocal_Click(object sender, RoutedEventArgs e)
        {
            LocalTestResult.Text = "Testing...";
            if (await TestConnectionAsync(LocalConnText.Text))
                LocalTestResult.Text = "OK";
            else
                LocalTestResult.Text = "Error";
        }

        private async Task<bool> TestConnectionAsync(string connStr)
        {
            try
            {
                using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Connection error:\n" + ex.Message,
                                "TruckScale POS",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                return false;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Opcional: podrías exigir que al menos la principal pase el test antes de guardar
            ConfigManager.Save(MainConnText.Text, LocalConnText.Text);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
