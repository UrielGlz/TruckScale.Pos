using MaterialDesignThemes.Wpf;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Xps; // para XpsDocumentWriter
using TruckScale.Pos.Tickets;
using TruckScale.Pos.Config;
using TruckScale.Pos.Data;
using System.Printing;
using TruckScale.Pos.Domain;
using System.Text.Json;

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
    public class LicenseState
    {
        public int Id { get; set; }
        public string CountryCode { get; set; } = "";
        public string Code { get; set; } = "";   // state_code
        public string Name { get; set; } = "";   // state_name

        public string Display => $"{Name} ({Code})";
    }
    public sealed class DriverProduct
    {
        public int Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";

        public string Display => string.IsNullOrWhiteSpace(Code)
            ? Name
            : $"{Name} ({Code})";
    }


    public partial class MainWindow : Window
    {
        private string _ticketPrinterName = "";

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
        private string _selectedPaymentId = "";

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
        private readonly List<TransportAccount> _accounts = new();
        private readonly List<LicenseState> _licenseStates = new();

        // ===== Driver phone lookup =====
        private string _driverPhoneDigits = "";
        private const int DRIVER_PHONE_LEN = 10;

        private static string OnlyDigits(string? s)
            => string.IsNullOrEmpty(s)
                ? ""
                : new string(s.Where(char.IsDigit).ToArray());

        private static string FormatPhone10(string digits)
        {
            digits = OnlyDigits(digits);
            if (digits.Length != 10) return digits;
            return $"({digits.Substring(0, 3)}) {digits.Substring(3, 3)}-{digits.Substring(6, 4)}";
        }
        private readonly ObservableCollection<ProductInfo> _productOptions = new();
        private readonly ObservableCollection<DriverProduct> _driverProducts = new();

        // Para recordar datos del último ticket y usarlos al imprimir
        private string _lastTicketUid = "";
        private string _lastTicketNumber = "";
        private string _lastTicketQrJson = "";

        //status_catalogo para TICKETS.PRINTED
        private const int STATUS_TICKET_PRINTED = 9;



        public MainWindow()
        {
            InitializeComponent();

            ApplyTheme();
            ApplyBrand();
            ApplyUi();

            LoadKeypadConfig();
            BuildKeypadUI();

            if (PosSession.IsLoggedIn)
            {
                var opName = string.IsNullOrWhiteSpace(PosSession.FullName)
                    ? PosSession.Username
                    : PosSession.FullName;

                lblEstado.Content = $"Logged in as {opName} ({PosSession.Username})";
                OperatorNameRun.Text = opName;
            }
            else
            {
                OperatorNameRun.Text = "—";
            }

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
            // 1) Intentar leer desde config.json (PosConfigService)
            var cfg = PosConfigService.Load();

            if (!string.IsNullOrWhiteSpace(cfg.MainDbStrCon))
            {
                // Tomamos la cadena guardada y le inyectamos las opciones de fecha
                var fromConfig = new MySqlConnectionStringBuilder(cfg.MainDbStrCon)
                {
                    AllowZeroDateTime = true,      // permite 0000-00-00...
                    ConvertZeroDateTime = true     // los convierte a DateTime.MinValue
                };

                return fromConfig.ConnectionString;
            }

            // 2) Fallback legacy (lo que ya tenías), pero con las mismas opciones
            var builder = new MySqlConnectionStringBuilder
            {
                Server = Environment.GetEnvironmentVariable("BAKEOPS_DB_HOST") ?? "lunisapp.com",
                Database = Environment.GetEnvironmentVariable("BAKEOPS_DB_NAME") ?? "lunisapppudin_demo_scale",
                UserID = Environment.GetEnvironmentVariable("BAKEOPS_DB_USER") ?? "lunisapppudin_admin",
                Password = Environment.GetEnvironmentVariable("BAKEOPS_DB_PASS") ?? "andy012809$50$565etd69873",
                SslMode = MySqlSslMode.Preferred,
                ConnectionTimeout = 8,
                DefaultCommandTimeout = 30,

                // 👇 clave para quitar el error de impresión
                AllowZeroDateTime = true,
                ConvertZeroDateTime = true
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
            // Bloquea el formulario para el siguiente camión
            _formUnlocked = false;

            _simSavedOnce = false;
            _autoStable = false;
            _winTotals.Clear();
            _canAccept = false;

            HideDriverCard();
            UpdateProductButtonsEnabled();
            try { WeighToggle.IsChecked = false; } catch { }
            try { ReweighToggle.IsChecked = false; } catch { }

            // 2) Limpia modal (datos de chofer / cuenta / unidad)
            try { ChoferNombreText.Text = ""; } catch { }
            try { ChoferApellidosText.Text = ""; } catch { }
            try { LicenciaNumeroText.Text = ""; } catch { }
            try { PlacasRegText.Text = ""; } catch { }

            // Unidad
            try { TrailerNumberText.Text = ""; } catch { }
            try { TractorNumberText.Text = ""; } catch { }
            try { LicenseStateCombo.SelectedIndex = -1; } catch { }

            // Datos de cuenta
            try { AccountNameText.Text = ""; } catch { }
            try { AccountAddressText.Text = ""; } catch { }
            try { AccountCountryText.Text = ""; } catch { }
            try { AccountStateText.Text = ""; } catch { }

            // Cliente / producto
            try { ClienteRegCombo.SelectedIndex = 0; } catch { }   // 0 = Cash sale – no account
                                                                   // Datos de chofer
            try { DriverPhoneText.Text = ""; } catch { }
            try { DriverPhoneStatusText.Visibility = Visibility.Collapsed; } catch { }
            try { ChoferNombreText.Text = ""; } catch { }
            try { ChoferApellidosText.Text = ""; } catch { }
            try { LicenciaNumeroText.Text = ""; } catch { }
          
            _driverPhoneDigits = "";

            try { RootDialog.IsOpen = false; } catch { }
            //try { ProductoRegCombo.SelectedIndex = -1; } catch { }
            try { ProductoRegText.SelectedIndex = -1; } catch { }


            try { DriverPhoneText.Text = ""; } catch { }
           

            

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
            try
            {
                TotalVentaBigText.Text = _ventaTotal.ToString("C", _moneyCulture);
            }
            catch { }

            // 4) Pagos / referencia / teclado
            try { RefText.Text = ""; } catch { }
            try { RefPanel.Visibility = Visibility.Collapsed; } catch { }
            _pagos.Clear();

            // Ningún método de pago seleccionado hasta que el operador elija uno
            _selectedPaymentId = "";
            SelectPaymentByCode(null);

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
            try
            {
                lblEje1.Content = "";
                lblEje2.Content = "";
                lblEje3.Content = "";
                lblUUID.Content = "";
            }
            catch { }
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

                SetOkButtonWaitState();
            }
            catch { }

            SetUiReady(_isConnected, null);
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
                //KeysItems.ItemsSource = _kp.Keys;


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
            //try { KeypadDisplay.Text = KeypadText; } catch { }
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
                if (decimal.TryParse(KeypadText, NumberStyles.Any, CultureInfo.InvariantCulture, out var importe)
                    && importe > 0)
                {
                    if (string.IsNullOrWhiteSpace(_selectedPaymentId))
                    {
                        await ShowAlertAsync(
                            "Payment method required",
                            "Please select the payment method (Cash, Card, etc.) before adding an amount.",
                            PackIconKind.Cash);
                        return;
                    }

                    // Valida chofer ligado
                    if (!await EnsureDriverLinkedForPaymentAsync())
                        return;

                    AddPayment(_selectedPaymentId, importe);
                }

                _keypadBuffer = "";
                RefreshKeypadDisplay();
                RefreshSummary();   // esto actualiza totales y el estado de DONE
            }
            catch
            {
                // silencioso para no romper la UI
            }
        }

        private async Task<bool> EnsureDriverLinkedForPaymentAsync()
        {
            if (_driverLinked && !string.IsNullOrWhiteSpace(_currentWeightUuid))
                return true;

            await ShowAlertAsync(
                "Driver required",
                "Please register the driver for the current weight before adding payments.",
                PackIconKind.AccountAlertOutline);

            return false;
        }

        private async Task TryAutoCompleteSaleAsync()
        {
            if (_isCompletingSale)
                return;

            var (subtotal, tax, total) = ComputeTotals();
            var recibido = SumReceived();

            // 1) Validaciones de negocio
            if (!_driverLinked)
            {
                await ShowAlertAsync(
                    "Driver required",
                    "Please register the driver for the current weight before completing the sale.",
                    PackIconKind.AccountAlertOutline);
                return;
            }

            if (_selectedProductId <= 0)
            {
                await ShowAlertAsync(
                    "Service required",
                    "Please select a service before completing the sale.",
                    PackIconKind.InformationOutline);
                return;
            }

            if (_pagos.Count == 0)
            {
                await ShowAlertAsync(
                    "Payment required",
                    "Please record at least one payment before completing the sale.",
                    PackIconKind.Cash);
                return;
            }

            if (total <= 0m)
            {
                await ShowAlertAsync(
                    "Invalid amount",
                    "The service total must be greater than zero before completing the sale.",
                    PackIconKind.AlertCircle);
                return;
            }

            if (recibido < total)
            {
                await ShowAlertAsync(
                    "Incomplete payment",
                    "Received amount is less than the order total.",
                    PackIconKind.Cash);
                return;
            }

            // 2) Guardar venta
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
                MessageBox.Show("Error completing sale: " + ex.Message,
                                "TruckScale POS",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
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
            if (!decimal.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out var add))
                return;

            // 1) Validar método de pago
            if (string.IsNullOrWhiteSpace(_selectedPaymentId))
            {
                await ShowAlertAsync(
                    "Payment method required",
                    "Please select the payment method (Cash, Card, etc.) before adding an amount.",
                    PackIconKind.Cash);
                return;
            }

            // 2) Validar chofer ligado a este peso
            if (!_driverLinked)
            {
                await ShowAlertAsync(
                    "Driver required",
                    "Please register the driver for the current weight before recording payments.",
                    PackIconKind.AccountAlertOutline);
                return;
            }

            // 3) Agregar directamente el pago con la denominación seleccionada
            AddPayment(_selectedPaymentId, add);

            // 4) Limpiar buffer del keypad (por si en el futuro vuelves a usarlo)
            _keypadBuffer = "";
            RefreshKeypadDisplay();
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
            try { 
                RootDialog.IsOpen = false;
                ResetDriverContext();
            } catch { }
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
        private async Task<bool> ValidateDriverFormAsync()
        {
            //string product = ProductoRegText.Text.Trim();
            // Product (dropdown)
            var selectedProduct = ProductoRegText.SelectedItem as DriverProduct;
            string product = selectedProduct?.Name?.Trim() ?? "";



            string first = ChoferNombreText.Text.Trim();
            string last = ChoferApellidosText.Text.Trim();
            string lic = LicenciaNumeroText.Text.Trim();
            string plates = PlacasRegText.Text.Trim();
            string licState = (LicenseStateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim()
                               ?? LicenseStateCombo.Text.Trim();
            string trailer = TrailerNumberText.Text.Trim();
            string tractor = TractorNumberText.Text.Trim();

            if (string.IsNullOrEmpty(product))
            {
                await ShowAlertAsync("Required field",
                    "Please select the product for this load.",
                    PackIconKind.InformationOutline);
                ProductoRegText.Focus();
                return false;
            }

            // Phone digits
            _driverPhoneDigits = OnlyDigits(DriverPhoneText.Text);
            if (string.IsNullOrEmpty(_driverPhoneDigits) || _driverPhoneDigits.Length != DRIVER_PHONE_LEN)
            {
                await ShowAlertAsync("Required field",
                    "Please enter a 10-digit phone number for the driver.",
                    PackIconKind.InformationOutline);
                DriverPhoneText.Focus();
                return false;
            }


            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(last))
            {
                await ShowAlertAsync("Required field",
                    "Please enter the driver's first and last name.",
                    PackIconKind.InformationOutline);
                ChoferNombreText.Focus();
                return false;
            }
            if (string.IsNullOrEmpty(lic))
            {
                await ShowAlertAsync("Required field",
                    "Please enter the driver's license number (DL#).",
                    PackIconKind.InformationOutline);
                LicenciaNumeroText.Focus();
                return false;
            }
            if (string.IsNullOrEmpty(plates))
            {
                await ShowAlertAsync("Required field",
                    "Please enter the truck license plate.",
                    PackIconKind.InformationOutline);
                PlacasRegText.Focus();
                return false;
            }
            if (string.IsNullOrEmpty(licState))
            {
                await ShowAlertAsync("Required field",
                    "Please select the state where the plate was issued.",
                    PackIconKind.InformationOutline);
                LicenseStateCombo.Focus();
                return false;
            }
            if (string.IsNullOrEmpty(trailer))
            {
                await ShowAlertAsync("Required field",
                    "Please enter the trailer number.",
                    PackIconKind.InformationOutline);
                TrailerNumberText.Focus();
                return false;
            }
            if (string.IsNullOrEmpty(tractor))
            {
                await ShowAlertAsync("Required field",
                    "Please enter the tractor number.",
                    PackIconKind.InformationOutline);
                TractorNumberText.Focus();
                return false;
            }

            return true;
        }

        //UG save driver *sale_driver_info*
        private async void RegisterSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var uuid = _currentWeightUuid;
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    await ShowAlertAsync(
                        "Weight required",
                        "No weight captured yet. Press OK on a stable weight before saving the driver.",
                        PackIconKind.Scale);
                    return;
                }

                if (!await ValidateDriverFormAsync())
                    return;

                

                string first = ChoferNombreText?.Text?.Trim() ?? "";
                string last = ChoferApellidosText?.Text?.Trim() ?? "";
                string licNo = LicenciaNumeroText?.Text?.Trim() ?? "";
                string plates = PlacasRegText?.Text?.Trim() ?? "";

                // NUEVO: tomar tráiler y tractor del formulario
                string trailerNumber = TrailerNumberText?.Text?.Trim() ?? "";
                string tractorNumber = TractorNumberText?.Text?.Trim() ?? "";

                // Estado de la placa (código, ej. "AZ")
                var licenseState = (LicenseStateCombo.SelectedItem as LicenseState)?.Code ?? "";

                var selectedProduct = ProductoRegText.SelectedItem as DriverProduct;
                string productText = selectedProduct?.Name
                                     ?? ProductoRegText.Text?.Trim() ?? "";
                int? driverProductId = selectedProduct?.Id;


                string phoneDigits = _driverPhoneDigits;

                // ===== Datos de cuenta =====
                string? accountNumber = null;
                string accountName;
                string accountAddress;
                string accountCountry;
                string accountState;

                if (ClienteRegCombo?.SelectedItem is TransportAccount acc && acc.IdCustomer != 0)
                {
                    // Cuenta seleccionada del catálogo
                    accountNumber = string.IsNullOrWhiteSpace(acc.AccountNumber) ? null : acc.AccountNumber;
                    accountName = acc.AccountName ?? "";
                    accountAddress = acc.AccountAddress ?? "";
                    accountCountry = acc.AccountCountry ?? "";
                    accountState = acc.AccountState ?? "";
                }
                else
                {
                    // Cash sale / cuenta libre: usamos lo que capturó el operador
                    accountName = AccountNameText?.Text?.Trim() ?? "";
                    accountAddress = AccountAddressText?.Text?.Trim() ?? "";
                    accountCountry = AccountCountryText?.Text?.Trim() ?? "";
                    accountState = AccountStateText?.Text?.Trim() ?? "";
                }

                if (string.IsNullOrWhiteSpace(plates) && string.IsNullOrWhiteSpace(licNo))
                {
                    await ShowAlertAsync(
                        "Missing information",
                        "Enter at least Plates or License number.",
                        PackIconKind.AlertOutline);
                    return;
                }

                //long driverId = await InsertDriverInfoWithFallbackAsync(
                //    saleUid: null,
                //    accountNumber: accountNumber,
                //    accountName: accountName,
                //    accountAddress: accountAddress,
                //    accountCountry: accountCountry,
                //    accountState: accountState,
                //    firstName: first,
                //    lastName: last,
                //    licenseNo: licNo,
                //    licenseState: licenseState,
                //    plates: plates,
                //    trailerNumber: trailerNumber,
                //    tractorNumber: tractorNumber,
                //    productDescription: productText,
                //    identifyBy: "weight_uuid",
                //    matchKey: uuid
                //);

                long driverId = await InsertDriverInfoWithFallbackAsync(
                    saleUid: null,
                    accountNumber: accountNumber,
                    accountName: accountName,
                    accountAddress: accountAddress,
                    accountCountry: accountCountry,
                    accountState: accountState,
                    firstName: first,
                    lastName: last,
                    driverPhone: phoneDigits,
                    licenseNo: licNo,
                    licenseState: licenseState,
                    plates: plates,
                    trailerNumber: trailerNumber,
                    tractorNumber: tractorNumber,
                    driverProductId: driverProductId,                     
                    identifyBy: "weight_uuid",
                    matchKey: uuid
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
                await ShowAlertAsync("Driver error", "Error saving driver: " + ex.Message, PackIconKind.AlertCircle);
            }
        }


        private async Task<long> InsertDriverInfoWithFallbackAsync(
        string? saleUid,
        string? accountNumber,
        string accountName,
        string accountAddress,
        string accountCountry,
        string accountState,
        string firstName,
        string lastName,
        string driverPhone,
        string licenseNo,
        string licenseState,
        string plates,
        string trailerNumber,
        string tractorNumber,
        int? driverProductId,        
        string identifyBy,
        string matchKey)
        {
            try
            {
                return await InsertDriverInfoAsync(
                saleUid, accountNumber, accountName, accountAddress, accountCountry, accountState,
                firstName, lastName, driverPhone, licenseNo, licenseState,
                plates, trailerNumber, tractorNumber, driverProductId,
                identifyBy, matchKey);
            }
            catch (Exception ex1)
            {
                AppendLog("[Driver] Primary DB failed, trying local… " + ex1.Message);

                var localCsb = new MySqlConnectionStringBuilder(GetLocalConn());
                await using var conn = new MySqlConnection(localCsb.ConnectionString);
                await conn.OpenAsync(); // si no hay MySQL local, aquí también truena
                // Aquí se hará duplicar el INSERT para modo offline si lo necesitas.
                throw;
            }
        }


        private readonly ObservableCollection<PaymentEntry> _pagos = new();

        private void AddPayment(string methodCode, decimal monto)
        {
            if (monto <= 0) return;


            if (string.IsNullOrWhiteSpace(methodCode))
            {
                MessageBox.Show(
                    "Please select the payment method (Cash, Credit card, etc.) before entering the amount.",
                    "TruckScale POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }

            if (!_driverLinked)
            {
                MessageBox.Show(
                    "Please register the driver for the current weight before recording payments.",
                    "TruckScale POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }

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

                //BalanceCaption.Text = diff >= 0 ? "Change" : T("Payment.BalanceKpi");
                //actualizar el estado del botón DONE
                UpdateDoneButtonState();
            }
            catch { }
        }
        private void UpdateDoneButtonState()
        {
            try
            {
                if (DoneButton == null)
                    return;

                var (subtotal, tax, total) = ComputeTotals();
                var recibido = SumReceived();

                bool hasProduct = _selectedProductId > 0;
                bool hasPayments = _pagos.Count > 0;
                bool isFullyPaid = total > 0m && recibido >= total;

                // Reglas:
                //  - chofer ligado
                //  - servicio seleccionado
                //  - al menos un pago
                //  - total del servicio cubierto
                DoneButton.IsEnabled = _driverLinked && hasProduct && hasPayments && isFullyPaid;
            }
            catch
            {
                // No romper la app por temas de UI
            }
        }

        // ===== DB init en Window_Loaded =====
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // === Config general (incluye impresora) ===
            ConfigManager.Load();
            _ticketPrinterName = ConfigManager.Current.TicketPrinterName; //Si nunca configuramos TicketPrinterName, se quedará vacío y usaremos la predeterminada.

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
            await LoadDriverProductsAsync();
            await LoadVehicleTypesAsync();
            await LoadAccountsAsync();
            await LoadLicenseStatesAsync();
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
        private bool _formUnlocked = false; // habilita MidCol/RightCol sólo después de OK

        private void SetUiReady(bool ready, string status = null)
        {
            _isConnected = ready;

            // El formulario sólo se habilita si:
            //  - hay conexión a la báscula
            //  - ya se presionó OK al menos una vez (_formUnlocked)
            bool enableForm = ready && _formUnlocked;

            MidCol.IsEnabled = enableForm;
            RightCol.IsEnabled = enableForm;

            UiBlocker.Visibility = enableForm ? Visibility.Collapsed : Visibility.Visible;
            UiBlocker.IsHitTestVisible = !enableForm;

            if (status != null)
                lblTemp.Content = status;
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
            //ProductoRegCombo.ItemsSource = _productOptions;
            //ProductoRegCombo.DisplayMemberPath = nameof(ProductInfo.Name);
            //ProductoRegCombo.SelectedIndex = -1;
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

                _productOptions.Clear();

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
                    _productOptions.Add(p);

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

            public string AccountNumber { get; init; } = "";
            public string AccountName { get; init; } = "";
            public string AccountAddress { get; init; } = "";
            public string AccountCountry { get; init; } = "";
            public string AccountState { get; init; } = "";
            public int? DriverProductId { get; init; }

            public string ProductDescription { get; init; } = "";

            public string License { get; init; } = "";
            public string LicenseStateCode { get; init; } = "";

            public string PhoneDigits { get; init; } = "";

            public string Plates { get; init; } = "";
            public string TrailerNumber { get; init; } = "";
            public string TractorNumber { get; init; } = "";
        }

        private async Task<long> InsertDriverInfoAsync(
        string? saleUid,
        string? accountNumber,
        string accountName,
        string accountAddress,
        string accountCountry,
        string accountState,
        string firstName,
        string lastName,
        string driverPhone,
        string licenseNo,
        string licenseState,
        string plates,
        string trailerNumber,
        string tractorNumber,
        int? driverProductId,      
        string identifyBy,
        string matchKey)
        {
            string connStr = GetConnectionString();

            const string SQL = @"
            INSERT INTO sale_driver_info
                (sale_uid,
                 account_number, account_name, account_address, account_country, account_state,
                 driver_first_name, driver_last_name, driver_phone,
                 license_number, license_state,
                 vehicle_plates,
                 trailer_number, tractor_number, driver_product_id,
                 identify_by, match_key, created_at)
            VALUES
                (@sale_uid,
                 @acc_no, @acc_name, @acc_addr, @acc_country, @acc_state,
                 @first, @last, @phone,
                 @lic, @lic_state,
                 @plates,
                 @trailer, @tractor, @product_id,
                 @idby, @mkey, NOW())
            ON DUPLICATE KEY UPDATE
                sale_uid          = COALESCE(sale_driver_info.sale_uid, VALUES(sale_uid)),
                account_number    = IF(sale_driver_info.sale_uid IS NULL, VALUES(account_number),   sale_driver_info.account_number),
                account_name      = IF(sale_driver_info.sale_uid IS NULL, VALUES(account_name),     sale_driver_info.account_name),
                account_address   = IF(sale_driver_info.sale_uid IS NULL, VALUES(account_address),  sale_driver_info.account_address),
                account_country   = IF(sale_driver_info.sale_uid IS NULL, VALUES(account_country),  sale_driver_info.account_country),
                account_state     = IF(sale_driver_info.sale_uid IS NULL, VALUES(account_state),    sale_driver_info.account_state),
                driver_first_name = IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_first_name),sale_driver_info.driver_first_name),
                driver_last_name  = IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_last_name), sale_driver_info.driver_last_name),
                driver_phone      = IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_phone),     sale_driver_info.driver_phone),
                license_number    = IF(sale_driver_info.sale_uid IS NULL, VALUES(license_number),   sale_driver_info.license_number),
                license_state     = IF(sale_driver_info.sale_uid IS NULL, VALUES(license_state),    sale_driver_info.license_state),
                vehicle_plates    = IF(sale_driver_info.sale_uid IS NULL, VALUES(vehicle_plates),   sale_driver_info.vehicle_plates),
                trailer_number    = IF(sale_driver_info.sale_uid IS NULL, VALUES(trailer_number),   sale_driver_info.trailer_number),
                tractor_number    = IF(sale_driver_info.sale_uid IS NULL, VALUES(tractor_number),   sale_driver_info.tractor_number),
                driver_product_id =
                    IF(sale_driver_info.sale_uid IS NULL, VALUES(driver_product_id), sale_driver_info.driver_product_id)";


            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = new MySqlCommand(SQL, conn);

            cmd.Parameters.AddWithValue("@sale_uid", (object?)saleUid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@acc_no", (object?)accountNumber ?? DBNull.Value);

            cmd.Parameters.AddWithValue("@acc_name", string.IsNullOrWhiteSpace(accountName) ? (object)DBNull.Value : accountName);
            cmd.Parameters.AddWithValue("@acc_addr", string.IsNullOrWhiteSpace(accountAddress) ? (object)DBNull.Value : accountAddress);
            cmd.Parameters.AddWithValue("@acc_country", string.IsNullOrWhiteSpace(accountCountry) ? (object)DBNull.Value : accountCountry);
            cmd.Parameters.AddWithValue("@acc_state", string.IsNullOrWhiteSpace(accountState) ? (object)DBNull.Value : accountState);

            cmd.Parameters.AddWithValue("@first", firstName);
            cmd.Parameters.AddWithValue("@last", lastName);
       
            cmd.Parameters.AddWithValue("@phone",
                string.IsNullOrWhiteSpace(driverPhone) ? (object)DBNull.Value : driverPhone);
            cmd.Parameters.AddWithValue("@lic", licenseNo);
            cmd.Parameters.AddWithValue("@lic_state",
                                         string.IsNullOrWhiteSpace(licenseState) ? (object)DBNull.Value : licenseState);

            cmd.Parameters.AddWithValue("@plates", plates);

            cmd.Parameters.AddWithValue("@trailer",
                                         string.IsNullOrWhiteSpace(trailerNumber) ? (object)DBNull.Value : trailerNumber);
            cmd.Parameters.AddWithValue("@tractor",
                                         string.IsNullOrWhiteSpace(tractorNumber) ? (object)DBNull.Value : tractorNumber);
            cmd.Parameters.AddWithValue("@product_id",
                                        driverProductId.HasValue ? driverProductId.Value : (object)DBNull.Value);


            cmd.Parameters.AddWithValue("@idby", identifyBy);
            cmd.Parameters.AddWithValue("@mkey", matchKey);


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
        /*
             
                COALESCE(
                    sdi.product_description,
                    CONCAT(dp.NAME, ' (', dp.code, ')'),
                    ''
                ) AS product_description,
         */
        private async Task<DriverInfo?> GetDriverByWeightUuidAsync(string uuid)
        {
            const string SQL = @"
            SELECT
                sdi.id_driver_info,
                sdi.driver_first_name,
                sdi.driver_last_name,
                sdi.account_name,
                sdi.account_address,
                sdi.account_country,
                sdi.account_state,
                sdi.driver_product_id,
                dp.name AS product_description,
                sdi.license_number,
                sdi.license_state,
                sdi.vehicle_plates,
                sdi.trailer_number,
                sdi.tractor_number
            FROM sale_driver_info sdi
            LEFT JOIN driver_products dp
                ON dp.product_id = sdi.driver_product_id   -- <-- ajusta el nombre si tu PK se llama distinto
            WHERE sdi.identify_by = 'weight_uuid'
                AND sdi.match_key   = @uuid
            ORDER BY sdi.id_driver_info DESC
            LIMIT 1;";

            async Task<DriverInfo?> TryOneAsync(string connStr)
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                await using var cmd = new MySqlCommand(SQL, conn);
                cmd.Parameters.AddWithValue("@uuid", uuid);

                await using var rd = await cmd.ExecuteReaderAsync();
                if (!await rd.ReadAsync())
                    return null;

                return new DriverInfo
                {
                    Id = rd.GetInt64("id_driver_info"),
                    First = rd.GetString("driver_first_name"),
                    Last = rd.GetString("driver_last_name"),
                    AccountName = rd.IsDBNull("account_name") ? "" : rd.GetString("account_name"),
                    AccountAddress = rd.IsDBNull("account_address") ? "" : rd.GetString("account_address"),
                    AccountCountry = rd.IsDBNull("account_country") ? "" : rd.GetString("account_country"),
                    AccountState = rd.IsDBNull("account_state") ? "" : rd.GetString("account_state"),

                    DriverProductId = rd.IsDBNull("driver_product_id")
                        ? (int?)null
                        : rd.GetInt32("driver_product_id"),

                    // ahora sí alimentamos la descripción que usa la tarjeta
                    ProductDescription = rd.IsDBNull("product_description")
                        ? ""
                        : rd.GetString("product_description"),

                    License = rd.IsDBNull("license_number") ? "" : rd.GetString("license_number"),
                    LicenseStateCode = rd.IsDBNull("license_state") ? "" : rd.GetString("license_state"),
                    Plates = rd.IsDBNull("vehicle_plates") ? "" : rd.GetString("vehicle_plates"),
                    TrailerNumber = rd.IsDBNull("trailer_number") ? "" : rd.GetString("trailer_number"),
                    TractorNumber = rd.IsDBNull("tractor_number") ? "" : rd.GetString("tractor_number"),
                };
            }

            try
            {
                return await TryOneAsync(GetPrimaryConn());
            }
            catch (Exception ex)
            {
                AppendLog("[Driver] Primary DB failed, trying local… " + ex.Message);
                return await TryOneAsync(GetLocalConn());
            }
        }





        private void ShowDriverCard(DriverInfo d)
        {
            // Nombre
            DriverNameText.Text = string.IsNullOrWhiteSpace(d.Last)
                ? d.First
                : $"{d.First} {d.Last}";

            // Account
            var accLabel = string.IsNullOrWhiteSpace(d.AccountName) ? "Cash sale" : d.AccountName;
            DriverAccountText.Text = $"Account: {accLabel}";

            // Product
            var prodLabel = string.IsNullOrWhiteSpace(d.ProductDescription) ? "—" : d.ProductDescription;
            DriverProductText.Text = $"Product: {prodLabel}";

            // License + state
            if (string.IsNullOrWhiteSpace(d.License))
            {
                DriverLicenseText.Text = "License: —";
            }
            else if (!string.IsNullOrWhiteSpace(d.LicenseStateCode))
            {
                DriverLicenseText.Text = $"License: {d.License} ({d.LicenseStateCode})";
            }
            else
            {
                DriverLicenseText.Text = $"License: {d.License}";
            }

            // Plates
            DriverPlatesText.Text = string.IsNullOrWhiteSpace(d.Plates)
                ? "Plates: —"
                : $"Plates: {d.Plates}";

            // Unit = Tractor / Trailer
            if (!string.IsNullOrWhiteSpace(d.TractorNumber) || !string.IsNullOrWhiteSpace(d.TrailerNumber))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(d.TractorNumber)) parts.Add($"Tractor {d.TractorNumber}");
                if (!string.IsNullOrWhiteSpace(d.TrailerNumber)) parts.Add($"Trailer {d.TrailerNumber}");
                DriverUnitText.Text = "Unit: " + string.Join(" · ", parts);
            }
            else
            {
                DriverUnitText.Text = "Unit: —";
            }

            // Address = address + (state, country)
            if (!string.IsNullOrWhiteSpace(d.AccountAddress) ||
                !string.IsNullOrWhiteSpace(d.AccountState) ||
                !string.IsNullOrWhiteSpace(d.AccountCountry))
            {
                var addrParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(d.AccountAddress))
                    addrParts.Add(d.AccountAddress);

                var loc = new List<string>();
                if (!string.IsNullOrWhiteSpace(d.AccountState)) loc.Add(d.AccountState);
                if (!string.IsNullOrWhiteSpace(d.AccountCountry)) loc.Add(d.AccountCountry);
                if (loc.Count > 0) addrParts.Add(string.Join(", ", loc));

                DriverAccountAddressText.Text = "Address: " + string.Join(" · ", addrParts);
            }
            else
            {
                DriverAccountAddressText.Text = "Address: —";
            }

            DriverCard.Visibility = Visibility.Visible;
        }




        private void HideDriverCard()
        {
            DriverCard.Visibility = Visibility.Collapsed;

            DriverNameText.Text = "—";
            DriverAccountText.Text = "Account: —";
            DriverProductText.Text = "Product: —";
            DriverLicenseText.Text = "License: —";
            DriverPlatesText.Text = "Plates: —";
            DriverUnitText.Text = "Unit: —";
            DriverAccountAddressText.Text = "Address: —";
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


            // Al terminar de cargar métodos, dejamos TODO sin seleccionar:
            SelectPaymentByCode(null);

        }

        private void SelectPaymentByCode(string? code)
        {
            PaymentMethod? sel = null;

            foreach (var m in PaymentMethods)
            {
                var isSel = !string.IsNullOrWhiteSpace(code) &&
                            string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase);

                m.IsSelected = isSel;
                if (isSel) sel = m;
            }

            if (sel != null)
            {
                _selectedPaymentId = sel.Code;
                RefPanel.Visibility = sel.AllowReference ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                // Nada seleccionado
                _selectedPaymentId = "";
                RefPanel.Visibility = Visibility.Collapsed;
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

        private int _operatorId = 0;

        private int GetCurrentOperatorId()
        {
            if (_operatorId > 0) return _operatorId;
            if (PosSession.IsLoggedIn)
                _operatorId = PosSession.UserId;
            return _operatorId > 0 ? _operatorId : 1; // fallback por si acaso
        }


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


        private async Task<string> SaveSaleAsync()
        {
            // TODO OFFLINE:
            //  - En una fase futura, envolver este método en un try/catch
            //    y, si falla la conexión principal (GetConnectionString()),
            //    repetir los mismos INSERTs usando GetLocalConn(),
            //    igual que se hace en InsertDriverInfoWithFallbackAsync.
            //  - Por ahora sólo se guarda en la BD principal.

            if (!_driverLinked)
                throw new InvalidOperationException("Please register the driver before completing the sale.");

            if (_selectedProductId <= 0)
                throw new InvalidOperationException("No service selected. Please select a service before completing the sale.");


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

                // ================== TICKET ==================
                var ticketUid = Guid.NewGuid().ToString();
                var ticketNumber = await GenerateTicketNumberAsync(conn, (MySqlTransaction)tx);

                const string SQL_TICKET = @"INSERT INTO tickets
                     (ticket_uid, sale_uid, ticket_status_id, ticket_number, reprint_count, printed_at)
                     VALUES
                     (@tuid, @suid, @st, @tnum, 0, NOW());";

                await using (var tcmd = new MySqlCommand(SQL_TICKET, conn, (MySqlTransaction)tx))
                {
                    tcmd.Parameters.AddWithValue("@tuid", ticketUid);
                    tcmd.Parameters.AddWithValue("@suid", saleUid);
                    tcmd.Parameters.AddWithValue("@st", STATUS_TICKET_PRINTED);
                    tcmd.Parameters.AddWithValue("@tnum", ticketNumber);
                    await tcmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();

                // Guardamos datos en memoria para impresión (TicketView)
                _lastTicketUid = ticketUid;
                _lastTicketNumber = ticketNumber;

                // Moneda actual
                var currency = _selectedCurrency ?? "USD";

                // JSON para QR en memoria (para uso inmediato si lo necesitas)
                _lastTicketQrJson = BuildTicketQrPayload(
                    saleUid: saleUid,
                    ticketUid: ticketUid,
                    ticketNumber: ticketNumber,
                    total: total,
                    currency: currency,
                    date: DateTime.Now   // aquí usamos la fecha actual
                );

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
        private TaskCompletionSource<bool>? _alertDialogTcs;

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

        private async Task PrintTicketAsync(string saleUid)
        {
            try
            {
                // 1) Cargar datos DESDE BD (esto puede ir en cualquier hilo)
                var data = await LoadTicketDataAsync(saleUid);
                if (data == null)
                {
                    AppendLog($"[Print] No data found for sale_uid={saleUid}");
                    return;
                }

                // 2) Todo lo que cree visuals / use impresora VA EN EL UI THREAD (STA)
                await Dispatcher.InvokeAsync(() =>
                {
                    const double inch = 96.0;
                    double marginInch = ConfigManager.Current.TicketMarginInches > 0
                        ? ConfigManager.Current.TicketMarginInches
                        : 0.25;

                    // Media carta horizontal
                    double pageWidth = 8.5 * inch;
                    double pageHeight = 5.5 * inch;

                    if (!ConfigManager.Current.TicketLandscape)
                    {
                        // Si algún día lo quieres vertical
                        var tmp = pageWidth;
                        pageWidth = pageHeight;
                        pageHeight = tmp;
                    }

                    // Crear el visual del ticket
                    var ticketView = new Tickets.TicketView
                    {
                        DataContext = data,
                        Width = pageWidth,
                        Height = pageHeight
                    };

                    ticketView.Measure(new Size(pageWidth, pageHeight));
                    ticketView.Arrange(new Rect(new Size(pageWidth, pageHeight)));
                    ticketView.UpdateLayout();

                    // --- Selección de impresora -------------------------------
                    var server = new LocalPrintServer();
                    PrintQueue? queue = null;

                    // 1) Intentamos usar la impresora configurada en config.json
                    var configuredName = ConfigManager.Current.TicketPrinterName;
                    if (!string.IsNullOrWhiteSpace(configuredName))
                    {
                        try
                        {
                            queue = new PrintQueue(server, configuredName);
                            AppendLog($"[Print] Using configured printer '{configuredName}'.");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[Print] Could not use configured printer '{configuredName}': {ex.Message}");
                        }
                    }

                    // 2) Si no hay configurada o falló, en DESARROLLO intentamos Microsoft Print to PDF
                    if (queue == null)
                    {
                        const string devPrinter = "Microsoft Print to PDF";
                        try
                        {
                            queue = new PrintQueue(server, devPrinter);
                            AppendLog($"[Print] Using DEV printer '{devPrinter}'.");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[Print] Could not use '{devPrinter}': {ex.Message}");
                        }
                    }

                    // 3) Último recurso: impresora predeterminada del sistema
                    if (queue == null)
                    {
                        queue = LocalPrintServer.GetDefaultPrintQueue();
                        AppendLog($"[Print] Using system default printer '{queue.FullName}'.");
                    }

                    // En este punto `queue` ya NO debe ser null
                    var writer = PrintQueue.CreateXpsDocumentWriter(queue);
                    writer.Write(ticketView);

                    AppendLog($"[Print] Ticket sent to printer '{queue.FullName}' for sale_uid={saleUid}");
                });
            }
            catch (Exception ex)
            {
                AppendLog("[Print] ERROR: " + ex);
                MessageBox.Show("Error al imprimir el ticket:\n" + ex.Message,
                                "Print error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
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
            //UG es un modal que se mostraba como confirmacion
            //DriverSavedHost.IsOpen = true;
            await _drvDialogTcs.Task;
        }

        private void CloseDrvDlg_Click(object? sender, RoutedEventArgs e)
        {
            //DriverSavedHost.IsOpen = false;
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

                    // A partir de aquí se habilita el formulario (MidCol / RightCol)
                    _formUnlocked = true;
                    SetUiReady(_isConnected, "Waiting for next truck…");

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
        private async Task ShowAlertAsync(string title, string message, PackIconKind icon = PackIconKind.InformationOutline)
        {
            AlertTitle.Text = title;
            AlertMessage.Text = message;
            AlertIcon.Kind = icon;

            _alertDialogTcs = new TaskCompletionSource<bool>();

            RoutedEventHandler handler = null!;
            handler = (s, e) =>
            {
                AlertOkButton.Click -= handler;
                AlertHost.IsOpen = false;
                _alertDialogTcs.TrySetResult(true);
            };
            AlertOkButton.Click += handler;

            AlertHost.IsOpen = true;
            await _alertDialogTcs.Task;
        }
        private async Task LoadAccountsAsync()
        {
            _accounts.Clear();

            var connStr = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connStr))
            {
                AppendLog("[Accounts] Missing connection string.");
                return;
            }

            try
            {
                using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                const string SQL = @"SELECT 
                                    id_customer,
                                    account_number,
                                    account_name,
                                    account_address,
                                    account_country,
                                    account_state
                                FROM customers
                                ORDER BY account_name;";

                using var cmd = new MySqlCommand(SQL, conn);
                using var rd = await cmd.ExecuteReaderAsync();

                var count = 0;
                while (await rd.ReadAsync())
                {
                    var acc = new TransportAccount
                    {
                        IdCustomer = rd.GetInt32(rd.GetOrdinal("id_customer")),
                        AccountNumber = rd.IsDBNull(rd.GetOrdinal("account_number")) ? "" : rd.GetString(rd.GetOrdinal("account_number")),
                        AccountName = rd.IsDBNull(rd.GetOrdinal("account_name")) ? "" : rd.GetString(rd.GetOrdinal("account_name")),
                        AccountAddress = rd.IsDBNull(rd.GetOrdinal("account_address")) ? "" : rd.GetString(rd.GetOrdinal("account_address")),
                        AccountCountry = rd.IsDBNull(rd.GetOrdinal("account_country")) ? "" : rd.GetString(rd.GetOrdinal("account_country")),
                        AccountState = rd.IsDBNull(rd.GetOrdinal("account_state")) ? "" : rd.GetString(rd.GetOrdinal("account_state")),
                    };

                    _accounts.Add(acc);
                    count++;
                }

                AppendLog($"[Accounts] Loaded {count} account(s) from {conn.DataSource}.");

                // Opción CASH / sin cuenta al inicio
                _accounts.Insert(0, new TransportAccount
                {
                    IdCustomer = 0,
                    AccountNumber = "",
                    AccountName = "(Cash sale – no account)",
                    AccountAddress = "",
                    AccountCountry = "",
                    AccountState = ""
                });

                // Refrescar combo
                ClienteRegCombo.ItemsSource = null;
                ClienteRegCombo.ItemsSource = _accounts;
                ClienteRegCombo.DisplayMemberPath = nameof(TransportAccount.Display);
                ClienteRegCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                AppendLog($"[Accounts] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ClienteRegCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClienteRegCombo.SelectedItem is not TransportAccount acc)
            {
                SetAccountFieldsEditable(true);
                ClearAccountFields();
                return;
            }

            bool isCashSale = acc.IdCustomer == 0;

            if (isCashSale)
            {
                SetAccountFieldsEditable(true);
                ClearAccountFields();
            }
            else
            {
                AccountNameText.Text = acc.AccountName ?? string.Empty;
                AccountAddressText.Text = acc.AccountAddress ?? string.Empty;
                AccountCountryText.Text = acc.AccountCountry ?? string.Empty;
                AccountStateText.Text = acc.AccountState ?? string.Empty;

                SetAccountFieldsEditable(false);
            }
        }


        private void SetAccountFieldsEditable(bool editable)
        {
            AccountNameText.IsReadOnly = !editable;
            AccountAddressText.IsReadOnly = !editable;
            AccountCountryText.IsReadOnly = !editable;
            AccountStateText.IsReadOnly = !editable;
        }

        private void ClearAccountFields()
        {
            AccountNameText.Text = string.Empty;
            AccountAddressText.Text = string.Empty;
            AccountCountryText.Text = string.Empty;
            AccountStateText.Text = string.Empty;
        }
        private async Task LoadLicenseStatesAsync()
        {
            _licenseStates.Clear();

            var connStr = GetConnectionString();

            try
            {
                using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                const string SQL = @"
            SELECT id_state, country_code, state_code, state_name
            FROM license_states
            WHERE is_active = 1
            ORDER BY country_code, state_name;";

                using var cmd = new MySqlCommand(SQL, conn);
                using var rd = await cmd.ExecuteReaderAsync();

                while (await rd.ReadAsync())
                {
                    var st = new LicenseState
                    {
                        Id = rd.GetInt32(0),
                        CountryCode = rd.GetString(1),
                        Code = rd.GetString(2),
                        Name = rd.GetString(3)
                    };
                    _licenseStates.Add(st);
                }

                AppendLog($"[LicenseStates] Loaded {_licenseStates.Count} state(s).");
            }
            catch (Exception ex)
            {
                AppendLog($"[LicenseStates] {ex.GetType().Name}: {ex.Message}");
            }

            LicenseStateCombo.ItemsSource = _licenseStates;
            LicenseStateCombo.DisplayMemberPath = "Display";
        }
        private async Task<DriverInfo?> GetDriverByPhoneAsync(string phoneDigits)
        {
            const string SQL = @"SELECT
                sdi.id_driver_info,
                sdi.driver_first_name,
                sdi.driver_last_name,
                sdi.account_number,
                sdi.account_name,
                sdi.account_address,
                sdi.account_country,
                sdi.account_state,                
                sdi.license_number,
                sdi.license_state,
                sdi.vehicle_plates,
                sdi.trailer_number,
                sdi.tractor_number,
                sdi.driver_phone
            FROM sale_driver_info sdi
            WHERE sdi.driver_phone = @phone
            ORDER BY sdi.id_driver_info DESC
            LIMIT 1;";

            async Task<DriverInfo?> TryOneAsync(string connStr)
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand(SQL, conn);
                cmd.Parameters.AddWithValue("@phone", phoneDigits);

                await using var rd = await cmd.ExecuteReaderAsync();
                if (!await rd.ReadAsync())
                    return null;

                return new DriverInfo
                {
                    Id = rd.GetInt64("id_driver_info"),
                    First = rd.GetString("driver_first_name"),
                    Last = rd.GetString("driver_last_name"),
                    AccountNumber = rd.IsDBNull("account_number") ? "" : rd.GetString("account_number"),
                    AccountName = rd.IsDBNull("account_name") ? "" : rd.GetString("account_name"),
                    AccountAddress = rd.IsDBNull("account_address") ? "" : rd.GetString("account_address"),
                    AccountCountry = rd.IsDBNull("account_country") ? "" : rd.GetString("account_country"),
                    AccountState = rd.IsDBNull("account_state") ? "" : rd.GetString("account_state"),
                  
                    License = rd.IsDBNull("license_number") ? "" : rd.GetString("license_number"),
                    LicenseStateCode = rd.IsDBNull("license_state") ? "" : rd.GetString("license_state"),
                    Plates = rd.IsDBNull("vehicle_plates") ? "" : rd.GetString("vehicle_plates"),
                    TrailerNumber = rd.IsDBNull("trailer_number") ? "" : rd.GetString("trailer_number"),
                    TractorNumber = rd.IsDBNull("tractor_number") ? "" : rd.GetString("tractor_number"),
                    PhoneDigits = rd.IsDBNull("driver_phone") ? "" : rd.GetString("driver_phone")
                };
            }

            try
            {
                return await TryOneAsync(GetPrimaryConn());
            }
            catch (Exception ex)
            {
                AppendLog("[Driver] Phone lookup primary failed, trying local… " + ex.Message);
                return await TryOneAsync(GetLocalConn());
            }
        }

        private void FillDriverFormFromInfo(DriverInfo d)
        {
            ChoferNombreText.Text = d.First;
            ChoferApellidosText.Text = d.Last;
            LicenciaNumeroText.Text = d.License;
            PlacasRegText.Text = d.Plates;
            TrailerNumberText.Text = d.TrailerNumber;
            TractorNumberText.Text = d.TractorNumber;

            // License state
            if (!string.IsNullOrWhiteSpace(d.LicenseStateCode))
            {
                var st = _licenseStates
                    .FirstOrDefault(x => string.Equals(x.Code, d.LicenseStateCode, StringComparison.OrdinalIgnoreCase));
                if (st != null)
                    LicenseStateCombo.SelectedItem = st;
            }

            // Company from catálogo
            TransportAccount? acc = null;
            if (!string.IsNullOrWhiteSpace(d.AccountNumber))
            {
                acc = _accounts.FirstOrDefault(a =>
                    string.Equals(a.AccountNumber, d.AccountNumber, StringComparison.OrdinalIgnoreCase));
            }
            if (acc == null && !string.IsNullOrWhiteSpace(d.AccountName))
            {
                acc = _accounts.FirstOrDefault(a =>
                    string.Equals(a.AccountName, d.AccountName, StringComparison.OrdinalIgnoreCase));
            }

            if (acc != null)
            {
                ClienteRegCombo.SelectedItem = acc;
            }
            else
            {
                ClienteRegCombo.SelectedIndex = 0; // Cash sale – no account
                AccountNameText.Text = d.AccountName;
                AccountAddressText.Text = d.AccountAddress;
                AccountCountryText.Text = d.AccountCountry;
                AccountStateText.Text = d.AccountState;
            }
            if (d.DriverProductId.HasValue && d.DriverProductId.Value > 0)
            {
                var dp = _driverProducts.FirstOrDefault(x => x.Id == d.DriverProductId.Value);
                if (dp != null)
                    ProductoRegText.SelectedItem = dp;
            }            
        }
        private async void DriverPhoneText_LostFocus(object sender, RoutedEventArgs e)
        {
            await ValidateAndLoadDriverByPhoneAsync();
        }
        private async void DialogBody_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Solo nos interesa si el textbox tenía el foco
            if (!DriverPhoneText.IsKeyboardFocusWithin)
                return;

            // Si el click fue dentro del propio textbox, no hacemos nada extra
            if (DriverPhoneText.IsMouseOver)
                return;

            await ValidateAndLoadDriverByPhoneAsync();
        }

        private async Task ValidateAndLoadDriverByPhoneAsync()
        {
            var digits = OnlyDigits(DriverPhoneText.Text);
            if (digits.Length != DRIVER_PHONE_LEN)
            {
                // No buscamos si no trae 10 dígitos
                DriverPhoneStatusText.Text = "Phone must be 10 digits.";
                DriverPhoneStatusText.Visibility = Visibility.Visible;
                return;
            }

            _driverPhoneDigits = digits;
            // Formato visual
            DriverPhoneText.Text = FormatPhone10(digits);

            var info = await GetDriverByPhoneAsync(digits);
            if (info == null)
            {
                DriverPhoneStatusText.Text = "Driver not found";
                DriverPhoneStatusText.Visibility = Visibility.Visible;

                // Limpia campos básicos para captura manual
                ChoferNombreText.Text = "";
                ChoferApellidosText.Text = "";
                LicenciaNumeroText.Text = "";
                PlacasRegText.Text = "";
            }
            else
            {
                FillDriverFormFromInfo(info);
                DriverPhoneStatusText.Text = $"Driver loaded: {info.First} {info.Last}";
                DriverPhoneStatusText.Visibility = Visibility.Visible;
            }
        }

        private async Task LoadDriverProductsAsync()
        {
            _driverProducts.Clear();

            async Task<bool> TryOneAsync(string connStr)
            {
                try
                {
                    using var conn = new MySqlConnection(connStr);
                    await conn.OpenAsync();

                    const string SQL = @"SELECT product_id, code, name FROM driver_products WHERE is_active = 1 ORDER BY name;";

                    using var cmd = new MySqlCommand(SQL, conn);
                    using var rd = await cmd.ExecuteReaderAsync();

                    while (await rd.ReadAsync())
                    {
                        _driverProducts.Add(new DriverProduct
                        {
                            Id = rd.GetInt32("product_id"),
                            Code = rd.GetString("code"),
                            Name = rd.GetString("name")
                        });
                    }

                    AppendLog($"[DrvProducts] Loaded {_driverProducts.Count} product(s).");
                    return _driverProducts.Count > 0;
                }
                catch (Exception ex)
                {
                    AppendLog($"[DrvProducts] {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            if (!await TryOneAsync(GetPrimaryConn()))
            {
                AppendLog("[DrvProducts] Primary DB failed, trying local…");
                await TryOneAsync(GetLocalConn());
            }

            // Bind al ComboBox del modal
            ProductoRegText.ItemsSource = _driverProducts;
            ProductoRegText.DisplayMemberPath = nameof(DriverProduct.Display);
            //ProductoRegText.SelectedIndex = -1;
        }

        private async void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            await TryAutoCompleteSaleAsync();
        }
      

    private async Task<TicketData?> LoadTicketDataAsync(string saleUid)
    {
        const string SQL = @"SELECT
            s.sale_uid,
            s.total,
            s.currency,
            COALESCE(s.occurred_at, s.created_at) AS sale_date,
            sl.description AS product_description,
            p.code AS product_code,

            sdi.driver_first_name,
            sdi.driver_last_name,
            sdi.license_number,
            sdi.vehicle_plates,
            sdi.tractor_number,
            sdi.trailer_number,

            ax.eje1,
            ax.eje2,
            ax.eje3,
            ax.peso_total,

            t.ticket_number,
            t.ticket_uid
        FROM sales s
        JOIN sale_lines sl
            ON sl.sale_uid = s.sale_uid AND sl.seq = 1
        LEFT JOIN products p
            ON p.product_id = sl.product_id
        LEFT JOIN sale_driver_info sdi
            ON sdi.sale_uid = s.sale_uid
        LEFT JOIN scale_session_axles ax
            ON ax.uuid_weight = sdi.match_key
           AND sdi.identify_by = 'weight_uuid'
        LEFT JOIN tickets t
            ON t.sale_uid = s.sale_uid
        WHERE s.sale_uid = @uid
        ORDER BY t.ticket_id DESC
        LIMIT 1;";

        await using var conn = new MySqlConnector.MySqlConnection(GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = new MySqlConnector.MySqlCommand(SQL, conn);
        cmd.Parameters.AddWithValue("@uid", saleUid);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return null;

        var data = new TicketData();

        // Transaction / ticket
        data.Transaction = saleUid;

        var rawTicketNumber = SafeGetString(rd, "ticket_number");
        data.TicketNumber = string.IsNullOrWhiteSpace(rawTicketNumber)
            ? saleUid
            : rawTicketNumber;

        // Fecha segura
        var rawDate = rd.IsDBNull("sale_date")
            ? DateTime.MinValue
            : rd.GetDateTime("sale_date");

        if (rawDate.Year < 2000) // por si vino 0000-00-00 o algo raro
            rawDate = DateTime.Now;

        data.Date = rawDate;

        // Producto / fee
        data.Product = SafeGetString(rd, "product_description");
        data.WeighFee = rd.IsDBNull("total") ? 0m : rd.GetDecimal("total");

        // Driver
        var first = SafeGetString(rd, "driver_first_name");
        var last = SafeGetString(rd, "driver_last_name");
        data.DriverName = $"{first} {last}".Trim();

        data.DriverLicense = SafeGetString(rd, "license_number");
        data.Plates = SafeGetString(rd, "vehicle_plates");
        data.TractorNumber = SafeGetString(rd, "tractor_number");
        data.TrailerNumber = SafeGetString(rd, "trailer_number");

        // Pesos
        data.Scale1 = rd.IsDBNull("eje1") ? 0 : rd.GetDouble("eje1");
        data.Scale2 = rd.IsDBNull("eje2") ? 0 : rd.GetDouble("eje2");
        data.Scale3 = rd.IsDBNull("eje3") ? 0 : rd.GetDouble("eje3");
        data.TotalGross = rd.IsDBNull("peso_total") ? 0 : rd.GetDouble("peso_total");

        var prodCode = SafeGetString(rd, "product_code");
        data.IsReweigh = string.Equals(prodCode, "REWEIGH",
            StringComparison.OrdinalIgnoreCase);

        var currency = SafeGetString(rd, "currency");
        if (string.IsNullOrWhiteSpace(currency))
            currency = "USD";

        string? ticketUid = null;
        {
            int ordTicketUid = rd.GetOrdinal("ticket_uid");
            if (!rd.IsDBNull(ordTicketUid))
            {
                object value = rd.GetValue(ordTicketUid);
                ticketUid = value?.ToString();
            }
        }

            // Guardamos el ticketUid también en el modelo
            data.TicketUid = ticketUid ?? "";

        // JSON para el QR (aquí usamos el helper nuevo)
        data.QrPayload = BuildTicketQrPayload(
            saleUid: saleUid,
            ticketUid: ticketUid,
            ticketNumber: data.TicketNumber,
            total: data.WeighFee,
            currency: currency,
            date: data.Date
        );

            // Weigher = operador actual
            var opName = string.IsNullOrWhiteSpace(PosSession.FullName)
            ? PosSession.Username
            : PosSession.FullName;
        data.Weigher = opName;

        return data;
        }


        private async Task<string> GenerateTicketNumberAsync(MySqlConnection conn, MySqlTransaction tx)
        {
            // TODO: reemplazar por llamada a sp_next_sequence('TICKETS.NUMBER')
            // cuando tengas listo el SP.
            await Task.CompletedTask; // para que no se queje el compilador al ser async
            return "TS-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }


        private static string BuildTicketQrPayload(
            string saleUid,
            string? ticketUid,
            string ticketNumber,
            decimal total,
            string currency,
            DateTime date)
        {
            var payload = new QrPayload
            {
                Version = 1,
                TicketUid = string.IsNullOrWhiteSpace(ticketUid) ? saleUid : ticketUid,
                SaleUid = saleUid,
                TicketNumber = ticketNumber,
                Total = total,
                Currency = currency,
                DateTimeIso = date.ToString("yyyy-MM-ddTHH:mm:ss")
            };

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            return JsonSerializer.Serialize(payload, options);
        }

        private static string SafeGetString(IDataRecord r, string column)
        {
            int ordinal = r.GetOrdinal(column);
            if (r.IsDBNull(ordinal))
                return "";

            object value = r.GetValue(ordinal);

            // Si viene Guid, int, lo que sea, lo convertimos a string seguro
            return value?.ToString() ?? "";
        }

    }
}
