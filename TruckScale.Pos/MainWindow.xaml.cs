using MaterialDesignThemes.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TruckScale.Pos.Data;
using TruckScale.Pos.Domain;

namespace TruckScale.Pos
{
    // ===== Teclado / pagos (UI) =====
    public class KeypadConfig
    {
        public int Columns { get; set; } = 3;
        public string[] Keys { get; set; } = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "00", "←" };
        public decimal[] Denominations { get; set; } = new decimal[] { 1, 5, 10, 20, 50, 100, 200, 500 };
    }
    public class PaymentRow
    {
        public string Metodo { get; set; } = "";
        public decimal Monto { get; set; }
    }
    public enum UnitSystem { Metric, Imperial }

    public partial class MainWindow : Window
    {
        // Tema
        private readonly PaletteHelper _palette = new();
        private bool _dark = false;

        // Cultura / unidades 
        private readonly CultureInfo _moneyCulture = new("en-US");
        private UnitSystem _units = UnitSystem.Imperial;

        // UI: keypad / pagos
        private KeypadConfig _kp = new();
        private string _keypadBuffer = "";
        public string KeypadText => string.IsNullOrEmpty(_keypadBuffer) ? "0" : _keypadBuffer;
        private string _selectedPaymentId = "cash";
        private readonly List<Button> _paymentButtons = new();
        readonly ObservableCollection<PaymentRow> _pagos = new();

        // DB / estado (usa tu clase nueva Domain/WeightLogger.cs)
        private WeightLogger? _logger;
        private bool _dbErrorShown = false;

        // Estado de sesión mostrada en UI
        private int _axleCount = 0;
        private double _sessionTotalLb = 0;
        private bool _sessionActive = false;
        private readonly ObservableCollection<string> _recentSessions = new();
        private readonly ObservableCollection<string> _currentAxles = new();

        // === Serial simple al estilo "ScaleTesting" ===
        private SerialPort? _port;

        // Config mínima (se lee de C:\TruckScale\serial.txt)
        private string _portName = "COM3";
        private int _baud = 9600;
        private double _minValidWeightLb = 300;

        // Últimos valores por canal (0,1,2 = ejes; 3 = total)
        private readonly Dictionary<int, double> _last = new() { { 0, 0 }, { 1, 0 }, { 2, 0 }, { 3, 0 } };
        private readonly Dictionary<int, string> _lastTail = new() { { 0, "" }, { 1, "" }, { 2, "" }, { 3, "" } };
        // Guardamos también la línea cruda por canal para usarla como raw_line en detalle
        private readonly Dictionary<int, string> _lastRaw = new() { { 0, "" }, { 1, "" }, { 2, "" }, { 3, "" } };

        private double _ultimoTotalGuardado = 0;

        // Tolerancias/umbral 
        private const double MAX_ERROR_LB = 100;
        private const double MIN_CHANGE_LB = 200;

        // Regex EXACTA de tu prueba (solo enteros)
        private static readonly System.Text.RegularExpressions.Regex rx =
        new(@"^%(?<ch>\d)\s*(?<w>\d+)\s*lbG(?<tail>[GR]+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private const double MAX_ERROR = 100;   // |(e0+e1+e2) - total|
        private const double MIN_CAMBIO = 200;  // umbral para registrar una nueva sesión

        public MainWindow()
        {
            InitializeComponent();

            ApplyTheme();
            ApplyBrand();
            StartSerialFromConfig();   // abre COM según serial.txt

            // UI básica
            ApplyUi();

            LoadKeypadConfig();
            BuildKeypadUI();
            AxlesList.ItemsSource = _currentAxles;
            RecientesList.ItemsSource = _recentSessions;
        }

        // ===== Localización / UI básica =====
        private string T(string key) => (TryFindResource(key) as string) ?? key;
        private void ApplyUi()
        {
            UpdateWeightText(0); // arranca en 0 lb
            try { RecientesList.ItemsSource = _recentSessions; } catch { }
        }
        private void ApplyTheme()
        {
            var theme = _palette.GetTheme();
            theme.SetBaseTheme(_dark ? BaseTheme.Dark : BaseTheme.Light);
            _palette.SetTheme(theme);
        }
        private void ApplyBrand()
        {
            var theme = _palette.GetTheme();
            theme.SetPrimaryColor((Color)ColorConverter.ConvertFromString("#455A64"));
            theme.SetSecondaryColor((Color)ColorConverter.ConvertFromString("#26A69A"));
            _palette.SetTheme(theme);
        }
        private void ToggleTheme_Click(object sender, RoutedEventArgs e) { _dark = !_dark; ApplyTheme(); }

        // ===== Monitor de peso (label grande) =====
        private void UpdateWeightText(double lb)
        {
            // Si hay sesión aceptada, muestra el total congelado
            double valToShowLb = (_sessionActive || _axleCount > 0) ? _sessionTotalLb : lb;

            // Usa _units para evitar la advertencia CS0414
            string suffix = (_units == UnitSystem.Imperial) ? "lb" : "kg";
            double value = (_units == UnitSystem.Imperial) ? valToShowLb : (valToShowLb / 2.20462262185);

            if (WeightText != null) WeightText.Text = $"{value:0,0.0} {suffix}";
        }

        private void Zero_Click(object sender, RoutedEventArgs e)
        {
            _sessionActive = false; _axleCount = 0; _sessionTotalLb = 0;
            _currentAxles.Clear();
            UpdateWeightText(0);
        }
        private void Tara_Click(object sender, RoutedEventArgs e) { }

        // ===== Keypad / pagos  =====
        private void ToggleDrawer_Click(object sender, RoutedEventArgs e) => RootDrawerHost.IsLeftDrawerOpen = !RootDrawerHost.IsLeftDrawerOpen;
        private void LoadKeypadConfig()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "keypad.json");
                _kp = File.Exists(path) ? (System.Text.Json.JsonSerializer.Deserialize<KeypadConfig>(File.ReadAllText(path)) ?? new()) : new();
            }
            catch { _kp = new(); }
        }
        private void BuildKeypadUI()
        {
            try
            {
                DenomsHost.ItemsSource = _kp.Denominations;
                KeysItems.ItemsSource = _kp.Keys;
                _paymentButtons.Clear();
                _paymentButtons.AddRange(new[] { PayCashBtn, PayCreditBtn, PayDebitBtn, PayWireBtn, PayDollarsBtn });
                SetPayment("cash");
                RefreshKeypadDisplay();
            }
            catch { }
        }
        private void PayButton_Click(object sender, RoutedEventArgs e) { SetPayment(((Button)sender).Tag?.ToString() ?? "cash"); }
        private void SetPayment(string methodId)
        {
            _selectedPaymentId = methodId;
            foreach (var b in _paymentButtons)
            {
                var isSel = string.Equals(b.Tag?.ToString(), methodId, StringComparison.OrdinalIgnoreCase);
                b.Style = (Style)FindResource(isSel ? "PrimaryRaised" : "PrimaryOutlined");
            }
            try
            {
                RefPanel.Visibility =
                    (string.Equals(methodId, "credit", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(methodId, "debit", StringComparison.OrdinalIgnoreCase))
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }
        private void RefreshKeypadDisplay() { try { KeypadDisplay.Text = KeypadText; } catch { } }
        private void KeypadClear_Click(object sender, RoutedEventArgs e) { _keypadBuffer = ""; RefreshKeypadDisplay(); }
        private void KeypadCommit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (decimal.TryParse(KeypadDisplay.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var importe) && importe > 0)
                    AddPayment(_selectedPaymentId, importe);
                _keypadBuffer = "";
                RefreshKeypadDisplay();
            }
            catch { }
        }
        private void Key_Click(object sender, RoutedEventArgs e)
        {
            var key = ((Button)sender).Content?.ToString() ?? "";
            switch (key)
            {
                case "←": if (_keypadBuffer.Length > 0) _keypadBuffer = _keypadBuffer[..^1]; break;
                case ".": if (!_keypadBuffer.Contains('.')) _keypadBuffer = (_keypadBuffer.Length == 0 ? "0" : _keypadBuffer) + "."; break;
                default: _keypadBuffer += key; break;
            }
            RefreshKeypadDisplay();
        }
        private void Denom_Click(object sender, RoutedEventArgs e)
        {
            var tag = ((Button)sender).Tag?.ToString();
            if (decimal.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out var add))
            {
                decimal.TryParse(KeypadDisplay.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var current);
                _keypadBuffer = (current + add).ToString("0.##", CultureInfo.InvariantCulture);
                RefreshKeypadDisplay();
            }
        }
        private string PaymentName(string id) => id switch
        {
            "cash" => T("Pay.Cash"),
            "credit" => T("Pay.Credit"),
            "debit" => T("Pay.Debit"),
            "wire" => T("Pay.Wire"),
            "usd" => T("Pay.USD"),
            _ => id
        };
        // === Handlers que faltaban del XAML ===
        private void RegisterDriver_Click(object sender, RoutedEventArgs e)
        {
            try { RootDialog.IsOpen = true; } catch { }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Acción rápida ejecutada.", "TruckScale POS",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RegisterCancel_Click(object sender, RoutedEventArgs e)
        {
            try { RootDialog.IsOpen = false; } catch { }
        }

        private void RegisterSave_Click(object sender, RoutedEventArgs e)
        {
            try { RootDialog.IsOpen = false; } catch { }
            MessageBox.Show("Información guardada (demo).", "TruckScale POS",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddPayment(string methodId, decimal monto)
        {
            if (monto <= 0) return;
            _pagos.Add(new PaymentRow { Metodo = PaymentName(methodId), Monto = monto });
            RefreshSummary();
        }
        private decimal _ventaTotal = 0m, _descuento = 0m, _impuestos = 0m, _comisiones = 0m;
        private void InitSummaryUi() { try { PagosList.ItemsSource = _pagos; RefreshSummary(); } catch { } }
        private void RefreshSummary()
        {
            try
            {
                TotalVentaBigText.Text = _ventaTotal.ToString("C", _moneyCulture);
                var recibido = 0m; foreach (var p in _pagos) recibido += p.Monto;
                var totalCalculado = _ventaTotal - _descuento + _impuestos + _comisiones;
                var balance = totalCalculado - recibido;
                PagoRecibidoText.Text = recibido.ToString("N2", _moneyCulture);
                BalanceText.Text = balance.ToString("N2", _moneyCulture);
                var rojo = (Brush)(TryFindResource("MaterialDesignValidationErrorBrush") ?? Brushes.IndianRed);
                var ok = (Brush)(TryFindResource("PrimaryHueMidBrush") ?? TryFindResource("PrimaryBrush") ?? Brushes.SeaGreen);
                BalanceText.Foreground = balance > 0 ? rojo : ok;
                if (PagosEmpty != null) PagosEmpty.Visibility = _pagos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        // ===== DB init en Window_Loaded =====
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var dbCfg = new DatabaseConfig();
            var factory = new MySqlConnectionFactory(dbCfg);
            _logger = new WeightLogger(factory);
            try { using var _ = await factory.CreateOpenConnectionAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo abrir MySQL:\n{ex.Message}", "TruckScale POS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            InitSummaryUi();
            try { RecientesList.ItemsSource = _recentSessions; } catch { }
        }

        // ===== SERIAL: lee C:\TruckScale\serial.txt y abre puerto =====
        private void StartSerialFromConfig()
        {
            try
            {
                var path = @"C:\TruckScale\serial.txt";
                if (File.Exists(path))
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (kv.Length != 2) continue;
                        var k = kv[0].ToLowerInvariant();
                        var v = kv[1];

                        if (k is "port" or "fixedport" or "com") _portName = v;
                        else if (k == "baud" && int.TryParse(v, out var b)) _baud = b;
                        else if (k == "minvalidweightlb" &&
                                 double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var mv))
                            _minValidWeightLb = mv;
                    }
                }
            }
            catch { /* usa defaults */ }

            OpenSerial(_portName, _baud);
        }

        private void OpenSerial(string portName, int baud)
        {
            try
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnPortData;
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                    _port = null;
                }

                _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    NewLine = "\r",          // CR 
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true,
                    ReceivedBytesThreshold = 1,
                };

                _port.DataReceived += OnPortData;
                _port.Open();

                Dispatcher.BeginInvoke(() =>
                {
                    if (ScaleStateText != null)
                        ScaleStateText.Text = _port!.IsOpen ? "Scale: Connected" : "Scale: Disconnected";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SERIAL] {ex.Message}");
                Dispatcher.BeginInvoke(() =>
                {
                    if (ScaleStateText != null) ScaleStateText.Text = "Scale: Disconnected";
                });
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnPortData;
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                }
            }
            catch { }
            base.OnClosed(e);
        }

        private void OnPortData(object? sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                while (_port != null && _port.BytesToRead > 0)
                {
                    var line = _port.ReadLine();          // usa NewLine = "\r"
                    if (!string.IsNullOrWhiteSpace(line))
                        ProcessLine(line);
                }
            }
            catch (TimeoutException) { }
            catch (Exception ex) { Debug.WriteLine($"[SERIAL] Read error: {ex.Message}"); }
        }


        // ===== Lógica EXACTA del .txt =====
        private void ProcessLine(string line)
        {
            // 1) normaliza espacios como en tu prueba
            var compact = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", "");

            var m = rx.Match(compact);
            if (!m.Success)
            {
                // si quieres log local:
                Debug.WriteLine($"No coincide: {line}");
                return;
            }

            int ch = int.Parse(m.Groups["ch"].Value);
            double w = double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
            string tail = m.Groups["tail"].Value.ToUpperInvariant(); // GG o GR

            _last[ch] = w;
            _lastTail[ch] = tail;

            // Live (si quieres actualizar el display con el último canal)
            _ = Dispatcher.BeginInvoke(new Action(() => UpdateWeightText(w)));

            // 2) Sólo cuando TOTAL (ch=3) viene estable (GG)
            if (ch == 3 && tail == "GG")
            {
                double e0 = _last[0], e1 = _last[1], e2 = _last[2], tot = _last[3];
                double suma = e0 + e1 + e2;

                // reset visual si TOTAL==0
                if (tot == 0)
                {
                    _ultimoTotalGuardado = 0;
                    _sessionActive = false; _sessionTotalLb = 0; _axleCount = 0;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _currentAxles.Clear();
                        UpdateWeightText(0);
                    }));
                    return;
                }

                bool sumaCuadra = Math.Abs(suma - tot) <= MAX_ERROR;
                bool cambioOk = Math.Abs(tot - _ultimoTotalGuardado) >= MIN_CAMBIO;

                if (sumaCuadra && cambioOk)
                {
                    _ultimoTotalGuardado = tot;
                    AcceptSnapshot(e0, e1, e2, tot, line);   // <— guardamos y pintamos
                }
                else
                {
                    Debug.WriteLine($"Descartado: sum={suma:0} vs total={tot:0}  Δ={Math.Abs(suma - tot):0} lb");
                }
            }
        }


        /// <summary>
        /// Aquí se llama a los 2 SP:
        ///   1) sp_scale_session_insert  -> crea cabecera (una por camión) y regresa session_id/uuid10
        ///   2) sp_scale_axle_insert     -> tres veces (una por eje) usando ese session_id/uuid10
        /// </summary>
        private async void AcceptSnapshot(double e0, double e1, double e2, double total, string rawLine)
        {
            // 1) Congela label + lista de ejes (UI)
            _sessionActive = true; _sessionTotalLb = total; _axleCount = 3;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _currentAxles.Clear();
                _currentAxles.Add($"Eje 1: {e0:N0} lb");
                _currentAxles.Add($"Eje 2: {e1:N0} lb");
                _currentAxles.Add($"Eje 3: {e2:N0} lb");
                _currentAxles.Add($"TOTAL: {total:N0} lb");
                UpdateWeightText(0);
            }));

            // Recientes (opcional)
            string axles = $"{e0:N0} | {e1:N0} | {e2:N0}";
            string line = $"Axles: [{axles}] → Gross {total:N0} lb";
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _recentSessions.Insert(0, line);
                if (_recentSessions.Count > 8) _recentSessions.RemoveAt(_recentSessions.Count - 1);
            }));

            // 2) Persistencia: EXACTAMENTE 1 encabezado + 3 detalles
            try
            {
                if (_logger != null)
                {
                    // Encabezado: sp_scale_session_insert (regresa sessionId y leemos uuid10)
                    var (sessionId, uuid10) =
                        await _logger.InsertSessionAsync((decimal)Math.Round(total, 3)).ConfigureAwait(false);

                    // Detalles: sp_scale_axle_insert por cada eje (pasamos id y uuid10)
                    await _logger.InsertAxleAsync(
                        axleIndex: 1,
                        weightLb: (decimal)Math.Round(e0, 3),
                        rawLine: rawLine,
                        sessionId: sessionId,
                        sessionUuid10: uuid10
                    ).ConfigureAwait(false);

                    await _logger.InsertAxleAsync(
                        axleIndex: 2,
                        weightLb: (decimal)Math.Round(e1, 3),
                        rawLine: rawLine,
                        sessionId: sessionId,
                        sessionUuid10: uuid10
                    ).ConfigureAwait(false);

                    await _logger.InsertAxleAsync(
                        axleIndex: 3,
                        weightLb: (decimal)Math.Round(e2, 3),
                        rawLine: rawLine,
                        sessionId: sessionId,
                        sessionUuid10: uuid10
                    ).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DB] Insert session/axles failed: {ex}");
                if (!_dbErrorShown)
                {
                    _dbErrorShown = true;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                        MessageBox.Show($"Error guardando en MySQL:\n{ex.Message}",
                            "TruckScale POS", MessageBoxButton.OK, MessageBoxImage.Error)));
                }
            }
        }


    }
}
