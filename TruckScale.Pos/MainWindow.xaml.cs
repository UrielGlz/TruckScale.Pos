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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        public string Metodo { get; set; } = "";
        public decimal Monto { get; set; }
    }

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

        //readonly ObservableCollection<PaymentRow> _pagos = new();
        public ObservableCollection<PaymentMethod> PaymentMethods { get; } = new();

        // DB / estado (usa tu clase nueva Domain/WeightLogger.cs)
        private WeightLogger? _logger;


        // Estado de sesión mostrada en UI
        private int _axleCount = 0;
        private double _sessionTotalLb = 0;
        private bool _sessionActive = false;
        private readonly ObservableCollection<string> _recentSessions = new();
        private readonly ObservableCollection<string> _currentAxles = new();

        // === Serial simple al estilo "ScaleTesting" ===
        private SerialPort? _port;


        // Últimos valores por canal (0,1,2 = ejes; 3 = total)
        readonly Dictionary<int, double> _last = new() { { 0, 0 }, { 1, 0 }, { 2, 0 }, { 3, 0 } };
        readonly Dictionary<int, string> _lastTail = new() { { 0, "" }, { 1, "" }, { 2, "" }, { 3, "" } };

        public MainWindow()
        {
            InitializeComponent();

            ApplyTheme();
            ApplyBrand();

            // UI básica
            ApplyUi();

            LoadKeypadConfig();
            BuildKeypadUI();
            //UG lo movio a el boton de start
            //StartReader("COM2"); //Yo setie el puerto manualmente en produccion
            SetUiReady(false, "Waiting");   // bloqueado hasta que presionen Start


        }

        //******************** Pudin *******************************//
        /* Todo el codigo lo puse entre estos comentarios*/
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
                    NewLine = "\r",      // <-- CR (igual que en el stream real)
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
                    SetUiReady(true, "Connected");
                    lblEstado.Content = ($"Puerto {_port.PortName} abierto a 9600 8N1");
                }
                else
                {
                    SetUiReady(false, "Error");

                    lblEstado.Content = ("No se pudo abrir el puerto.");
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


        //Captura lineas del indicador y guardar en un txt
        //Esto solo lo use para generar un log y de ahi crear un simulador de bascula no lo vamos a usar en produccion
        //Hay que asegurarnos que esto no funcione porque hace lenta la captura.
        private StreamWriter _rxCapture;
        private readonly object _capLock = new();
        private readonly Stopwatch _capSw = new();
        public void StartCapture(string folder = null)
        {
            //no usar
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
                // 1) Asegura carpeta
                Directory.CreateDirectory(_dir);

                // 2) Archivo por día
                string filePath = Path.Combine(_dir, $"raw_{DateTime.Now:yyyyMMdd}.log");

                // 3) Línea con timestamp
                string toWrite = $"{DateTime.Now:HH:mm:ss.fff}\t{line}{Environment.NewLine}";

                // 4) Escritura segura (permite leer el log mientras se escribe)
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
                // Opcional: loguea el error a Debug/Console para no romper el flujo
                System.Diagnostics.Debug.WriteLine($"CaptureRaw error: {ex}");
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
                        //lblUUID.Content = $"{_last[3]:0} lbs";
                    }
                }
                //lblUUID.Content = $"{_last[3]:0} lbs";
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                //AppendLog($"Error lectura: {ex.Message}");
            }
        }

        // ===== Campos/estado de la ventana =====
        private readonly double[] _last1 = new double[4];          // 0,1,2 = ejes; 3 = total
        private readonly string[] _lastTail1 = new string[4];      // GG/GR por canal
        private double _ultimoTotalGuardado1 = 0;

        // Variables globales para BD (solo cuando estabiliza)
        private double G_Eje1, G_Eje2, G_Eje3, G_Total;
        private DateTime G_TimestampUtc;
        private bool G_Estable;

        double lbs_temp = 0;
        bool isStable = false;
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

            int ch = int.Parse(m.Groups["ch"].Value, System.Globalization.CultureInfo.InvariantCulture);
            double w = double.Parse(m.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
            string tail = m.Groups["tail"].Value.ToUpperInvariant(); // (lo ignoraremos para estabilidad)

            _last[ch] = w;
            _lastTail[ch] = tail;

            // 2) UI en “tiempo real”
            Dispatcher.InvokeAsync(() =>
            {
                if (ch == 0) lblEje1.Content = $"{_last[0]:0} lb12";
                if (ch == 1) lblEje2.Content = $"{_last[1]:0} lb";
                if (ch == 2) lblEje3.Content = $"{_last[2]:0} lb";
                if (ch == 3) WeightText.Text = $"{_last[3]:0}";
                lblUUID.Content = $"{_last[3]:0} lb · ch={ch} · tail={tail}";
            });
            string note = $"ch3={_last[3]} ,ch2={_last[2]} ,ch1={_last[1]} ,ch0={_last[0]}";
            EvaluateStabilityAndMaybePersist(_last, note);
        }

        // ChatGPT [EvaluarEstabilidadPersistente] -- Parametros que podemos usar en Settings segun ChatGPT
        // ----- Config de estabilidad -----
        const double MIN_TOTAL_LB = 100;      // mínimo para considerar lectura válida
        const double EPSILON_LB = 20;      // variación permitida en la ventana (lb)
        const int WINDOW_MS = 1200;    // ancho de ventana para evaluar estabilidad
        const int MIN_SAMPLES = 5;       // mínimo de muestras en la ventana
        const int HOLD_MS = 600;     // tiempo estable continuo antes de disparar
        const double SUM_TOL_LB = 100;     // |(e0+e1+e2) - total| tolerado
        const double SNAP_DELTA_LB = 200;     // diferencia mínima contra último total guardado

        // ----- Estado de estabilidad -----
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly Queue<(long ts, double total)> _win = new();
        private long _lastUnstableMs = 0;
        private bool _autoStable = false;
        private double _lastPersistedTotal = double.NaN;

        private static readonly Regex rx = new(@"^%(?<ch>\d)(?<w>\d+(?:\.\d+)?)lb(?<tail>[A-Za-z]{2})$",
                                               RegexOptions.Compiled);

        //private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private static double Get(IDictionary<int, double> map, int key)
        {
            double v;
            return (map != null && map.TryGetValue(key, out v)) ? v : 0d;
        }

        private readonly Queue<(long ts, double total)> _winTotals = new();
        private void EvaluateStabilityAndMaybePersist(IDictionary<int, double> axles, string note)
        {
            //ChatGPT -- [Evaluar Estabilidad Persistente]
            // axles: keys 0,1,2,3 = e1,e2,e3,total
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
                _lastUnstableMs = now;
                Dispatcher.InvokeAsync(() => lblTemp.Content = "Weight in progress");
                return;
            }

            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var s in _winTotals) { if (s.total < min) min = s.total; if (s.total > max) max = s.total; }
            double span = max - min;

            if (span > EPSILON_LB)
            {
                if (_autoStable) AppendLog($"→ pierde estabilidad (Δ={span:0.0} lb)");
                _autoStable = false;
                _lastUnstableMs = now;
                Dispatcher.InvokeAsync(() => lblTemp.Content = "Weight in progress");
                return;
            }

            if (!_autoStable && (now - _lastUnstableMs) < HOLD_MS)
                return;

            if (!_autoStable)
            {
                _autoStable = true;
                AppendLog($"→ entra estable (Δ={span:0.0} lb en {WINDOW_MS} ms)");
                // Indicador visible para el operador (antes de persistir)
                Dispatcher.InvokeAsync(() => lblTemp.Content = "STABLE WEIGHT");
            }

            double suma = e0 + e1 + e2;
            if (Math.Abs(suma - total) > SUM_TOL_LB)
            {
                AppendLog($"(Descartado) ejes {suma:0} vs total {total:0} (Δ={Math.Abs(suma - total):0} lb)");
                return;
            }

            if (!double.IsNaN(_lastPersistedTotal) &&
                Math.Abs(total - _lastPersistedTotal) < SNAP_DELTA_LB)
                return;

            string uuid = GenerateUid10();
            _lastPersistedTotal = total;

            Dispatcher.InvokeAsync(() =>
            {
                lblUUID.Content = uuid;
                //lblTemp.Content = "OK";

                lblTemp.Content = "STABLE WEIGHT";  //UG mantenemos el estado estable visible
            });

            string rawLine = note ?? $"e0={e0:0} e1={e1:0} e2={e2:0} tot={total:0}";

            _ = saveScaleData(e0, e1, e2, total, rawLine, uuid).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var ex = t.Exception?.GetBaseException()?.ToString();
                    AppendLog("[DB] ERROR: " + ex);
                    _ = _logger?.LogEventAsync("ACCEPT_EX", rawLine: rawLine, note: ex);

                    Dispatcher.InvokeAsync(() => lblTemp.Content = "DB ERROR");
                }
                else
                {
                    AppendLog($"[DB] Insertado id={t.Result} (uuid={uuid})");
                    //Dispatcher.InvokeAsync(() => lblTemp.Content = "OK"); 
                    lblTemp.Content = "Waiting";
                    lblEstado.Content = "Press Start to begin.";
                    SetUiReady(false, "Waiting");


                }
            }, TaskScheduler.Default);

            _autoStable = false;
        }

        public static string GetConnectionString()
        {
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
        //UG funcion que usa para save scale data
        public async Task<long> saveScaleData(double eje1, double eje2, double eje3, double total, string rawLine, string uuid_weight)
        {
            //MessageBox.Show(uuid_weight);
            string connStr = GetConnectionString();

            _currentWeightUuid = uuid_weight; //UG lo usamos para poder insertarlo temporalmente en  scale_session_axles

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
            //lblTemp.Content = "OK";
            await cmd.ExecuteNonQueryAsync();

            return (long)cmd.LastInsertedId; //Si oupamos saber el ultimo registro que se creo aqui lo tenemos 
            //Tambien podemos mantener el UUID en memoria
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
                    if (_seenIds.Add(id)) return id; // sólo devuelve si no se ha visto antes
            }
        }


        //***************************************************************//

        // ===== Localización / UI básica =====
        private string T(string key) => (TryFindResource(key) as string) ?? key;
        private void ApplyUi()
        {
            UpdateWeightText(0); // arranca en 0 lb
            //try { RecientesList.ItemsSource = _recentSessions; } catch { }
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
            lblTemp.Content = "Waiting";
            lblEstado.Content = "Press Start to begin.";
        }

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

                // Selección por defecto visual en los métodos dinámicos
                SelectPaymentByCode("cash");

                RefreshKeypadDisplay();
            }
            catch { }
        }
        private void PayButton_Click(object sender, RoutedEventArgs e) { SetPayment(((Button)sender).Tag?.ToString() ?? "cash"); }
        private void SetPayment(string methodId)
        {
            SelectPaymentByCode(methodId);
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

        async private void Button_Click(object sender, RoutedEventArgs e)
        {
            string uid = GenerateUid10();
            try
            {
                // opcional: deshabilita botón para evitar doble clic
                // btnSave.IsEnabled = false;


                var id = await saveScaleData(1.1, 2.0, 3.0, 6.0, "example data", uid);
                MessageBox.Show($"Guardado OK (id={id})", "TruckScale POS",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al guardar: " + ex.Message, "TruckScale POS",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // btnSave.IsEnabled = true;
            }
        }

        private void RegisterCancel_Click(object sender, RoutedEventArgs e)
        {
            try { RootDialog.IsOpen = false; } catch { }
        }

        private async void RegisterSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1) Necesitamos la llave temporal
                var uuid = await GetWeightUuidForLinkAsync();
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    MessageBox.Show("No weight captured yet. Press Start and accept a stable weight before saving driver.",
                                    "TruckScale POS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // 2) Lee campos del formulario (con tus nombres de controles)
                string first = ChoferNombreText?.Text?.Trim() ?? "";
                string last = ChoferApellidosText?.Text?.Trim() ?? "";
                string phone = ChoferTelefonoText?.Text?.Trim() ?? "";
                string licNo = LicenciaNumeroText?.Text?.Trim() ?? "";
                DateTime? exp = LicenciaVigenciaPicker?.SelectedDate;

                string plates = PlacasRegText?.Text?.Trim() ?? "";

                // obtiene el ID correctamente (int?) ya sea por SelectedValue o SelectedItem
                int? vtypeId = null;
                if (TipoUnidadCombo?.SelectedValue is int sv) vtypeId = sv;
                else if (TipoUnidadCombo?.SelectedItem is VehicleType vt) vtypeId = vt.Id;


                string brand = MarcaText?.Text?.Trim() ?? "";
                string model = ModeloText?.Text?.Trim() ?? "";
                string notes = ObsText?.Text?.Trim();

                // (Opcional) Por si quieres capturar lo visible en Cliente / Producto:
                string clientText = (ClienteRegCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "";
                string productText = ProductoRegText?.Text?.Trim() ?? "";

                // Validaciones rápidas mínimas
                if (string.IsNullOrWhiteSpace(plates) && string.IsNullOrWhiteSpace(licNo))
                {
                    MessageBox.Show("Enter at least Plates or License number.", "TruckScale POS",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 3) Inserta con identify_by/match_key = weight_uuid/uuid
                long driverId = await InsertDriverInfoWithFallbackAsync(
                    saleUid: null,//UG mandamos temporalmente un valor aqui
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

                // Trae el registro y muéstralo
                var info = await GetDriverByWeightUuidAsync(uuid);
                if (info != null)
                {
                    ShowDriverCard(info);
                    lblEstado.Content = "Driver linked to current weight.";
                }
                else
                {
                    AppendLog("[Driver] Warning: driver not found right after insert.");
                }


                RootDialog.IsOpen = false;
                MessageBox.Show("Driver saved.", "TruckScale POS",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving driver: " + ex.Message, "TruckScale POS",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private async Task<long> InsertDriverInfoWithFallbackAsync(string? saleUid, string firstName, string lastName, string phone, string licenseNo, DateTime? licenseExpiry, string plates, int? vehicleTypeId, string brand, string model, string identifyBy, string matchKey, string? notes)
        {
            try
            {
                return await InsertDriverInfoAsync(saleUid, firstName, lastName, phone, licenseNo, licenseExpiry, plates, vehicleTypeId, brand, model, identifyBy, matchKey, notes); // usa GetConnectionString()
            }
            catch (Exception ex1) //UG PEND falta esta parte
            {
                AppendLog("[Driver] Primary DB failed, trying local… " + ex1.Message);
                // local
                var localCsb = new MySqlConnectionStringBuilder(GetLocalConn());
                await using var conn = new MySqlConnection(localCsb.ConnectionString);
                await conn.OpenAsync();
                // reutiliza el mismo SQL/params de arriba aquí si quieres duplicar, o
                // llama a una versión interna que reciba conn abierta.
                // (Para mantener esto corto, puedes duplicar el bloque de comando aquí).
                throw; // si no implementas el bloque, no olvides quitar este throw
            }
        }
        private readonly ObservableCollection<PaymentEntry> _pagos = new();


        private void AddPayment(string methodId, decimal monto)
        {
            if (monto <= 0) return;
            _pagos.Add(new PaymentEntry { Metodo = PaymentName(methodId), Monto = monto });
            RefreshSummary();
        }
        private decimal _ventaTotal = 0m, _descuento = 0m, _impuestos = 0m, _comisiones = 0m;

        // === Simulación de báscula UG TODO  quitar ===
        private bool _isSimulated = false;
        private bool _simSavedOnce = false;  // ya guardamos una vez en esta sesión simulada

        private CancellationTokenSource _simCts;
        private readonly Random _rand = new Random();

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Estado inicial visible para el operador
                lblTemp.Content = "Connecting";
                lblEstado.Content = "Opening serial port…";
                // Inicia la lectura (puerto fijo por ahora; lo moveremos a config después)
                StartReader("COM2");
                SetUiReady(false, "Connecting");

            }
            catch (Exception ex)
            {
                //lblTemp.Content = "DB ERROR";  // mostramos error visible; puedes diferenciar si quieres
                //AppendLog("Weigh_Click error: " + ex);
                AppendLog($"[Serial] {ex.GetType().Name}: {ex.Message}");

                // Simulador
                StartSimulatedReader();

                // Quita overlay y habilita TODO
                SetUiReady(true, "Connected");        // ← clave
                lblEstado.Content = "Simulated mode (no device).";
                ScaleStateText.Text = "Scale: Simulated";

            }
        }



        // === Últimas lecturas (para validar TOTAL vs suma de ejes) ===
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

        //UG quien dispara el save en scale_session_axles
        private void TryAcceptStableSet()
        {
            // 1) Ventana de sincronía
            bool inWindow =
                (_tTotal - _tAx1).Duration().TotalMilliseconds <= SYNC_WINDOW_MS &&
                (_tTotal - _tAx2).Duration().TotalMilliseconds <= SYNC_WINDOW_MS &&
                (_tTotal - _tAx3).Duration().TotalMilliseconds <= SYNC_WINDOW_MS;
            if (!inWindow) return;

            // 2) Tolerancia suma vs total
            double sum = _ax1 + _ax2 + _ax3;
            if (Math.Abs(sum - _total) > 100) return;

            // 3) En simulado, guarda solo una vez por sesión
            if (_isSimulated && _simSavedOnce) return;

            // Aceptado
            //var uuid = Guid.NewGuid().ToString();
            //UG cambiamos de donde se toma el uuid
            string uuid = GenerateUid10();
            _ = saveScaleData(_ax1, _ax2, _ax3, _total, "%sim%", uuid);

            lblEstado.Content = _isSimulated ? "Stable set accepted (sim)." : "Stable set accepted.";

            if (_isSimulated)
                _simSavedOnce = true;   // ← bloquea nuevas inserciones en simulado
        }



        private void StartSimulatedReader()
        {
            _isSimulated = true;

            _simCts?.Cancel();
            _simCts = new CancellationTokenSource();
            var token = _simCts.Token;

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

        // === Simulación de báscula UG TODO  quitar hasta aqui ===


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
            lblTemp.Content = "Waiting";
            lblEstado.Content = "Press Start to begin.";
            await LoadProductsAsync();
            await LoadVehicleTypesAsync();
            PagosList.ItemsSource = _pagos; 
            await LoadPaymentMethodsAsync();   // ya implementado antes


        }
        private void WarnAndLog(string kind, string userMessage, string? detail = null, string? raw = null)
        {
            _ = (_logger?.LogEventAsync(kind, rawLine: raw, note: detail ?? userMessage)) ?? Task.CompletedTask;

            // Estamos en UI thread en Window_Loaded; si no, usa Dispatcher.BeginInvoke
            MessageBox.Show(userMessage, "TruckScale POS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool _isConnected;  // sólo conexión al puerto

        private void SetUiReady(bool ready, string status = null)
        {
            _isConnected = ready;
            MidCol.IsEnabled = ready;
            RightCol.IsEnabled = ready;

            UiBlocker.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            UiBlocker.IsHitTestVisible = !ready;

            if (status != null) lblTemp.Content = status;

            // Si Register driver debe esperar “estable”, lo puedes refinar así:
            // RegisterDriverBtn.IsEnabled = ready && _autoStable;
        }

        // Estado actual del producto seleccionado
        private int _selectedProductId;
        private string _selectedProductCode;
        private string _selectedProductName;
        private decimal _selectedProductPrice;
        private string _selectedCurrency = "USD";


        // ---- Modelo de producto (lo que realmente usas en UI) ----
        //UG, tal vez tengamos que mover esto a otro archivo
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


        private string GetPrimaryConn() => GetConnectionString(); // la que ya tienes
        private string GetLocalConn()
        {
            // TODO: llévalo a app.config / secrets
            return "Server=127.0.0.1;Port=3306;Database=truckscale;Uid=localuser;Pwd=localpass;SslMode=None;";
        }

        private async Task LoadProductsAsync()
        {
            // Intento #1: BD principal
            if (!await TryLoadProductsAsync(GetPrimaryConn()))
            {
                AppendLog("[Products] Primary DB failed, trying local…");
                // Intento #2: instancia local
                if (!await TryLoadProductsAsync(GetLocalConn()))
                {
                    AppendLog("[Products] Local DB failed. Products unavailable.");
                    // Si quieres, aquí puedes deshabilitar los toggles:
                    WeighToggle.IsEnabled = false;
                    ReweighToggle.IsEnabled = false;
                    lblEstado.Content = "Products unavailable (DB offline).";
                    return;
                }
            }

            // Habilita UI si llegaron productos
            WeighToggle.IsEnabled = _products.ContainsKey("WEIGH");
            ReweighToggle.IsEnabled = _products.ContainsKey("REWEIGH");

            // Selección por defecto
            if (WeighToggle.IsEnabled)
                WeighToggle.IsChecked = true; // dispara el handler y setea el precio
        }

        private async Task<bool> TryLoadProductsAsync(string connStr)
        {
            try
            {
                using var conn = new MySqlConnector.MySqlConnection(connStr);
                await conn.OpenAsync();

                // Pedimos solo lo necesario y solo los códigos que usas
                const string SQL = @"SELECT product_id, code, name, default_price, currency FROM products WHERE is_active = 1 AND code IN ('WEIGH','REWEIGH');";

                using var cmd = new MySqlConnector.MySqlCommand(SQL, conn);
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

        //=-------UG, tal vez tengamos que mover esto a otro archivo (arriba)


        private void SetProduct(int id, string code, string name, decimal price, string currency = "USD")
        {
            _selectedProductId = id;
            _selectedProductCode = code;
            _selectedProductName = name;
            _selectedProductPrice = price;
            _selectedCurrency = currency;

            // Refleja en el KPI de total
            TotalVentaBigText.Text = string.Format(
                _selectedCurrency == "USD" ? System.Globalization.CultureInfo.GetCultureInfo("en-US")
                                           : System.Globalization.CultureInfo.GetCultureInfo("es-MX"),
                "{0:C}", _selectedProductPrice);
        }

        private void ApplySelected(string code)
        {
            if (_products.TryGetValue(code, out var p))
            {
                // Usa el nombre de BD si quieres reflejarlo
                // (tu UI ya muestra solo "Weigh"/"Reweigh", así que es opcional)
                SetProduct(p.Id, p.Code, p.Name, p.Price, p.Currency);
            }
            else
            {
                // Sin datos → deshabilita o muestra mensaje
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
            const string SQL = @"SELECT uuid_weight FROM scale_session_axles WHERE captured_local >= CURDATE() ORDER BY id DESC LIMIT 1;";

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

        private async Task<string?> GetWeightUuidForLinkAsync()
        {
            if (!string.IsNullOrWhiteSpace(_currentWeightUuid))
                return _currentWeightUuid;

            // intenta principal → local
            var uuid = await TryGetLastWeightUuidAsync(GetPrimaryConn());
            if (string.IsNullOrWhiteSpace(uuid))
                uuid = await TryGetLastWeightUuidAsync(GetLocalConn());
            return uuid;
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

        private async Task<long> InsertDriverInfoAsync(string? saleUid,string firstName,string lastName,string phone,string licenseNo,DateTime? licenseExpiry,string plates,int? vehicleTypeId,string brand,string model,string identifyBy,string matchKey,string? notes)
        {
            // == MISMA CONEXIÓN QUE saveScaleData ==
            string connStr = GetConnectionString();

            const string SQL = @"INSERT INTO sale_driver_info (sale_uid, driver_first_name, driver_last_name, driver_phone, license_number, license_expiry,
                    vehicle_plates, vehicle_type_id, vehicle_brand, vehicle_model, identify_by, match_key, notes, created_at)
                    VALUES
                    (@sale_uid, @first, @last, @phone, @lic, @exp,@plates, @vtid, @brand, @model, @idby, @mkey, @notes, NOW())
                      ON DUPLICATE KEY UPDATE
                      sale_uid         = COALESCE(sale_driver_info.sale_uid, VALUES(sale_uid)),
                      driver_first_name= VALUES(driver_first_name),
                      driver_last_name = VALUES(driver_last_name),
                      driver_phone     = VALUES(driver_phone),
                      license_number   = VALUES(license_number),
                      license_expiry   = VALUES(license_expiry),
                      vehicle_plates   = VALUES(vehicle_plates),
                      vehicle_type_id  = VALUES(vehicle_type_id),
                      vehicle_brand    = VALUES(vehicle_brand),
                      vehicle_model    = VALUES(vehicle_model),
                      notes            = VALUES(notes);";

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
            return (long)cmd.LastInsertedId;   // igual que en saveScaleData
        }

        /* UG Asi lo vamos a usar 
         if (!string.IsNullOrWhiteSpace(_currentWeightUuid))
            await LinkDriverToSaleAsync(saleUid, _currentWeightUuid);
         */
        public async Task<int> LinkDriverToSaleAsync(string weightUuid, string saleUid)
        {
            const string SQL = @"UPDATE sale_driver_info SET sale_uid = @sale WHERE identify_by = 'weight_uuid' AND match_key = @uuid;";

            await using var conn = new MySqlConnection(GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(SQL, conn);
            cmd.Parameters.AddWithValue("@sale", saleUid);
            cmd.Parameters.AddWithValue("@uuid", weightUuid);
            return await cmd.ExecuteNonQueryAsync(); 
        }
        private async Task<DriverInfo?> GetDriverByWeightUuidAsync(string uuid)
        {
            const string SQL = @"SELECT sdi.id_driver_info,sdi.driver_first_name, sdi.driver_last_name,sdi.driver_phone, sdi.license_number, sdi.license_expiry,sdi.vehicle_plates, sdi.vehicle_type_id,sdi.vehicle_brand, sdi.vehicle_model,
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
                using var conn = new MySqlConnector.MySqlConnection(connStr);
                await conn.OpenAsync();

                const string SQL = @"SELECT vehicle_type_id, code, name  FROM vehicle_types  WHERE is_active = 1 ORDER BY vehicle_type_id";
                using var cmd = new MySqlConnector.MySqlCommand(SQL, conn);
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
                    // fallback visual para no bloquear el formulario
                    _vehicleTypes.Clear();
                    _vehicleTypes.Add(new VehicleType { Id = 1, Code = "TRACTOR", Name = "Tractor" });
                    _vehicleTypes.Add(new VehicleType { Id = 2, Code = "TORTON", Name = "Torton" });
                    _vehicleTypes.Add(new VehicleType { Id = 3, Code = "TRUCK35", Name = "Truck 3.5t" });
                    _vehicleTypes.Add(new VehicleType { Id = 4, Code = "PICKUP", Name = "Pickup" });
                }
            }

            // Enlaza al ComboBox
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
            // Acepta sólo 0–9
            e.Handled = !_rxDigits.IsMatch(e.Text);
        }

        // Controla pegado: sólo dígitos y respeta MaxLength
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

            // Longitud resultante si reemplaza la selección actual
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

            // Inserta nosotros mismos y cancelamos el pegado por defecto
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

                    const string SQL = @"SELECT method_id, code, name, is_cash, allow_reference, is_active FROM payment_methods WHERE is_active = 1 ORDER BY method_id;";

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
                            IconKind = _methodIconMap.TryGetValue(code, out var kind) ? kind : PackIconKind.CashRegister
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

            // Selección por defecto
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
                _selectedPaymentId = sel.Code; // conserva tu variable existente
                RefPanel.Visibility = sel.AllowReference ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void PaymentMethod_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PaymentMethod pm)
                SelectPaymentByCode(pm.Code);
        }



    }
}
