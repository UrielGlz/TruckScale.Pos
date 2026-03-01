using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Topaz;

namespace TruckScale.Pos
{
    public partial class SignatureCaptureWindow : Window
    {
        private SigPlusNET? _pad;
        private System.Windows.Forms.Panel? _panel;
        private DispatcherTimer? _timer;
        private int _lastPointCount = 0;
        private bool _connected = false;

        public byte[]? SignatureBytes { get; private set; }

        public SignatureCaptureWindow(string ticketNumber, decimal total)
        {
            InitializeComponent();
            TxtTicketNum.Text = ticketNumber;
            TxtTotal.Text = total.ToString("C", CultureInfo.GetCultureInfo("en-US"));
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            InitializePad(startCapture: true);
        }

        private void InitializePad(bool startCapture)
        {
            CleanupPad();

            TxtWaiting.Text = "Sign on the pad…";
            TxtWaiting.Visibility = Visibility.Visible;
            SigPreview.Source = null;
            _lastPointCount = 0;
            _connected = false;

            try
            {
                _pad = new SigPlusNET();

                // Model según tu demo (si no aplica, no truena)
                try { _pad.SetTabletModel("11"); } catch { }

                _panel = new System.Windows.Forms.Panel { Width = 1, Height = 1 };
                _panel.Controls.Add(_pad);
                SigHost.Child = _panel;

                // Config imagen (como demo)
                _pad.SetImageXSize(500);
                _pad.SetImageYSize(100);
                _pad.SetImagePenWidth(6);

                // Detecta si está conectado AHORA
                try { _connected = _pad.TabletConnectQuery(); } catch { _connected = false; }

                if (!_connected)
                {
                    TxtWaiting.Text = "Signature pad not detected. Plug it in and click Retry.";
                    StopTimer();
                    return;
                }

                if (startCapture)
                    _pad.SetTabletState(1);

                StartTimer();
            }
            catch
            {
                // Si algo falla, no bloqueamos: dejamos Skip disponible
                TxtWaiting.Text = "Signature pad not available. Click Retry or press Skip.";
                StopTimer();
            }
        }

        private void StartTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += Timer_Tick;
            }
            _timer.Start();
        }

        private void StopTimer()
        {
            try { _timer?.Stop(); } catch { }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_pad == null) return;

            // Si se desconectó en caliente, entra aquí:
            try
            {
                if (!_pad.TabletConnectQuery())
                {
                    _connected = false;
                    TxtWaiting.Text = "Signature pad disconnected. Plug it in and click Retry.";
                    TxtWaiting.Visibility = Visibility.Visible;
                    StopTimer();
                    try { _pad.SetTabletState(0); } catch { }
                    return;
                }
            }
            catch
            {
                _connected = false;
                TxtWaiting.Text = "Signature pad disconnected. Plug it in and click Retry.";
                TxtWaiting.Visibility = Visibility.Visible;
                StopTimer();
                return;
            }

            try
            {
                int pts = _pad.NumberOfTabletPoints();
                if (pts <= 0) return;
                if (pts == _lastPointCount) return;
                _lastPointCount = pts;

                var img = _pad.GetSigImage();
                if (img == null) return;

                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var png = ms.ToArray();

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();

                SigPreview.Source = bmp;
                TxtWaiting.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Si el SDK truena aquí, dejamos el modal vivo para Retry/Skip
                TxtWaiting.Text = "Signature capture error. Click Retry or press Skip.";
                TxtWaiting.Visibility = Visibility.Visible;
                StopTimer();
                try { _pad.SetTabletState(0); } catch { }
                _connected = false;
            }
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            InitializePad(startCapture: true);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (_pad == null || !_connected) return;
            try
            {
                _pad.ClearTablet();
                _lastPointCount = 0;
                SigPreview.Source = null;
                TxtWaiting.Text = "Sign on the pad…";
                TxtWaiting.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnDone_Click(object sender, RoutedEventArgs e)
        {
            if (_pad == null || !_connected)
            {
                MessageBox.Show(
                    "Signature pad not detected.\nPlug it in and click Retry, or press Skip to continue without signature.",
                    "Signature",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                if (_pad.NumberOfTabletPoints() <= 0)
                {
                    MessageBox.Show("Please sign first.", "No signature",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _pad.SetTabletState(0);

                var img = _pad.GetSigImage();
                if (img == null)
                {
                    MessageBox.Show("Could not read signature image.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                SignatureBytes = ms.ToArray();

                DialogResult = true;
                Close();
            }
            catch
            {
                MessageBox.Show("Signature capture failed. Click Retry or press Skip.",
                    "Signature",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            CleanupPad();
            base.OnClosed(e);
        }

        private void CleanupPad()
        {
            StopTimer();

            try { _pad?.SetTabletState(0); } catch { }

            try
            {
                if (_panel != null && _pad != null)
                    _panel.Controls.Remove(_pad);
            }
            catch { }

            try { SigHost.Child = null; } catch { }

            try { _pad?.Dispose(); } catch { }

            _pad = null;
            _panel = null;
            _connected = false;
        }
    }
}