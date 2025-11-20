using System.Windows;

namespace TruckScale.Pos
{
    public partial class AdminPasswordWindow : Window
    {
        public string Password => PwdBox.Password;

        public AdminPasswordWindow()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
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
