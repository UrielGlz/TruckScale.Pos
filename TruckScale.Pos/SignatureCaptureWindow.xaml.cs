using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TruckScale.Pos.Services;

namespace TruckScale.Pos
{
    public partial class SignatureCaptureWindow : Window
    {
        private readonly SignaturePadService _service;
        private readonly DispatcherTimer    _previewTimer;

        /// <summary>Bytes PNG de la firma capturada. Null/vacío si el usuario omitió.</summary>
        public byte[]? SignatureBytes { get; private set; }

        public SignatureCaptureWindow(SignaturePadService service, string ticketNumber, decimal total)
        {
            InitializeComponent();

            _service = service;

            TxtTicketNum.Text = ticketNumber;
            TxtTotal.Text = total.ToString("C", CultureInfo.GetCultureInfo("en-US"));

            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _previewTimer.Tick += PreviewTimer_Tick;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            _service.StartCapture("Please sign below", $"Ticket: {TxtTicketNum.Text}");
            _previewTimer.Start();
        }

        private void PreviewTimer_Tick(object? sender, EventArgs e)
        {
            if (!_service.HasSignature()) return;

            var bytes = _service.GetSignaturePng();
            if (bytes.Length == 0) return;

            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                SigPreview.Source       = bmp;
                TxtWaiting.Visibility   = Visibility.Collapsed;
                BtnDone.IsEnabled       = true;
            }
            catch { }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _service.ClearSignature();
            SigPreview.Source     = null;
            TxtWaiting.Visibility = Visibility.Visible;
            BtnDone.IsEnabled     = false;
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnDone_Click(object sender, RoutedEventArgs e)
        {
            SignatureBytes = _service.GetSignaturePng();
            DialogResult   = true;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _previewTimer.Stop();
            _service.StopCapture();
            base.OnClosed(e);
        }
    }
}
