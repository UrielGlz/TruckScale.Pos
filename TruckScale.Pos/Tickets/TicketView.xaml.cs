using System.Windows;
using System.Windows.Controls;

namespace TruckScale.Pos.Tickets
{
    public partial class TicketView : UserControl
    {
        public TicketView()
        {
            InitializeComponent();
            //DataContextChanged += TicketView_DataContextChanged;
        }

        private void TicketView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is TicketData td)
            {
                ReweighPanel.Visibility = td.IsReweigh ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                ReweighPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
