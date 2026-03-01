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

            try
            {
                // 1) Crear control WinForms SigPlusNET + hostearlo (como demo WPF)
                _pad = new SigPlusNET();

                // Opcional (si tu demo dice Model 11)
                try { _pad.SetTabletModel("11"); } catch { }

                _panel = new System.Windows.Forms.Panel { Width = 1, Height = 1 };
                _panel.Controls.Add(_pad);
                SigHost.Child = _panel;

                // 2) Config de imagen (como demo)
                _pad.SetImageXSize(500);
                _pad.SetImageYSize(100);
                _pad.SetImagePenWidth(6);

                // 3) Arrancar captura AUTOMÁTICA (sin botón Sign)
                _pad.SetTabletState(1);

                // 4) Timer oficial (100ms) para preview
                _timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _timer.Tick += Timer_Tick;
                _timer.Start();
            }
            catch
            {
                // Si falla (no hay pad/driver), cerramos silencioso para no bloquear el flujo POS
                DialogResult = false;
                Close();
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_pad == null) return;

            try
            {
                int pts = _pad.NumberOfTabletPoints();
                if (pts <= 0) return;

                // Evitar repintar demasiado si no cambió
                if (pts == _lastPointCount) return;
                _lastPointCount = pts;

                var img = _pad.GetSigImage();
                if (img == null) return;

                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(ms.ToArray());
                bmp.EndInit();
                bmp.Freeze();

                SigPreview.Source = bmp;
                TxtWaiting.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Si el SDK truena aquí, dejamos que el modal cierre sin bloquear
                // (mejor que tumbar la venta completa)
                DialogResult = false;
                Close();
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (_pad == null) return;

            try
            {
                _pad.ClearTablet();
                _lastPointCount = 0;
                SigPreview.Source = null;
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
            if (_pad == null) return;

            try
            {
                if (_pad.NumberOfTabletPoints() <= 0)
                {
                    MessageBox.Show("Please sign first.", "No signature",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Detener captura como demo
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
                DialogResult = false;
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _timer?.Stop(); } catch { }
            try
            {
                if (_pad != null)
                    _pad.SetTabletState(0);
            }
            catch { }

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
            _timer = null;

            base.OnClosed(e);
        }
    }
}