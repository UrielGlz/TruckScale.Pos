using MaterialDesignThemes.Wpf;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TruckScale.Pos.Data;
using TruckScale.Pos.Domain;
using TruckScale.Pos.Config;
namespace TruckScale.Pos
{
    // ===== Teclado / pagos (UI) =====
    public class KeypadConfig
    {
        public int Columns { get; set; } = 3;
        public string[] Keys { get; set; } = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "00", "←" };
        public decimal[] Denominations { get; set; } = new decimal[] { 1, 5, 10, 20, 50, 100, 200, 500 };
    }

    public enum UnitSystem { Metric, Imperial }

    public sealed class PaymentMethod : INotifyPropertyChanged
    {
        public int Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsCash { get; init; }
        public bool AllowReference { get; init; }
        public bool IsActive { get; init; }

        // Para la UI:
        public PackIconKind IconKind { get; init; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public class PaymentEntry
    {
        public string Metodo { get; set; } = "";   // Texto para UI
        public string Code { get; set; } = "";     // code real (cash, credit, etc.)
        public string? Ref { get; set; }           // referencia opcional
        public decimal Monto { get; set; }
    }

    public partial class MainWindow : Window
    {
        // ===== Tema =====
        private readonly PaletteHelper _palette = new();
        private bool _dark = false;

        // ===== Cultura / unidades =====
        private readonly CultureInfo _moneyCulture = new("en-US");
        private UnitSystem _units = UnitSystem.Imperial;

        // ===== UI: keypad / pagos =====
        private KeypadConfig _kp = new();
        private string _keypadBuffer = "";
        public string KeypadText => string.IsNullOrEmpty(_keypadBuffer) ? "0" : _keypadBuffer;
        private string _selectedPaymentId = "cash";

        public ObservableCollection<PaymentMethod> PaymentMethods { get; } = new();

        // ===== DB / logger =====
        private WeightLogger? _logger;

        // ===== Estado de sesión (peso / ejes) =====
        private int _axleCount = 0;
        private double _sessionTotalLb = 0;
        private bool _sessionActive = false;
        private readonly ObservableCollection<string> _recentSessions = new();
        private readonly ObservableCollection<string> _currentAxles = new();

        // === Serial al estilo "ScaleTesting" ===
        private SerialPort? _port;

        // Últimos valores por canal (0,1,2 = ejes; 3 = total)
        readonly Dictionary<int, double> _last = new() { { 0, 0 }, { 1, 0 }, { 2, 0 }, { 3, 0 } };
        readonly Dictionary<int, string> _lastTail = new() { { 0, "" }, { 1, "" }, { 2, "" }, { 3, "" } };

        // Para activar los botones de servicio
        private bool _driverLinked = false;

        // ==== Estado WAIT/OK y snapshot de peso estable ====
        private bool _canAccept = false;          // true cuando hay snapshot estable listo para guardar
        private double _snapAx1, _snapAx2, _snapAx3, _snapTotal;
        private string _snapRaw = "";
        private DateTime _snapUtc;

        // Referencia al botón WAIT/OK (se toma del sender la primera vez que se hace click)
        private Button? _waitOkButton;

        public MainWindow()
        {
            InitializeComponent();

            ApplyTheme();
            ApplyBrand();
            ApplyUi();

            LoadKeypadConfig();
            BuildKeypadUI();

            // Antes se usaba el botón Start para conectar.
            // Ahora la conexión se hace automáticamente al cargar la ventana (TryAutoConnectAtBoot).
            SetUiReady(false, "Connecting…");
        }

        //******************** Pudin *******************************//
        /* Todo el código de báscula lo puse entre estos comentarios */
        /* ANDRES *****************************************/

        private void StartReader(string portName)
        {
            try
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnDataReceived;
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                    _port = null;
                }

                _port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    NewLine = "\r",      // CR (igual que en el stream real)
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true,
                    ReceivedBytesThreshold = 1
                };

                _port.DataReceived += OnDataReceived;
                _port.Open();

                if (_port.IsOpen)
                {
                    _isSimulated = false;
                    SetUiReady(true, "Waiting for truck…");
                    lblEstado.Content = $"Scale connected on {_port.PortName} (9600 8N1)";
                    try { ScaleStateText.Text = "Scale: Connected"; } catch { }
                }
                else
                {
                    SetUiReady(false, "Error");
                    lblEstado.Content = "No se pudo abrir el puerto.";
                }
            }
            catch (UnauthorizedAccessException)
            {
                lblEstado.Content = "El puerto está en uso por otra aplicación.";
                throw;
            }
            catch (Exception ex)
            {
                lblEstado.Content = ex.ToString();
                throw;
            }
        }

        /// <summary>
        /// Conexión automática al arrancar (real → si falla, simulado).
        /// </summary>
        private void TryAutoConnectAtBoot()
        {
            try
            {
                AppendLog("[Boot] Trying to open scale on COM2…");
                StartReader("COM2"); // si falla, lanza excepción y caemos al catch
            }
            catch (Exception ex)
            {
                AppendLog($"[Boot] Failed to open serial port: {ex.Message}. Switching to simulated mode.");
                StartSimulatedReader();
            }
        }

        // ===== Captura de crudo a TXT (no usar en producción) =====
        private StreamWriter _rxCapture;
        private readonly object _capLock = new();
        private readonly Stopwatch _capSw = new();

        public void StartCapture(string folder = null)
        {
            // no usar en producción
            folder ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
            Directory.CreateDirectory(folder);

            var file = Path.Combine(folder, $"capture_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");

            _rxCapture = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            _capSw.Restart();
            _rxCapture.WriteLine("# capture-start-utc=" + DateTime.UtcNow.ToString("o"));
            _rxCapture.WriteLine("# format: <elapsed_ms>|<line-as-received>");
        }

        public void StopCapture()
        {
            lock (_capLock)
            {
                _rxCapture?.WriteLine("# capture-end-utc=" + DateTime.UtcNow.ToString("o"));
                _rxCapture?.Dispose();
                _rxCapture = null;
            }
        }

        private static readonly object _rawLock = new object();
        private static readonly string _dir = @"C:\TruckScale\Test3.3";

        public static void CaptureRaw1(string line)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string filePath = Path.Combine(_dir, $"raw_{DateTime.Now:yyyyMMdd}.log");
                string toWrite = $"{DateTime.Now:HH:mm:ss.fff}\t{line}{Environment.NewLine}";

                lock (_rawLock)
                {
                    using (var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                    {
                        sw.Write(toWrite);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CaptureRaw error: {ex}");
            }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                while (_port.BytesToRead > 0)
                {
                    var line = _port.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        //CaptureRaw1(line);
                        ProcessLine2(line);
                    }
                }
            }
            catch (TimeoutException) { }
            catch (Exception)
            {
                // log si quieres
            }
        }

        // ===== Campos / estado de estabilidad =====
        private readonly double[] _last1 = new double[4];     // 0,1,2 = ejes; 3 = total (legacy, casi no usado)
        private readonly string[] _lastTail1 = new string[4];
        private double _ultimoTotalGuardado1 = 0;

        // Variables globales para BD (solo cuando estabiliza)
        private double G_Eje1, G_Eje2, G_Eje3, G_Total;
        private DateTime G_TimestampUtc;
        private bool G_Estable;

        double lbs_temp = 0;
        bool isStable = false;

        private static readonly Regex rx = new(@"^%(?<ch>\d)(?<w>\d+(?:\.\d+)?)lb(?<tail>[A-Za-z]{2})$",
                                               RegexOptions.Compiled);

        // ----- Config de estabilidad -----
        const double MIN_TOTAL_LB = 100;           // mínimo para considerar lectura válida
        const double EPSILON_LB = 20;             // variación permitida en la ventana (lb)
        const int WINDOW_MS = 1200;               // ancho de ventana
        const int MIN_SAMPLES = 5;                // mínimo de muestras en la ventana
        const int HOLD_MS = 600;                  // tiempo estable continuo antes de disparar
        const double SUM_TOL_LB = 100;            // |(e0+e1+e2) - total| tolerado
        const double SNAP_DELTA_LB = 200;         // diferencia mínima contra último total guardado

        // ----- Estado de estabilidad -----
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastUnstableMs = 0;
        private bool _autoStable = false;
        private double _lastPersistedTotal = double.NaN;

        private static double Get(IDictionary<int, double> map, int key)
        {
            double v;
            return (map != null && map.TryGetValue(key, out v)) ? v : 0d;
        }

        private readonly Queue<(long ts, double total)> _winTotals = new();

        private void ProcessLine2(string line)
        {
            // 1) Normaliza y parsea
            string compact = Regex.Replace(line ?? "", @"\s+", "");
            AppendLog($"LINE: {compact}");

            var m = rx.Match(compact);
            if (!m.Success)
            {
                AppendLog("… no coincide con %<canal><peso>lbXX");
                return;
            }

            int ch = int.Parse(m.Groups["ch"].Value, CultureInfo.InvariantCulture);
            double w = double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
            string tail = m.Groups["tail"].Value.ToUpperInvariant(); // GG/GR/etc

            _last[ch] = w;
            _lastTail[ch] = tail;

            // 2) UI en “tiempo real”
            Dispatcher.InvokeAsync(() =>
            {
                if (ch == 0) lblEje1.Content = $"{_last[0]:0} lb";
                if (ch == 1) lblEje2.Content = $"{_last[1]:0} lb";
                if (ch == 2) lblEje3.Content = $"{_last[2]:0} lb";
                if (ch == 3) WeightText.Text = $"{_last[3]:0}";
            }); 

            string note = $"ch3={_last[3]} ,ch2={_last[2]} ,ch1={_last[1]} ,ch0={_last[0]}";

            // Ahora solo evaluamos estabilidad + UI; el guardado ocurre al presionar OK.
            EvaluateStabilityAndUpdateUi(_last, note);
        }

        /// <summary>
        /// Evalúa estabilidad en ventana, actualiza UI y genera un snapshot
        /// listo para guardar cuando el operador presione OK.
        /// NO guarda en BD aquí.
        /// </summary>
        private void EvaluateStabilityAndUpdateUi(IDictionary<int, double> axles, string note)
        {
            double e0 = Get(axles, 0);
            double e1 = Get(axles, 1);
            double e2 = Get(axles, 2);
            double total = Get(axles, 3);

            long now = _sw.ElapsedMilliseconds;

            _winTotals.Enqueue((now, total));
            while (_winTotals.Count > 0 && (now - _winTotals.Peek().ts) > WINDOW_MS)
                _winTotals.Dequeue();

            if (_winTotals.Count < MIN_SAMPLES || total < MIN_TOTAL_LB)
            {
                _autoStable = false;
                _canAccept = false;
                _lastUnstableMs = now;

                Dispatcher.InvokeAsync(() =>
                {
                    lblTemp.Content = total < MIN_TOTAL_LB ? "Waiting for truck…" : "Weight in progress";
                    SetOkButtonWaitState();
                });

                return;
            }

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (var s in _winTotals)
            {
                if (s.total < min) min = s.total;
                if (s.total > max) max = s.total;
            }
            double span = max - min;

            if (span > EPSILON_LB)
            {
                if (_autoStable) AppendLog($"→ pierde estabilidad (Δ={span:0.0} lb)");
                _autoStable = false;
                _canAccept = false;
                _lastUnstableMs = now;

                Dispatcher.InvokeAsync(() =>
                {
                    lblTemp.Content = "Weight in progress";
                    SetOkButtonWaitState();
                });

                return;
            }

            // Aún no se cumple el HOLD (tiempo mínimo estable)
            if (!_autoStable && (now - _lastUnstableMs) < HOLD_MS)
                return;

            if (!_autoStable)
            {
                _autoStable = true;
                AppendLog($"→ entra estable (Δ={span:0.0} lb en {WINDOW_MS} ms)");
            }

            // Reglas de suma de ejes vs total
            double suma = e0 + e1 + e2;
            if (Math.Abs(suma - total) > SUM_TOL_LB)
            {
                AppendLog($"(Descartado) ejes {suma:0} vs total {total:0} (Δ={Math.Abs(suma - total):0} lb)");
                _canAccept = false;

                Dispatcher.InvokeAsync(() =>
                {
                    lblTemp.Content = "Check axles / total";
                    SetOkButtonWaitState();
                });

                return;
            }

            // Evitar aceptar dos veces casi el mismo peso
            if (!double.IsNaN(_lastPersistedTotal) &&
                Math.Abs(total - _lastPersistedTotal) < SNAP_DELTA_LB)
            {
                AppendLog($"(Descartado) total {total:0} lb demasiado cerca del último aceptado {_lastPersistedTotal:0} lb.");
                _canAccept = false;

                Dispatcher.InvokeAsync(() =>
                {
                    lblTemp.Content = "Waiting for next truck…";
                    SetOkButtonWaitState();
                });

                return;
            }

            // Si llegamos aquí, hay snapshot estable listo para OK
            _snapAx1 = e0;
            _snapAx2 = e1;
            _snapAx3 = e2;
            _snapTotal = total;
            _snapUtc = DateTime.UtcNow;
            _snapRaw = note ?? $"e0={e0:0} e1={e1:0} e2={e2:0} tot={total:0}";

            _canAccept = true;

            Dispatcher.InvokeAsync(() =>
            {
                lblTemp.Content = "STABLE — press OK";
                SetOkButtonReadyState();
            });
        }


        /// <summary>
        /// Cambia visualmente el botón WAIT/OK según si se puede aceptar o no.
        /// Nota: el botón real se captura la primera vez que el usuario hace click.
        /// </summary>
        private void SetWaitOkVisual(bool canAccept)
        {
            try
            {
                if (_waitOkButton == null) return;

                _waitOkButton.Content = canAccept ? "OK" : "WAIT";

                var okBrush = TryFindResource("PrimaryHueMidBrush") as Brush
                              ?? TryFindResource("PrimaryBrush") as Brush
                              ?? Brushes.SeaGreen;

                var waitBrush = TryFindResource("MaterialDesignValidationErrorBrush") as Brush
                                ?? Brushes.IndianRed;

                _waitOkButton.Background = canAccept ? okBrush : waitBrush;
            }
            catch
            {
                // No romper el flujo si falla algo de UI
            }
        }

        // ===== Conexión a BD para peso =====
        public static string GetConnectionString()
        {
            // 1) Intentar leer desde config.json
            var cfg = PosConfigService.Load();
            if (!string.IsNullOrWhiteSpace(cfg.MainDbStrCon))
                return cfg.MainDbStrCon;

            // 2) Fallback legacy (lo que ya tenías)
            var builder = new MySqlConnectionStringBuilder
            {
                Server = Environment.GetEnvironmentVariable("BAKEOPS_DB_HOST") ?? "lunisapp.com",
                Database = Environment.GetEnvironmentVariable("BAKEOPS_DB_NAME") ?? "lunisapppudin_demo_scale",
                UserID = Environment.GetEnvironmentVariable("BAKEOPS_DB_USER") ?? "lunisapppudin_admin",
                Password = Environment.GetEnvironmentVariable("BAKEOPS_DB_PASS") ?? "andy012809$50$565etd69873",
                SslMode = MySqlSslMode.Preferred,
                ConnectionTimeout = 8,
                DefaultCommandTimeout = 30,
            };
            return builder.ConnectionString;
        }

        private string? _currentWeightUuid;

        // Guarda snapshot de peso en scale_session_axles
        public async Task<long> saveScaleData(double eje1, double eje2, double eje3, double total, string rawLine, string uuid_weight)
        {
            string connStr = GetConnectionString();

            _currentWeightUuid = uuid_weight; // se usa para vincular con sale_driver_info

            const string SQL = @"INSERT INTO scale_session_axles (uuid_weight, axle_index, weight_lb,
                                 captured_utc, captured_local, captured_local_time, raw_line, status_id, eje1, eje2, eje3, peso_total)
                                VALUES 
                                (@uuid_weight, 3, @weight_lb,@utc, @local, @local_time,@raw, 1, @e1, @e2, @e3, @total);";

            var nowLocal = DateTime.Now;

            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@uuid_weight", uuid_weight);
            cmd.Parameters.AddWithValue("@weight_lb", Math.Round(total, 3));
            cmd.Parameters.AddWithValue("@utc", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@local", nowLocal);
            cmd.Parameters.AddWithValue("@local_time", nowLocal.TimeOfDay);
            cmd.Parameters.AddWithValue("@raw", string.IsNullOrEmpty(rawLine) ? (object)DBNull.Value :
                (rawLine.Length > 128 ? rawLine[..128] : rawLine));
            cmd.Parameters.AddWithValue("@e1", (int)Math.Round(eje1));
            cmd.Parameters.AddWithValue("@e2", (int)Math.Round(eje2));
            cmd.Parameters.AddWithValue("@e3", (int)Math.Round(eje3));
            cmd.Parameters.AddWithValue("@total", (int)Math.Round(total));

            await cmd.ExecuteNonQueryAsync();

            return (long)cmd.LastInsertedId;
        }

        private void AppendLog(string text)
        {
            void append()
            {
                txtLogTemp.AppendText(text + Environment.NewLine);
                txtLogTemp.ScrollToEnd();
            }

            if (txtLogTemp.Dispatcher.CheckAccess())
                append();
            else
                txtLogTemp.Dispatcher.BeginInvoke((Action)append);
        }

        private static readonly char[] UID_CHARS =
            "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray(); // 24 letras + 8 dígitos = 32 símbolos

        private static readonly HashSet<string> _seenIds = new();
        private static readonly object _seenLock = new();

        private static string GenerateUid10()
        {
            const int LEN = 18;
            while (true)
            {
                Span<byte> buf = stackalloc byte[LEN];
                RandomNumberGenerator.Fill(buf);

                var chars = new char[LEN];
                for (int i = 0; i < LEN; i++)
                    chars[i] = UID_CHARS[buf[i] & 31];

                var id = new string(chars);
                lock (_seenLock)
                    if (_seenIds.Add(id)) return id;
            }
        }

        //***************************************************************//

        // ===== Localización / UI básica =====
        private string T(string key) => (TryFindResource(key) as string) ?? key;

        private void ApplyUi()
        {
            UpdateWeightText(0); // arranca en 0 lb
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

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            _dark = !_dark;
            ApplyTheme();
        }

        // ===== Monitor de peso (label grande) =====
        private void UpdateWeightText(double lb)
        {
            double valToShowLb = (_sessionActive || _axleCount > 0) ? _sessionTotalLb : lb;

            string suffix = (_units == UnitSystem.Imperial) ? "lb" : "kg";
            double value = (_units == UnitSystem.Imperial) ? valToShowLb : (valToShowLb / 2.20462262185);

            if (WeightText != null) WeightText.Text = $"{value:0,0.0} {suffix}";
        }

        private void Zero_Click(object sender, RoutedEventArgs e)
        {
            _sessionActive = false;
            _axleCount = 0;
            _sessionTotalLb = 0;
            _currentAxles.Clear();
            UpdateWeightText(0);
            lblTemp.Content = "Waiting";
            lblEstado.Content = "Scale ready.";
            ResetDriverContext();
        }

        private void ResetDriverContext()
        {
            // 1) Estado de chofer y toggles
            _driverLinked = false;

            _simSavedOnce = false;
            _autoStable = false;
            _winTotals.Clear();
            _canAccept = false;

            HideDriverCard();
            UpdateProductButtonsEnabled();
            try { WeighToggle.IsChecked = false; ReweighToggle.IsChecked = false; } catch { }

            // 2) Limpia modal
            try { ChoferNombreText.Text = ""; } catch { }
            try { ChoferApellidosText.Text = ""; } catch { }
            try { ChoferTelefonoText.Text = ""; } catch { }
            try { LicenciaNumeroText.Text = ""; } catch { }
            try { LicenciaVigenciaPicker.SelectedDate = null; } catch { }
            try { PlacasRegText.Text = ""; } catch { }
            try { TipoUnidadCombo.SelectedIndex = -1; } catch { }
            try { MarcaText.Text = ""; } catch { }
            try { ModeloText.Text = ""; } catch { }
            try { ObsText.Text = ""; } catch { }
            try { ClienteRegCombo.SelectedIndex = -1; } catch { }
            try { ProductoRegText.Text = ""; } catch { }
            try { RootDialog.IsOpen = false; } catch { }

            // 3) Totales y producto seleccionado
            _ventaTotal = 0m;
            _descuento = 0m;
            _impuestos = 0m;
            _comisiones = 0m;
            _selectedProductId = 0;
            _selectedProductCode = "";
            _selectedProductName = "";
            _selectedProductPrice = 0m;
            _selectedCurrency = "USD";
            try { TotalVentaBigText.Text = _ventaTotal.ToString("C", _moneyCulture); } catch { }

            // 4) Pagos / referencia / teclado
            try { RefText.Text = ""; } catch { }
            try { RefPanel.Visibility = Visibility.Collapsed; } catch { }
            _pagos.Clear();
            _selectedPaymentId = "cash";
            SelectPaymentByCode("cash");
            _keypadBuffer = "";
            RefreshKeypadDisplay();
            RefreshSummary();

            // 5) Báscula / UUID
            _currentAxles.Clear();
            _ax1 = _ax2 = _ax3 = _total = 0;
            _tAx1 = _tAx2 = _tAx3 = _tTotal = DateTime.MinValue;
            _lastPersistedTotal = double.NaN;
            _currentWeightUuid = null;
            _canAccept = false;
            _snapRaw = "";
            try { lblEje1.Content = ""; lblEje2.Content = ""; lblEje3.Content = ""; lblUUID.Content = ""; } catch { }
            UpdateWeightText(0);

            // 6) UI “Waiting”
            try
            {
                if (_isConnected)
                {
                    lblTemp.Content = "Waiting for truck…";
                    lblEstado.Content = "Scale ready.";
                }
                else
                {
                    lblTemp.Content = "Scale offline";
                    lblEstado.Content = "No scale connection.";
                }
                SetWaitOkVisual(false);
            }
            catch { }
        }

        // ===== Keypad / pagos =====
        private void ToggleDrawer_Click(object sender, RoutedEventArgs e)
            => RootDrawerHost.IsLeftDrawerOpen = !RootDrawerHost.IsLeftDrawerOpen;

        private void LoadKeypadConfig()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "keypad.json");
                _kp = File.Exists(path)
                    ? (System.Text.Json.JsonSerializer.Deserialize<KeypadConfig>(File.ReadAllText(path)) ?? new())
                    : new();
            }
            catch { _kp = new(); }
        }

        private void BuildKeypadUI()
        {
            try
            {
                DenomsHost.ItemsSource = _kp.Denominations;
                KeysItems.ItemsSource = _kp.Keys;

                SelectPaymentByCode("cash");
                RefreshKeypadDisplay();
            }
            catch { }
        }

        private void PayButton_Click(object sender, RoutedEventArgs e)
        {
            SetPayment(((Button)sender).Tag?.ToString() ?? "cash");
        }

        private void SetPayment(string methodId)
        {
            SelectPaymentByCode(methodId);
        }

        private void RefreshKeypadDisplay()
        {
            try { KeypadDisplay.Text = KeypadText; } catch { }
        }

        private void KeypadClear_Click(object sender, RoutedEventArgs e)
        {
            _keypadBuffer = "";
            RefreshKeypadDisplay();
        }

        private bool _isCompletingSale = false; // evita doble clic

        private async void KeypadCommit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (decimal.TryParse(KeypadDisplay.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var importe)
                    && importe > 0)
                {
                    AddPayment(_selectedPaymentId, importe);
                }
                _keypadBuffer = "";
                RefreshKeypadDisplay();
                RefreshSummary();

                await TryAutoCompleteSaleAsync();
            }
            catch { }
        }

        private async Task TryAutoCompleteSaleAsync()
        {
            if (_isCompletingSale) return;
            if (!_driverLinked || _selectedProductId <= 0) return;

            var (subtotal, tax, total) = ComputeTotals();
            var recibido = SumReceived();
            if (recibido < total) return;

            _isCompletingSale = true;
            try
            {
                var saleUid = await SaveSaleAsync();
                var change = recibido - total;

                AppendLog($"[Sale] Completed sale_uid={saleUid} Total={total:0.00} Received={recibido:0.00} Change={change:0.00}");

                await ShowSaleCompletedDialogAsync(total, recibido, change, saleUid);

                ResetDriverContext();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error completing sale: " + ex.Message, "TruckScale POS",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isCompletingSale = false;
            }
        }

        private void Key_Click(object sender, RoutedEventArgs e)
        {
            var key = ((Button)sender).Content?.ToString() ?? "";
            switch (key)
            {
                case "←":
                    if (_keypadBuffer.Length > 0) _keypadBuffer = _keypadBuffer[..^1];
                    break;
                case ".":
                    if (!_keypadBuffer.Contains('.'))
                        _keypadBuffer = (_keypadBuffer.Length == 0 ? "0" : _keypadBuffer) + ".";
                    break;
                default:
                    _keypadBuffer += key;
                    break;
            }
            RefreshKeypadDisplay();
        }

        async private void Denom_Click(object sender, RoutedEventArgs e)
        {
            var tag = ((Button)sender).Tag?.ToString();
            if (decimal.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out var add))
            {
                decimal.TryParse(KeypadDisplay.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var current);
                _keypadBuffer = (current + add).ToString("0.##", CultureInfo.InvariantCulture);
                RefreshKeypadDisplay();
                await TryAutoCompleteSaleAsync();
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

        // === Handlers de UI faltantes ===
        private void RegisterDriver_Click(object sender, RoutedEventArgs e)
        {
            try { RootDialog.IsOpen = true; } catch { }
        }

        async private void Button_Click(object sender, RoutedEventArgs e) // NO SE USA, solo pruebas
        {
            string uid = GenerateUid10();
            try
            {
                var id = await saveScaleData(1.1, 2.0, 3.0, 6.0, "example data", uid);
                MessageBox.Show($"Guardado OK (id={id})", "TruckScale POS",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al guardar: " + ex.Message, "TruckScale POS",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RegisterCancel_Click(object sender, RoutedEventArgs e)
        {
            try { RootDialog.IsOpen = false; } catch { }
        }

        // === Handler del botón WAIT/OK (antes Start) ===
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            // Capturamos el botón real la primera vez que se hace click
            if (_waitOkButton == null && sender is Button b)
                _waitOkButton = b;

            // Si el estado es WAIT (_canAccept=false), no hacemos nada
            if (!_canAccept)
            {
                AppendLog("[OK] Click ignored (no stable weight to accept).");
                return;
            }

            var uuid = GenerateUid10();
            _currentWeightUuid = uuid;

            var ax1 = _snapAx1;
            var ax2 = _snapAx2;
            var ax3 = _snapAx3;
            var total = _snapTotal;
            var raw = _snapRaw;

            try
            {
                AppendLog($"[OK] Saving snapshot uuid={uuid} total={total:0.0} lb");
                var id = await saveScaleData(ax1, ax2, ax3, total, raw, uuid);
                AppendLog($"[DB] Insertado id={id} (uuid={uuid})");

                _lastPersistedTotal = total;
                _canAccept = false;
                _autoStable = false;
                _winTotals.Clear();
                if (_isSimulated) _simSavedOnce = true;

                Dispatcher.Invoke(() =>
                {
                    lblUUID.Content = uuid;
                    lblTemp.Content = "Waiting for next truck…";
                    SetWaitOkVisual(false);
                });
            }
            catch (Exception ex)
            {
                AppendLog("[DB] ERROR al guardar desde OK: " + ex);
                Dispatcher.Invoke(() => lblTemp.Content = "DB ERROR");
                _ = _logger?.LogEventAsync("ACCEPT_EX", rawLine: raw, note: ex.ToString());
            }
        }

        //UG save driver *sale_driver_info*
        private async void RegisterSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var uuid = _currentWeightUuid;
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    MessageBox.Show("No weight captured yet. Press OK on a stable weight before saving driver.",
                                    "TruckScale POS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string first = ChoferNombreText?.Text?.Trim() ?? "";
                string last = ChoferApellidosText?.Text?.Trim() ?? "";
                string phone = ChoferTelefonoText?.Text?.Trim() ?? "";
                string licNo = LicenciaNumeroText?.Text?.Trim() ?? "";
                DateTime? exp = LicenciaVigenciaPicker?.SelectedDate;

                string plates = PlacasRegText?.Text?.Trim() ?? "";

                int? vtypeId = null;
                if (TipoUnidadCombo?.SelectedValue is int sv) vtypeId = sv;
                else if (TipoUnidadCombo?.SelectedItem is VehicleType vt) vtypeId = vt.Id;

                string brand = MarcaText?.Text?.Trim() ?? "";
                string model = ModeloText?.Text?.Trim() ?? "";
                string notes = ObsText?.Text?.Trim();

                string clientText = (ClienteRegCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "";
                string productText = ProductoRegText?.Text?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(plates) && string.IsNullOrWhiteSpace(licNo))
                {
                    MessageBox.Show("Enter at least Plates or License number.", "TruckScale POS",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                long driverId = await InsertDriverInfoWithFallbackAsync(
                    saleUid: null,
                    firstName: first,
                    lastName: last,
                    phone: phone,
                    licenseNo: licNo,
                    licenseExpiry: exp,
                    plates: plates,
                    vehicleTypeId: vtypeId,
                    brand: brand,
                    model: model,
                    identifyBy: "weight_uuid",
                    matchKey: uuid!,
                    notes: notes
                );

                AppendLog($"[Driver] Saved id={driverId} linked to weight_uuid={uuid}");

                var info = await GetDriverByWeightUuidAsync(uuid);
                if (info != null)
                {
                    ShowDriverCard(info);
                    lblEstado.Content = "Driver linked to current weight.";
                    _driverLinked = true;
                    UpdateProductButtonsEnabled();
                }
                else
                {
                    _driverLinked = false;
                    UpdateProductButtonsEnabled();
                    AppendLog("[Driver] Warning: driver not found right after insert.");
                }

                RootDialog.IsOpen = false;

                await ShowDriverSavedDialogAsync(
                    $"{first} {last}".Trim(),
                    string.IsNullOrWhiteSpace(plates) ? null : plates,
                    string.IsNullOrWhiteSpace(licNo) ? null : licNo
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving driver: " + ex.Message, "TruckScale POS",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<long> InsertDriverInfoWithFallbackAsync(
            string? saleUid,
            string firstName,
            string lastName,
            string phone,
            string licenseNo,
            DateTime? licenseExpiry,
            string plates,
            int? vehicleTypeId,
            string brand,
            string model,
            string identifyBy,
            string matchKey,
            string? notes)
        {
            try
            {
                return await InsertDriverInfoAsync(
                    saleUid, firstName, lastName, phone, licenseNo, licenseExpiry,
                    plates, vehicleTypeId, brand, model, identifyBy, matchKey, notes);
            }
            catch (Exception ex1)
            {
                AppendLog("[Driver] Primary DB failed, trying local… " + ex1.Message);
                var localCsb = new MySqlConnectionStringBuilder(GetLocalConn());
                await using var conn = new MySqlConnection(localCsb.ConnectionString);
                await conn.OpenAsync();
                // Aquí podrías duplicar el SQL/params o factorizarlo.
                throw;
            }
        }

        private readonly ObservableCollection<PaymentEntry> _pagos = new();

        private void AddPayment(string methodCode, decimal monto)
        {
            if (monto <= 0) return;
            var refTxt = "";
            try { refTxt = RefPanel.Visibility == Visibility.Visible ? (RefText?.Text ?? "") : ""; } catch { }

            _pagos.Add(new PaymentEntry
            {
                Metodo = PaymentName(methodCode),
                Code = methodCode,
                Ref = string.IsNullOrWhiteSpace(refTxt) ? null : refTxt,
                Monto = monto
            });
            RefreshSummary();
        }

        private (decimal subtotal, decimal tax, decimal total) ComputeTotals()
        {
            var subtotal = _selectedProductPrice;
            var tax = 0m;
            var total = subtotal + tax;
            return (subtotal, tax, total);
        }

        private decimal SumReceived()
        {
            decimal s = 0m;
            foreach (var p in _pagos) s += p.Monto;
            return s;
        }

        private int? FindPaymentMethodIdByCode(string code)
        {
            foreach (var m in PaymentMethods)
                if (string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase))
                    return m.Id;
            return null;
        }

        private decimal _ventaTotal = 0m, _descuento = 0m, _impuestos = 0m, _comisiones = 0m;

        // === Simulación de báscula ===
        private bool _isSimulated = false;
        private bool _simSavedOnce = false;   // ya guardamos una vez en esta sesión simulada

        private CancellationTokenSource _simCts;
        private readonly Random _rand = new Random();

        private void StartSimulatedReader()
        {
            _isSimulated = true;

            _simCts?.Cancel();
            _simCts = new CancellationTokenSource();
            var token = _simCts.Token;
            _simSavedOnce = false;
            _currentWeightUuid = null;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    ScaleStateText.Text = "Scale: Simulated";
                }
                catch { }

                lblEstado.Content = "Simulated mode (no device).";
                lblTemp.Content = "Waiting for truck…";
                SetUiReady(true, "Waiting");
                SetOkButtonWaitState();

            });

            _ = Task.Run(async () =>
            {
                double a1 = _rand.Next(6000, 12000);
                double a2 = _rand.Next(6000, 12000);
                double a3 = _rand.Next(4000, 10000);

                while (!token.IsCancellationRequested)
                {
                    var d1 = a1 + _rand.Next(-5, 6);
                    var d2 = a2 + _rand.Next(-5, 6);
                    var d3 = a3 + _rand.Next(-5, 6);
                    var tot = d1 + d2 + d3;

                    Dispatcher.Invoke(() => HandleCardinalRawGG($"%0 {d1:0}lb GG"));
                    await Task.Delay(250, token);
                    Dispatcher.Invoke(() => HandleCardinalRawGG($"%1 {d2:0}lb GG"));
                    await Task.Delay(250, token);
                    Dispatcher.Invoke(() => HandleCardinalRawGG($"%2 {d3:0}lb GG"));
                    await Task.Delay(250, token);
                    Dispatcher.Invoke(() => HandleCardinalRawGG($"%3 {tot:0}lb GG"));
                    await Task.Delay(500, token);
                }
            }, token);
        }

        // === Últimas lecturas (para validar TOTAL vs suma de ejes en simulador) ===
        private double _ax1, _ax2, _ax3, _total;
        private DateTime _tAx1, _tAx2, _tAx3, _tTotal;

        private const int SYNC_WINDOW_MS = 2000;  // ventana de 2 s

        private void HandleCardinalRawGG(string line)
        {
            // Ej: "%0 1234lb GG"
            var m = Regex.Match(line ?? "", @"^%(?<ch>[0-3])\s+(?<w>-?\d+(?:\.\d+)?)\s*lb\s+GG",
                                RegexOptions.IgnoreCase);
            if (!m.Success) return;

            int ch = int.Parse(m.Groups["ch"].Value, CultureInfo.InvariantCulture);
            double w = double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);

            switch (ch)
            {
                case 0:
                    _ax1 = w; _tAx1 = DateTime.UtcNow;
                    lblEje1.Content = $"Axle 1: {w:0} lb";
                    break;
                case 1:
                    _ax2 = w; _tAx2 = DateTime.UtcNow;
                    lblEje2.Content = $"Axle 2: {w:0} lb";
                    break;
                case 2:
                    _ax3 = w; _tAx3 = DateTime.UtcNow;
                    lblEje3.Content = $"Axle 3: {w:0} lb";
                    break;
                case 3:
                    _total = w; _tTotal = DateTime.UtcNow;
                    WeightText.Text = $"{w:0.0} lb";
                    break;
            }

            TryAcceptStableSet();
        }

        /// <summary>
        /// Evaluación de set estable en modo simulado.
        /// Ahora solo prepara snapshot y habilita OK, NO guarda.
        /// </summary>
        private void TryAcceptStableSet()
        {
            bool inWindow =
                (_tTotal - _tAx1).Duration().TotalMilliseconds <= SYNC_WINDOW_MS &&
                (_tTotal - _tAx2).Duration().TotalMilliseconds <= SYNC_WINDOW_MS &&
                (_tTotal - _tAx3).Duration().TotalMilliseconds <= SYNC_WINDOW_MS;

            if (!inWindow)
            {
                _canAccept = false;
                Dispatcher.Invoke(() =>
                {
                    lblTemp.Content = "Weight in progress";
                    SetOkButtonWaitState();
                });
                return;
            }

            double sum = _ax1 + _ax2 + _ax3;
            if (Math.Abs(sum - _total) > 100)
            {
                _canAccept = false;
                Dispatcher.Invoke(() =>
                {
                    lblTemp.Content = "Check axles / total";
                    SetOkButtonWaitState();
                });
                return;
            }

            // 👇 elimina estas dos líneas
            // if (_isSimulated && _simSavedOnce)
            //     return;

            // Snapshot listo para OK
            _snapAx1 = _ax1;
            _snapAx2 = _ax2;
            _snapAx3 = _ax3;
            _snapTotal = _total;
            _snapUtc = DateTime.UtcNow;
            _snapRaw = "%sim%";

            _canAccept = true;

            Dispatcher.Invoke(() =>
            {
                lblTemp.Content = "STABLE — press OK";
                SetOkButtonReadyState();
            });
        }



        private void InitSummaryUi()
        {
            try
            {
                PagosList.ItemsSource = _pagos;
                RefreshSummary();
            }
            catch { }
        }

        private void RefreshSummary()
        {
            try
            {
                TotalVentaBigText.Text = _ventaTotal.ToString("C", _moneyCulture);

                var recibido = 0m;
                foreach (var p in _pagos) recibido += p.Monto;
                var totalCalculado = _ventaTotal - _descuento + _impuestos + _comisiones;

                var diff = recibido - totalCalculado;

                PagoRecibidoText.Text = recibido.ToString("N2", _moneyCulture);
                BalanceText.Text = Math.Abs(diff).ToString("N2", _moneyCulture);

                var rojo = (Brush)(TryFindResource("MaterialDesignValidationErrorBrush") ?? Brushes.IndianRed);
                var ok = (Brush)(TryFindResource("PrimaryHueMidBrush") ?? TryFindResource("PrimaryBrush") ?? Brushes.SeaGreen);
                BalanceText.Foreground = diff >= 0 ? ok : rojo;

                if (PagosEmpty != null)
                    PagosEmpty.Visibility = _pagos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                BalanceCaption.Text = diff >= 0 ? "Change" : T("Payment.BalanceKpi");
            }
            catch { }
        }

        // ===== DB init en Window_Loaded =====
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var dbCfg = new DatabaseConfig();
            var factory = new MySqlConnectionFactory(dbCfg);
            _logger = new WeightLogger(factory);

            try
            {
                using var _ = await factory.CreateOpenConnectionAsync();
            }
            catch (Exception ex)
            {
                WarnAndLog("UI_BIND_ERROR",
                    "No se pudo preparar la lista de recientes.",
                    ex.ToString(),
                    "RecientesList.ItemsSource");
            }

            InitSummaryUi();

            lblTemp.Content = "Connecting…";
            lblEstado.Content = "Opening serial port…";

            // Conexión automática a la báscula (real o simulada)
            TryAutoConnectAtBoot();
            SetOkButtonWaitState();

            await LoadProductsAsync();
            await LoadVehicleTypesAsync();
            PagosList.ItemsSource = _pagos;
            await LoadPaymentMethodsAsync();

            if (_isConnected)
            {
                lblTemp.Content = "Waiting for truck…";
                lblEstado.Content = "Scale ready.";
            }
        }

        private void WarnAndLog(string kind, string userMessage, string? detail = null, string? raw = null)
        {
            _ = (_logger?.LogEventAsync(kind, rawLine: raw, note: detail ?? userMessage)) ?? Task.CompletedTask;
            MessageBox.Show(userMessage, "TruckScale POS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool _isConnected;  // solo conexión al puerto

        private void SetUiReady(bool ready, string status = null)
        {
            _isConnected = ready;
            MidCol.IsEnabled = ready;
            RightCol.IsEnabled = ready;

            UiBlocker.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            UiBlocker.IsHitTestVisible = !ready;

            if (status != null) lblTemp.Content = status;
        }

        // ===== Estado actual del producto seleccionado =====
        private int _selectedProductId;
        private string _selectedProductCode;
        private string _selectedProductName;
        private decimal _selectedProductPrice;
        private string _selectedCurrency = "USD";

        // ---- Modelo de producto para UI ----
        public sealed class ProductInfo
        {
            public int Id { get; init; }
            public string Code { get; init; } = "";
            public string Name { get; init; } = "";
            public decimal Price { get; init; }
            public string Currency { get; init; } = "USD";
        }

        // Cache en memoria por code ("WEIGH"/"REWEIGH")
        private readonly Dictionary<string, ProductInfo> _products =
            new(StringComparer.OrdinalIgnoreCase);

        private string GetPrimaryConn() => GetConnectionString();


        private string GetLocalConn()
        {
            var cfg = PosConfigService.Load();
            if (!string.IsNullOrWhiteSpace(cfg.LocalDbStrCon))
                return cfg.LocalDbStrCon;

            // Fallback legacy
            return "Server=127.0.0.1;Port=3306;Database=truckscale;Uid=localuser;Pwd=localpass;SslMode=None;";
        }
        private async Task LoadProductsAsync()
        {
            if (!await TryLoadProductsAsync(GetPrimaryConn()))
            {
                AppendLog("[Products] Primary DB failed, trying local…");
                if (!await TryLoadProductsAsync(GetLocalConn()))
                {
                    AppendLog("[Products] Local DB failed. Products unavailable.");
                    WeighToggle.IsEnabled = false;
                    ReweighToggle.IsEnabled = false;
                    lblEstado.Content = "Products unavailable (DB offline).";
                    return;
                }
            }
            UpdateProductButtonsEnabled();
        }

        private async Task<bool> TryLoadProductsAsync(string connStr)
        {
            try
            {
                using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                const string SQL = @"SELECT product_id, code, name, default_price, currency 
                                     FROM products 
                                     WHERE is_active = 1 AND code IN ('WEIGH','REWEIGH');";

                using var cmd = new MySqlCommand(SQL, conn);
                using var rd = await cmd.ExecuteReaderAsync();

                var found = 0;
                while (await rd.ReadAsync())
                {
                    var p = new ProductInfo
                    {
                        Id = rd.GetInt32("product_id"),
                        Code = rd.GetString("code"),
                        Name = rd.GetString("name"),
                        Price = rd.GetDecimal("default_price"),
                        Currency = rd.GetString("currency")
                    };
                    _products[p.Code] = p;
                    found++;
                }

                AppendLog($"[Products] Loaded {found} product(s) from {conn.DataSource}.");
                return found > 0;
            }
            catch (Exception ex)
            {
                AppendLog($"[Products] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private void SetProduct(int id, string code, string name, decimal price, string currency = "USD")
        {
            _selectedProductId = id;
            _selectedProductCode = code;
            _selectedProductName = name;
            _selectedProductPrice = price;
            _selectedCurrency = currency;

            _ventaTotal = price;
            TotalVentaBigText.Text = string.Format(
                _selectedCurrency == "USD" ? CultureInfo.GetCultureInfo("en-US")
                                           : CultureInfo.GetCultureInfo("es-MX"),
                "{0:C}", _selectedProductPrice);

            RefreshSummary();
        }

        private void ApplySelected(string code)
        {
            if (_products.TryGetValue(code, out var p))
            {
                SetProduct(p.Id, p.Code, p.Name, p.Price, p.Currency);
            }
            else
            {
                lblEstado.Content = "Product not available.";
            }
        }

        private void WeighToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (ReweighToggle.IsChecked == true) ReweighToggle.IsChecked = false;
            ApplySelected("WEIGH");
        }

        private void ReweighToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (WeighToggle.IsChecked == true) WeighToggle.IsChecked = false;
            ApplySelected("REWEIGH");
        }

        private async Task<string?> TryGetLastWeightUuidAsync(string connStr)
        {
            const string SQL = @"SELECT uuid_weight 
                                 FROM scale_session_axles 
                                 WHERE captured_local >= CURDATE() 
                                 ORDER BY id DESC LIMIT 1;";

            try
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand(SQL, conn);
                var obj = await cmd.ExecuteScalarAsync();
                return obj as string;
            }
            catch { return null; }
        }

        private Task<string?> GetWeightUuidForLinkAsync()
        {
            return Task.FromResult(_currentWeightUuid);
        }

        /// <DriverInfo>
        /// UG Apartado de DriverInfo
        /// </DriverInfo>
        private sealed class DriverInfo
        {
            public long Id { get; init; }
            public string First { get; init; } = "";
            public string Last { get; init; } = "";
            public string Phone { get; init; } = "";
            public string License { get; init; } = "";
            public DateTime? LicenseExpiry { get; init; }
            public string Plates { get; init; } = "";
            public int? VehicleTypeId { get; init; }
            public string VehicleBrand { get; init; } = "";
            public string VehicleModel { get; init; } = "";
            public string VehicleTypeName { get; init; } = "";
        }

        private async Task<long> InsertDriverInfoAsync(
            string? saleUid,
            string firstName,
            string lastName,
            string phone,
            string licenseNo,
            DateTime? licenseExpiry,
            string plates,
            int? vehicleTypeId,
            string brand,
            string model,
            string identifyBy,
            string matchKey,
            string? notes)
        {
            string connStr = GetConnectionString();

            const string SQL = @"INSERT INTO sale_driver_info
                                (sale_uid, driver_first_name, driver_last_name, driver_phone, license_number, license_expiry,
                                 vehicle_plates, vehicle_type_id, vehicle_brand, vehicle_model, identify_by, match_key, notes, created_at)
                                VALUES
                                (@sale_uid, @first, @last, @phone, @lic, @exp, @plates, @vtid, @brand, @model, @idby, @mkey, @notes, NOW())
                                ON DUPLICATE KEY UPDATE
                                sale_uid = COALESCE(sale_driver_info.sale_uid, VALUES(sale_uid)),
                                driver_first_name = IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_first_name), sale_driver_info.driver_first_name),
                                driver_last_name  = IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_last_name),  sale_driver_info.driver_last_name),
                                driver_phone      = IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_phone),      sale_driver_info.driver_phone),
                                license_number    = IF(sale_driver_info.sale_uid IS NULL, VALUES(license_number),    sale_driver_info.license_number),
                                license_expiry    = IF(sale_driver_info.sale_uid IS NULL, VALUES(license_expiry),    sale_driver_info.license_expiry),
                                vehicle_plates    = IF(sale_driver_info.sale_uid IS NULL, VALUES(vehicle_plates),    sale_driver_info.vehicle_plates),
                                vehicle_type_id   = IF(sale_driver_info.sale_uid IS NULL, VALUES(vehicle_type_id),   sale_driver_info.vehicle_type_id),
                                vehicle_brand     = IF(sale_driver_info.sale_uid IS NULL, VALUES(vehicle_brand),     sale_driver_info.vehicle_brand),
                                vehicle_model     = IF(sale_driver_info.sale_uid IS NULL, VALUES(vehicle_model),     sale_driver_info.vehicle_model),
                                notes             = IF(sale_driver_info.sale_uid IS NULL, VALUES(notes),             sale_driver_info.notes);";

            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(SQL, conn);

            cmd.Parameters.AddWithValue("@sale_uid", (object?)saleUid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@first", firstName);
            cmd.Parameters.AddWithValue("@last", lastName);
            cmd.Parameters.AddWithValue("@phone", phone);
            cmd.Parameters.AddWithValue("@lic", licenseNo);
            cmd.Parameters.AddWithValue("@exp", (object?)licenseExpiry?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@plates", plates);
            cmd.Parameters.AddWithValue("@vtid", (object?)vehicleTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@brand", brand);
            cmd.Parameters.AddWithValue("@model", model);
            cmd.Parameters.AddWithValue("@idby", identifyBy);
            cmd.Parameters.AddWithValue("@mkey", matchKey);
            cmd.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
            return (long)cmd.LastInsertedId;
        }

        public async Task<int> LinkDriverToSaleAsync(MySqlConnection conn, MySqlTransaction tx, string weightUuid, string saleUid)
        {
            const string SQL = @"UPDATE sale_driver_info 
                                 SET sale_uid = @sale 
                                 WHERE identify_by = 'weight_uuid' AND match_key = @uuid;";

            try
            {
                await using var cmd = new MySqlCommand(SQL, conn, tx);
                cmd.CommandTimeout = 10;
                cmd.Parameters.AddWithValue("@sale", saleUid);
                cmd.Parameters.AddWithValue("@uuid", weightUuid);

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                    throw new InvalidOperationException(
                        $"No rows updated for weight_uuid='{weightUuid}'. ¿Existe el registro previo?");
                return rows;
            }
            catch (Exception ex)
            {
                var msg = $"LinkDriverToSaleAsync(tx) failed. uuid='{weightUuid}', sale='{saleUid}'. {ex.Message}";
                try { Dispatcher?.Invoke(() => txtLogTemp.AppendText(msg + Environment.NewLine)); } catch { }
                throw new Exception(msg, ex);
            }
        }

        private async Task<DriverInfo?> GetDriverByWeightUuidAsync(string uuid)
        {
            const string SQL = @"SELECT sdi.id_driver_info,sdi.driver_first_name, sdi.driver_last_name,
                                        sdi.driver_phone, sdi.license_number, sdi.license_expiry,
                                        sdi.vehicle_plates, sdi.vehicle_type_id,sdi.vehicle_brand, sdi.vehicle_model,
                                        COALESCE(vt.name,'') AS vehicle_type_name
                                 FROM sale_driver_info sdi
                                 LEFT JOIN vehicle_types vt ON vt.vehicle_type_id = sdi.vehicle_type_id
                                 WHERE sdi.identify_by = 'weight_uuid' AND sdi.match_key = @uuid
                                 ORDER BY sdi.id_driver_info DESC LIMIT 1;";

            async Task<DriverInfo?> TryOneAsync(string connStr)
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand(SQL, conn);
                cmd.Parameters.AddWithValue("@uuid", uuid);

                await using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    return new DriverInfo
                    {
                        Id = rd.GetInt64("id_driver_info"),
                        First = rd.GetString("driver_first_name"),
                        Last = rd.GetString("driver_last_name"),
                        Phone = rd.GetString("driver_phone"),
                        License = rd.GetString("license_number"),
                        LicenseExpiry = rd.IsDBNull("license_expiry") ? (DateTime?)null : rd.GetDateTime("license_expiry"),
                        Plates = rd.GetString("vehicle_plates"),
                        VehicleTypeId = rd.IsDBNull("vehicle_type_id") ? (int?)null : rd.GetInt32("vehicle_type_id"),
                        VehicleBrand = rd.IsDBNull("vehicle_brand") ? "" : rd.GetString("vehicle_brand"),
                        VehicleModel = rd.IsDBNull("vehicle_model") ? "" : rd.GetString("vehicle_model"),
                        VehicleTypeName = rd.GetString("vehicle_type_name"),
                    };
                }
                return null;
            }

            try { return await TryOneAsync(GetPrimaryConn()); }
            catch
            {
                AppendLog("[Driver] Primary DB failed, trying local…");
                return await TryOneAsync(GetLocalConn());
            }
        }

        private void ShowDriverCard(DriverInfo d)
        {
            DriverNameText.Text = string.IsNullOrWhiteSpace(d.Last)
                ? d.First
                : $"{d.First} {d.Last}";

            DriverLicenseText.Text = string.IsNullOrWhiteSpace(d.License)
                ? "License: —"
                : (d.LicenseExpiry.HasValue
                    ? $"License: {d.License} (exp. {d.LicenseExpiry:yyyy-MM-dd})"
                    : $"License: {d.License}");

            DriverPlatesText.Text = string.IsNullOrWhiteSpace(d.Plates) ? "Plates: —" : $"Plates: {d.Plates}";
            DriverPhoneText.Text = string.IsNullOrWhiteSpace(d.Phone) ? "Phone: —" : $"Phone: {d.Phone}";

            string unit = string.IsNullOrWhiteSpace(d.VehicleTypeName) ? "" : d.VehicleTypeName;
            string brandModel = string.Join(" ", new[] { d.VehicleBrand, d.VehicleModel }.Where(s => !string.IsNullOrWhiteSpace(s)));
            string combo = string.Join(" · ", new[] { unit, brandModel }.Where(s => !string.IsNullOrWhiteSpace(s)));
            DriverVehicleText.Text = string.IsNullOrWhiteSpace(combo) ? "Unit: —" : $"Unit: {combo}";

            DriverCard.Visibility = Visibility.Visible;
        }

        private void HideDriverCard()
        {
            DriverCard.Visibility = Visibility.Collapsed;
            DriverNameText.Text = DriverLicenseText.Text = DriverPlatesText.Text =
                DriverPhoneText.Text = DriverVehicleText.Text = "—";
        }

        /// <vehicle_types>
        /// UG Apartado de vehicle_types
        /// </vehicle_types>
        public sealed class VehicleType
        {
            public int Id { get; init; }
            public string Code { get; init; } = "";
            public string Name { get; init; } = "";
        }

        private readonly ObservableCollection<VehicleType> _vehicleTypes = new();

        private async Task<bool> TryLoadVehicleTypesAsync(string connStr)
        {
            try
            {
                using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                const string SQL = @"SELECT vehicle_type_id, code, name  
                                     FROM vehicle_types  
                                     WHERE is_active = 1 
                                     ORDER BY vehicle_type_id";

                using var cmd = new MySqlCommand(SQL, conn);
                using var rd = await cmd.ExecuteReaderAsync();

                _vehicleTypes.Clear();
                while (await rd.ReadAsync())
                {
                    _vehicleTypes.Add(new VehicleType
                    {
                        Id = rd.GetInt32("vehicle_type_id"),
                        Code = rd.GetString("code"),
                        Name = rd.GetString("name")
                    });
                }
                AppendLog($"[VehicleTypes] Loaded: {_vehicleTypes.Count}.");
                return _vehicleTypes.Count > 0;
            }
            catch (Exception ex)
            {
                AppendLog($"[VehicleTypes] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async Task LoadVehicleTypesAsync()
        {
            if (!await TryLoadVehicleTypesAsync(GetPrimaryConn()))
            {
                AppendLog("[VehicleTypes] Primary DB failed, trying local…");
                if (!await TryLoadVehicleTypesAsync(GetLocalConn()))
                {
                    AppendLog("[VehicleTypes] Local DB failed. Seeding defaults.");
                    _vehicleTypes.Clear();
                    _vehicleTypes.Add(new VehicleType { Id = 1, Code = "TRACTOR", Name = "Tractor" });
                    _vehicleTypes.Add(new VehicleType { Id = 2, Code = "TORTON", Name = "Torton" });
                    _vehicleTypes.Add(new VehicleType { Id = 3, Code = "TRUCK35", Name = "Truck 3.5t" });
                    _vehicleTypes.Add(new VehicleType { Id = 4, Code = "PICKUP", Name = "Pickup" });
                }
            }

            TipoUnidadCombo.ItemsSource = _vehicleTypes;
            TipoUnidadCombo.DisplayMemberPath = "Name";
            TipoUnidadCombo.SelectedValuePath = "Id";
        }

        /// <_rxDigits>
        /// UG Formato numero 
        /// </_rxDigits>
        private static readonly Regex _rxDigits = new Regex("^[0-9]+$", RegexOptions.Compiled);

        private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !_rxDigits.IsMatch(e.Text);
        }

        private void DigitsOnly_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var tb = (TextBox)sender;
            string pasted = (string)e.DataObject.GetData(DataFormats.Text) ?? "";
            string digits = new string(pasted.Where(char.IsDigit).ToArray());

            int room = tb.MaxLength - (tb.Text.Length - tb.SelectionLength);
            if (room <= 0)
            {
                e.CancelCommand();
                return;
            }

            if (digits.Length == 0)
            {
                e.CancelCommand();
                return;
            }

            if (digits.Length > room)
                digits = digits.Substring(0, room);

            tb.SelectedText = digits;
            e.CancelCommand();
        }

        /// <payment_methods>
        /// UG Apartado de payment_methods
        /// </payment_methods>        
        private static readonly Dictionary<string, PackIconKind> _methodIconMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["cash"] = PackIconKind.Cash,
                ["credit"] = PackIconKind.CreditCardOutline,
                ["debit"] = PackIconKind.CreditCard,
                ["wire"] = PackIconKind.BankTransfer,
                ["usd"] = PackIconKind.CurrencyUsd,
            };

        private async Task LoadPaymentMethodsAsync()
        {
            async Task<bool> TryOneAsync(string connStr)
            {
                try
                {
                    using var conn = new MySqlConnection(connStr);
                    await conn.OpenAsync();

                    const string SQL = @"SELECT method_id, code, name, is_cash, allow_reference, is_active 
                                         FROM payment_methods 
                                         WHERE is_active = 1 
                                         ORDER BY method_id;";

                    using var cmd = new MySqlCommand(SQL, conn);
                    using var rd = await cmd.ExecuteReaderAsync();

                    PaymentMethods.Clear();
                    while (await rd.ReadAsync())
                    {
                        var code = rd.GetString("code");
                        PaymentMethods.Add(new PaymentMethod
                        {
                            Id = rd.GetInt32("method_id"),
                            Code = code,
                            Name = rd.GetString("name"),
                            IsCash = rd.GetBoolean("is_cash"),
                            AllowReference = rd.GetBoolean("allow_reference"),
                            IsActive = rd.GetBoolean("is_active"),
                            IconKind = _methodIconMap.TryGetValue(code, out var kind)
                                ? kind
                                : PackIconKind.CashRegister
                        });
                    }
                    AppendLog($"[Payments] Loaded {PaymentMethods.Count} method(s) from {conn.DataSource}.");
                    return PaymentMethods.Count > 0;
                }
                catch (Exception ex)
                {
                    AppendLog($"[Payments] {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            if (!await TryOneAsync(GetPrimaryConn()))
            {
                AppendLog("[Payments] Primary DB failed, trying local…");
                await TryOneAsync(GetLocalConn());
            }

            SelectPaymentByCode("cash");
        }

        private void SelectPaymentByCode(string code)
        {
            PaymentMethod? sel = null;
            foreach (var m in PaymentMethods)
            {
                var isSel = string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase);
                m.IsSelected = isSel;
                if (isSel) sel = m;
            }

            if (sel != null)
            {
                _selectedPaymentId = sel.Code;
                RefPanel.Visibility = sel.AllowReference ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void PaymentMethod_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PaymentMethod pm)
                SelectPaymentByCode(pm.Code);
        }

        /// <Servicios>
        /// UG BOTONES DE PRODUCTOS
        /// </Servicios> 
        private void UpdateProductButtonsEnabled()
        {
            bool hasWeigh = _products.ContainsKey("WEIGH");
            bool hasReweigh = _products.ContainsKey("REWEIGH");

            WeighToggle.IsEnabled = _driverLinked && hasWeigh;
            ReweighToggle.IsEnabled = _driverLinked && hasReweigh;

            if (!WeighToggle.IsEnabled) WeighToggle.IsChecked = false;
            if (!ReweighToggle.IsEnabled) ReweighToggle.IsChecked = false;
        }

        /// <sales>
        /// UG section save sales
        /// </sales> 
        private int _siteId = 0;       // setéalo al arrancar (ej. 1)
        private int _terminalId = 0;   // setéalo al arrancar (ej. 1)
        private int _operatorId = 1;   // por ahora 'admin' = 1

        private async Task<int> GetDefaultSiteIdAsync(MySqlConnection conn, MySqlTransaction tx)
        {
            const string Q = "SELECT site_id FROM sites ORDER BY site_id LIMIT 1;";
            using var cmd = new MySqlCommand(Q, conn, tx);
            var obj = await cmd.ExecuteScalarAsync();
            return (obj == null || obj == DBNull.Value) ? 1 : Convert.ToInt32(obj);
        }

        private async Task<int> GetDefaultTerminalIdAsync(MySqlConnection conn, MySqlTransaction tx)
        {
            const string Q = "SELECT terminal_id FROM pos_terminals ORDER BY terminal_id LIMIT 1;";
            using var cmd = new MySqlCommand(Q, conn, tx);
            var obj = await cmd.ExecuteScalarAsync();
            return (obj == null || obj == DBNull.Value) ? 1 : Convert.ToInt32(obj);
        }

        private int GetCurrentOperatorId() => _operatorId > 0 ? _operatorId : 1;

        private async Task<string> SaveSaleAsync()
        {
            if (!_driverLinked) throw new InvalidOperationException("Driver not linked.");
            if (_selectedProductId <= 0) throw new InvalidOperationException("No service selected.");

            var (subtotal, tax, total) = ComputeTotals();
            var recibido = SumReceived();
            if (recibido < total) throw new InvalidOperationException("Received amount is less than order total.");

            var saleUid = Guid.NewGuid().ToString();

            const int STATUS_SALE_COMPLETED = 2; // SALES.COMPLETED
            const int STATUS_PAY_RECEIVED = 6;   // PAYMENTS.RECEIVED

            await using var conn = new MySqlConnection(GetConnectionString());
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                int siteId = _siteId > 0 ? _siteId : await GetDefaultSiteIdAsync(conn, (MySqlTransaction)tx);
                int terminalId = _terminalId > 0 ? _terminalId : await GetDefaultTerminalIdAsync(conn, (MySqlTransaction)tx);
                int operatorId = GetCurrentOperatorId();

                const string SQL_SALE = @"INSERT INTO sales
                         (sale_uid, site_id, terminal_id, operator_id, sale_status_id, currency, subtotal, tax_total, total, created_at)
                         VALUES
                         (@uid, @site, @term, @op, @st, @ccy, @sub, @tax, @tot, NOW());";

                await using (var cmd = new MySqlCommand(SQL_SALE, conn, (MySqlTransaction)tx))
                {
                    cmd.Parameters.AddWithValue("@uid", saleUid);
                    cmd.Parameters.AddWithValue("@site", siteId);
                    cmd.Parameters.AddWithValue("@term", terminalId);
                    cmd.Parameters.AddWithValue("@op", operatorId);
                    cmd.Parameters.AddWithValue("@st", STATUS_SALE_COMPLETED);
                    cmd.Parameters.AddWithValue("@ccy", _selectedCurrency ?? "USD");
                    cmd.Parameters.AddWithValue("@sub", subtotal);
                    cmd.Parameters.AddWithValue("@tax", tax);
                    cmd.Parameters.AddWithValue("@tot", total);
                    await cmd.ExecuteNonQueryAsync();
                }

                var qty = 1m;
                var unit = "EA";
                var lineSubtotal = _selectedProductPrice * qty;
                var discount = 0m;
                var taxable = 0;
                var taxRatePct = 0m;
                var taxAmount = 0m;
                var lineTotal = lineSubtotal - discount + taxAmount;

                const string SQL_LINE = @"INSERT INTO sale_lines
                                            (sale_uid, seq, product_id, description, qty, unit, unit_price,
                                             line_subtotal, discount_amount, taxable, tax_rate_percent, tax_amount, line_total)
                                        VALUES
                                            (@uid, @seq, @pid, @desc, @qty, @unit, @price,
                                             @lsub, @disc, @taxable, @trate, @tamt, @ltot);";

                await using (var cmd = new MySqlCommand(SQL_LINE, conn, (MySqlTransaction)tx))
                {
                    cmd.Parameters.AddWithValue("@uid", saleUid);
                    cmd.Parameters.AddWithValue("@seq", 1);
                    cmd.Parameters.AddWithValue("@pid", _selectedProductId);
                    cmd.Parameters.AddWithValue("@desc", _selectedProductName ?? "Service");
                    cmd.Parameters.AddWithValue("@qty", qty);
                    cmd.Parameters.AddWithValue("@unit", unit);
                    cmd.Parameters.AddWithValue("@price", _selectedProductPrice);
                    cmd.Parameters.AddWithValue("@lsub", lineSubtotal);
                    cmd.Parameters.AddWithValue("@disc", discount);
                    cmd.Parameters.AddWithValue("@taxable", taxable);
                    cmd.Parameters.AddWithValue("@trate", taxRatePct);
                    cmd.Parameters.AddWithValue("@tamt", taxAmount);
                    cmd.Parameters.AddWithValue("@ltot", lineTotal);
                    await cmd.ExecuteNonQueryAsync();
                }

                const string SQL_PAY = @"INSERT INTO payments
                                            (payment_uid, sale_uid, method_id, payment_status_id, amount, currency,
                                             exchange_rate, reference_number, received_by, received_at)
                                        VALUES
                                            (@puid, @uid, @mid, @st, @amt, @ccy,
                                             @rate, @ref, @rcvby, NOW());";

                foreach (var p in _pagos)
                {
                    int? methodId = FindPaymentMethodIdByCode(p.Code);
                    if (methodId == null)
                        throw new InvalidOperationException($"Payment method '{p.Code}' not found/mapped.");

                    await using var cmd = new MySqlCommand(SQL_PAY, conn, (MySqlTransaction)tx);
                    cmd.Parameters.AddWithValue("@puid", Guid.NewGuid().ToString());
                    cmd.Parameters.AddWithValue("@uid", saleUid);
                    cmd.Parameters.AddWithValue("@mid", methodId.Value);
                    cmd.Parameters.AddWithValue("@st", STATUS_PAY_RECEIVED);
                    cmd.Parameters.AddWithValue("@amt", p.Monto);
                    cmd.Parameters.AddWithValue("@ccy", _selectedCurrency ?? "USD");
                    cmd.Parameters.AddWithValue("@rate", 1m);
                    cmd.Parameters.AddWithValue("@ref", (object?)p.Ref ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@rcvby", GetCurrentOperatorId());
                    await cmd.ExecuteNonQueryAsync();
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(_currentWeightUuid))
                        await LinkDriverToSaleAsync(conn, (MySqlTransaction)tx, _currentWeightUuid!, saleUid);
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"Error linking driver (weight_uuid='{_currentWeightUuid}', sale_uid='{saleUid}'). {ex.Message}", ex);
                }

                var amountPaid = recibido;
                var balanceDue = Math.Max(total - amountPaid, 0m);

                const string SQL_SALE_UPDATE = @"UPDATE sales
                                                    SET amount_paid = @paid,
                                                        balance_due = @bal,
                                                        occurred_at = COALESCE(occurred_at, NOW())
                                                WHERE sale_uid = @uid;";

                await using (var up = new MySqlCommand(SQL_SALE_UPDATE, conn, (MySqlTransaction)tx))
                {
                    up.Parameters.AddWithValue("@paid", amountPaid);
                    up.Parameters.AddWithValue("@bal", balanceDue);
                    up.Parameters.AddWithValue("@uid", saleUid);
                    await up.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return saleUid;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // Campos para señales de cierre
        private TaskCompletionSource<bool>? _saleDialogTcs;
        private TaskCompletionSource<bool>? _drvDialogTcs;

        // === Modal: Sale completed ===
        private async Task ShowSaleCompletedDialogAsync(decimal total, decimal recibido, decimal change, string saleUid)
        {
            DlgSaleTotal.Text = total.ToString("C", _moneyCulture);
            DlgSaleReceived.Text = recibido.ToString("C", _moneyCulture);
            DlgSaleChange.Text = change.ToString("C", _moneyCulture);
            DlgSaleUid.Text = $"Ticket: {saleUid}";

            _saleDialogTcs = new TaskCompletionSource<bool>();
            DlgCloseBtn.Click += CloseSaleDlg_Click;
            DlgReprintBtn.Click += (s, e) => { _ = ReprintTicketAsync(saleUid); };

            DlgTicketProgress.IsIndeterminate = true;
            _ = Task.Run(async () =>
            {
                try { await PrintTicketAsync(saleUid); }
                catch { }
                finally
                {
                    Dispatcher.Invoke(() => DlgTicketProgress.IsIndeterminate = false);
                }
            });

            SaleCompletedHost.IsOpen = true;
            await _saleDialogTcs.Task;
        }

        private void CloseSaleDlg_Click(object? sender, RoutedEventArgs e)
        {
            SaleCompletedHost.IsOpen = false;
            DlgCloseBtn.Click -= CloseSaleDlg_Click;
            _saleDialogTcs?.TrySetResult(true);
        }

        private Task PrintTicketAsync(string saleUid)
        {
            return Task.Delay(900);
        }

        private Task ReprintTicketAsync(string saleUid)
        {
            return PrintTicketAsync(saleUid);
        }

        // === Modal: Driver saved ===
        private async Task ShowDriverSavedDialogAsync(string fullName, string? plates, string? license)
        {
            DlgDrvName.Text = fullName;
            DlgDrvExtra.Text = $"{(string.IsNullOrWhiteSpace(plates) ? "" : $"Plates: {plates}   ")}{(string.IsNullOrWhiteSpace(license) ? "" : $"License: {license}")}".Trim();

            _drvDialogTcs = new TaskCompletionSource<bool>();
            DlgDrvOkBtn.Click += CloseDrvDlg_Click;

            DriverSavedHost.IsOpen = true;
            await _drvDialogTcs.Task;
        }

        private void CloseDrvDlg_Click(object? sender, RoutedEventArgs e)
        {
            DriverSavedHost.IsOpen = false;
            DlgDrvOkBtn.Click -= CloseDrvDlg_Click;
            _drvDialogTcs?.TrySetResult(true);
        }
        private void SetOkButtonWaitState()
        {
            try
            {
                OkButtonLabel.Text = "WAIT";
                OkButtonIcon.Kind = PackIconKind.TimerSand;

                var waitBg =
                    TryFindResource("WaitCtaBg") as Brush ??
                    TryFindResource("MaterialDesignValidationErrorBrush") as Brush ??
                    Brushes.IndianRed;

                OkButton.Background = waitBg;
            }
            catch { /* no rompemos la app por temas de UI */ }
        }


        private void SetOkButtonReadyState()
        {
            try
            {
                OkButtonLabel.Text = "OK";
                OkButtonIcon.Kind = PackIconKind.CheckCircle;

                var okBg =
                    TryFindResource("StartCtaBg") as Brush ??
                    TryFindResource("PrimaryHueMidBrush") as Brush ??
                    TryFindResource("PrimaryBrush") as Brush ??
                    Brushes.SeaGreen;

                OkButton.Background = okBg;
            }
            catch { }
        }
        private async void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Si está en modo WAIT, no hace nada
            if (!_canAccept)
            {
                AppendLog("[OK] Click ignored (no stable weight to accept).");
                return;
            }

            // Tomamos el snapshot estable preparado por EvaluateStabilityAndUpdateUi o TryAcceptStableSet
            var uuid = GenerateUid10();
            _currentWeightUuid = uuid;

            var ax1 = _snapAx1;
            var ax2 = _snapAx2;
            var ax3 = _snapAx3;
            var total = _snapTotal;
            var raw = _snapRaw;

            try
            {
                AppendLog($"[OK] Saving snapshot uuid={uuid} total={total:0.0} lb");
                var id = await saveScaleData(ax1, ax2, ax3, total, raw, uuid);
                AppendLog($"[DB] Insertado id={id} (uuid={uuid})");

                // Reglas para siguiente camión
                _lastPersistedTotal = total;
                _canAccept = false;
                _autoStable = false;
                _winTotals.Clear();
                //if (_isSimulated) _simSavedOnce = true;

                Dispatcher.Invoke(() =>
                {
                    // Mostrar el peso que REALMENTE se guardó (redondeado igual que en DB)
                    int pesoEntero = (int)Math.Round(total);
                    lblUUID.Content = $"{pesoEntero} lb";

                    lblTemp.Content = "Waiting for next truck…";
                    SetOkButtonWaitState();
                });
            }
            catch (Exception ex)
            {
                AppendLog("[DB] ERROR al guardar desde OK: " + ex);
                Dispatcher.Invoke(() =>
                {
                    lblTemp.Content = "DB ERROR";
                    SetOkButtonWaitState();
                });
                _ = _logger?.LogEventAsync("ACCEPT_EX", rawLine: raw, note: ex.ToString());
            }
        }

    }
}
