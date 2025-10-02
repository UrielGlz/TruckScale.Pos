using System;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TruckScale.Pos
{
    public partial class SerialConsoleWindow : Window
    {
        private SerialPort? _port;

        // ---- Auto-verificación de tráfico tras conectar ----
        private CancellationTokenSource? _verifyCts;
        private int _bytesSinceConnect = 0;
        private const int VERIFY_TIMEOUT_MS = 3000;   // igual que VerifyTimeoutMs
        private const int MIN_VALID_BYTES = 1;        // igual que MinValidSamples

        // Guardamos el puerto/baud “abierto” para reflejar en la UI durante verificación
        private string? _openPortName;
        private int? _openBaud;

        public SerialConsoleWindow()
        {
            InitializeComponent();
            Loaded += SerialConsoleWindow_Loaded;
        }

        private void SerialConsoleWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadPorts();
            LoadBauds();
            TrySelectDefaults();
        }

        private void LoadPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            PortCombo.ItemsSource = ports;
            if (ports.Length > 0 && PortCombo.SelectedItem == null)
                PortCombo.SelectedIndex = 0;
        }

        private void LoadBauds()
        {
            // Valores típicos
            var bauds = new[] { 9600, 19200, 38400, 57600, 115200, 4800, 2400, 1200 };
            BaudCombo.ItemsSource = bauds;
            // IDE0074: asignación compuesta
            BaudCombo.SelectedItem ??= 9600;
        }

        private void TrySelectDefaults()
        {
            // Si tienes una config previa (COM3/9600), selecciónala si existe
            var preferPort = "COM3";
            var preferBaud = 9600;

            if (PortCombo.Items.Contains(preferPort))
                PortCombo.SelectedItem = preferPort;

            if (BaudCombo.Items.Contains(preferBaud))
                BaudCombo.SelectedItem = preferBaud;
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e) => LoadPorts();

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_port != null && _port.IsOpen) return;

            var portName = PortCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(portName))
            {
                MessageBox.Show("Selecciona un puerto COM.", "Serial Console",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (BaudCombo.SelectedItem is not int baud) baud = 9600;

            try
            {
                _port = new SerialPort(portName, baud)
                {
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    Handshake = Handshake.None,
                    DtrEnable = true, // como tu config
                    RtsEnable = true, // como tu config
                    NewLine = "\r\n",
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                _port.DataReceived += Port_DataReceived;
                _port.ErrorReceived += Port_ErrorReceived;
                _port.Open();

                // Guardar estado de lo abierto y preparar verificación
                _openPortName = portName;
                _openBaud = baud;
                _bytesSinceConnect = 0;

                _verifyCts?.Cancel();
                _verifyCts = new CancellationTokenSource();

                // UI en modo "Conectando/verificando"
                SetUiConnecting(_openPortName, _openBaud);
                Append($"[OK] Conectado a {portName} @ {baud}.\r\n");
                Append($"[i] Esperando datos durante {VERIFY_TIMEOUT_MS} ms...\r\n");

                _ = VerifyAfterConnectAsync(_verifyCts.Token);
            }
            catch (Exception ex)
            {
                Append($"[ERR] No se pudo abrir {portName}: {ex.Message}\r\n");
                SafeClose();
                SetUiConnected(false, null, null);
            }
        }

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            SafeClose();
            SetUiConnected(false, null, null);
            Append("[i] Desconectado.\r\n");
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;
                // Lee lo disponible sin bloquear
                var chunk = sp.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    // Contamos bytes para la verificación
                    _bytesSinceConnect += chunk.Length;
                    Append(chunk);
                }
            }
            catch (Exception ex)
            {
                Append($"[ERR] DataReceived: {ex.Message}\r\n");
            }
        }

        private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Append($"[WARN] ErrorReceived: {e.EventType}\r\n");
        }

        private async Task VerifyAfterConnectAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(VERIFY_TIMEOUT_MS, ct);
                if (ct.IsCancellationRequested) return;

                if (_bytesSinceConnect < MIN_VALID_BYTES)
                {
                    Append($"[WARN] No se recibieron datos en {VERIFY_TIMEOUT_MS} ms. " +
                           $"¿Indicador conectado? ¿Modo Continuous/Print? ¿Necesitas cable null-modem?\r\n");

                    // Si no hay tráfico, cerramos y regresamos UI a "Desconectado"
                    SafeClose();
                    SetUiConnected(false, null, null);
                }
                else
                {
                    Append("[OK] Verificación: se recibió tráfico del dispositivo.\r\n");
                    // Verificación OK -> estado Conectado real
                    SetUiConnected(true, _openPortName, _openBaud);
                }
            }
            catch (TaskCanceledException)
            {
                // Ignorar: el usuario desconectó antes de terminar la verificación
            }
        }

        private void Append(string text)
        {
            _ = this.Dispatcher.BeginInvoke(new Action(() =>
            {
                var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                LogBox.AppendText($"[{ts}] {text}");
                if (!text.EndsWith("\r\n")) LogBox.AppendText("\r\n");
                LogBox.ScrollToEnd();
            }));
        }

        // --- Estados de UI ---
        private void SetUiConnecting(string? port, int? baud)
        {
            StatusText.Text = "Conectando (verificando)...";
            PortInfoText.Text = (port != null && baud != null) ? $"{port} @ {baud}" : "-";

            // Mientras verificamos: no permitir reconectar, sí permitir desconectar
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;
            PortCombo.IsEnabled = false;
            BaudCombo.IsEnabled = false;
            RefreshBtn.IsEnabled = false;
        }

        private void SetUiConnected(bool connected, string? port, int? baud)
        {
            StatusText.Text = connected ? "Conectado" : "Desconectado";
            PortInfoText.Text = connected && port != null ? $"{port} @ {baud}" : "-";

            ConnectBtn.IsEnabled = !connected;
            DisconnectBtn.IsEnabled = connected;
            PortCombo.IsEnabled = !connected;
            BaudCombo.IsEnabled = !connected;
            RefreshBtn.IsEnabled = !connected;
        }

        private void SafeClose()
        {
            try
            {
                _verifyCts?.Cancel();
                _verifyCts = null;

                if (_port != null)
                {
                    _port.DataReceived -= Port_DataReceived;
                    _port.ErrorReceived -= Port_ErrorReceived;
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                }
            }
            catch { /* ignore */ }
            finally
            {
                _port = null;
                _openPortName = null;
                _openBaud = null;
                _bytesSinceConnect = 0;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SafeClose();
            base.OnClosed(e);
        }
    }
}
