using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TruckScale.Pos
{
    /// <summary>
    /// Lector serie para báscula. Expone:
    ///  - event WeightUpdated(double value, string raw)  → value en TargetUnit (kg|lb)
    ///  - event ConnectionChanged(bool ok, string message)
    ///  - Connect(string port, int baud)
    ///  - Disconnect()
    ///
    /// Lee C:\TruckScale\serial.txt para Parity/StopBits/Handshake y opciones:
    ///   Brand (Cardinal), Cardinal.Mode (Continuous|Demand), RequireUnits, EnforceUnit, TargetUnit.
    /// Valida el puerto: solo acepta si llegan líneas que parecen peso (y coinciden con EnforceUnit si se exige).
    /// </summary>
    public class SerialScaleReader : IDisposable
    {
        // ==== Eventos (firmas que espera MainWindow) ====
        public event Action<double, string>? WeightUpdated;
        public event Action<bool, string>? ConnectionChanged;

        private SerialPort? _sp;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private string? _portName;

        private SerialOptions? _opts;   // opciones cargadas del TXT (se sobreescribe port/baud)
        private bool _isDemand;         // Cardinal.Mode == Demand (requiere ENQ 0x05)
        private bool _emitLb;           // si true, el evento sale en lb; si false, en kg

        // Separadores reutilizables (mejor perf que new[] cada vez)
        private static readonly char[] CRLF = { '\r', '\n' };

        // ====== REGEX: número + unidad opcional (kg|lb|g|t) ======
        private static readonly Regex RxWeight = new(
            @"(?<!\w)(?<num>[-+]?\d{1,3}(?:[ ,]\d{3})*(?:[.,]\d+)?|\d+(?:[.,]\d+)?)(?:\s*(?<u>kg|lb|g|t))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxNumBeforeUnit = new(
    @"(?is)^.*?([-+]?\d{1,6}(?:[.,]\d{1,3})?)\s*(lb|kg)\b.*$",
    RegexOptions.Compiled);

        private static string CanonicalNumber(string raw)
        {
            if (raw.Contains('.') && raw.Contains(',')) raw = raw.Replace(",", "");      // ',' como miles
            else if (raw.Contains(',') && !raw.Contains('.')) raw = raw.Replace(',', '.'); // ',' como decimal
            return raw.Replace(" ", "");
        }

        /// <summary>
        /// Parsea una línea a KG. Si enforceUnit no es vacío, exige que la línea traiga EXACTAMENTE esa unidad.
        /// </summary>
        private static bool TryParseToKg(string line, bool requireUnits, string? enforceUnit, out double kg)
        {
            kg = 0;
            if (string.IsNullOrWhiteSpace(line)) return false;

            // 1) Camino preferido: buscar número pegado a la unidad lb|kg
            var m2 = RxNumBeforeUnit.Match(line);
            if (m2.Success)
            {
                var numTxt = CanonicalNumber(m2.Groups[1].Value);
                var unit = m2.Groups[2].Value.ToLowerInvariant(); // "lb" o "kg"

                // Si se exige unidad exacta, validar
                if (!string.IsNullOrWhiteSpace(enforceUnit) &&
                    !string.Equals(unit, enforceUnit.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    return false;

                if (!double.TryParse(numTxt, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    return false;

                kg = unit == "lb" ? val / 2.20462262185 : val; // lb→kg o kg→kg
                return true;
            }

            // 2) Fallback genérico (por si algún formato trae unidad suelta o número solo)
            var m = RxWeight.Match(line);
            if (!m.Success) return false;

            var unitOpt = m.Groups["u"].Success ? m.Groups["u"].Value.ToLowerInvariant() : null;

            if (requireUnits && unitOpt is null) return false;

            if (!string.IsNullOrWhiteSpace(enforceUnit))
            {
                var eu = enforceUnit!.ToLowerInvariant();
                if (unitOpt == null || unitOpt != eu) return false;
            }

            var canon = CanonicalNumber(m.Groups["num"].Value);
            if (!double.TryParse(canon, NumberStyles.Any, CultureInfo.InvariantCulture, out var val2))
                return false;

            switch (unitOpt)
            {
                case "lb": kg = val2 / 2.20462262185; break;
                case "g": kg = val2 / 1000.0; break;
                case "t": kg = val2 * 1000.0; break;
                default: kg = val2; break; // "kg" o sin unidad (si no se exige)
            }
            return true;
        }


        private static void TrySendEnq(SerialPort sp)
        {
            try { sp.Write(new byte[] { 0x05 }, 0, 1); } catch { /* ignorar */ }
        }

        // =================== API usada por MainWindow ===================

        /// <summary>
        /// Abre el puerto indicado y valida que realmente salgan tramas de peso dentro del timeout.
        /// Lanza excepción si el puerto NO “huele” a peso (MainWindow atrapará y probará otro COM).
        /// </summary>
        public void Connect(string portName, int baud)
        {
            Disconnect(); // limpia sesión previa si la hay

            // Cargar opciones y fijar puerto/baud
            _opts = SerialOptions.LoadFromFile(@"C:\TruckScale\serial.txt");
            _opts.Mode = SerialMode.FixedPort;
            _opts.FixedPort = portName;
            _opts.Baud = baud;

            // Unidad de salida del evento
            _emitLb = string.Equals(_opts.TargetUnit, "lb", StringComparison.OrdinalIgnoreCase);

            // ¿Cardinal ENQ?
            _isDemand =
                string.Equals(_opts.Brand, "Cardinal", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_opts.CardinalMode, "Demand", StringComparison.OrdinalIgnoreCase);

            // Abrir y VERIFICAR que hay líneas que parecen peso (y unidad, si se exige)
            _sp = OpenAndVerify(_opts);
            _portName = portName;

            // Notificar conectado
            ConnectionChanged?.Invoke(true, $"Connected ({_portName})");

            // Iniciar bucle de lectura (usa las mismas opciones no-null)
            StartReadLoop(_opts);
        }

        public void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            try { _loop?.Wait(300); } catch { }

            try { _sp?.Close(); } catch { }
            try { _sp?.Dispose(); } catch { }

            _sp = null;
            _cts = null;
            _loop = null;
            _portName = null;

            ConnectionChanged?.Invoke(false, "Disconnected");
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }

        // =================== Núcleo: abrir y verificar ===================

        private SerialPort OpenAndVerify(SerialOptions o)
        {
            var sp = new SerialPort(o.FixedPort, o.Baud, o.Parity, o.DataBits, o.StopBits)
            {
                Handshake = o.Handshake,
                ReadTimeout = 200,
                WriteTimeout = 200,
                NewLine = "\r\n",
                DtrEnable = o.DtrEnable,   // ← nuevo
                RtsEnable = o.RtsEnable    // ← nuevo
            };
            sp.Open();

            int valid = 0;
            string buffer = string.Empty;
            int start = Environment.TickCount;

            if (_isDemand) TrySendEnq(sp); // Cardinal/Demand: solicita

            while (Environment.TickCount - start < o.VerifyTimeoutMs)
            {
                try
                {
                    buffer += sp.ReadExisting();

                    if (_isDemand && (Environment.TickCount - start) % 300 < 50)
                        TrySendEnq(sp);

                    if (buffer.Length == 0)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    var parts = buffer.Split(CRLF, StringSplitOptions.RemoveEmptyEntries);
                    buffer = (buffer.EndsWith('\r') || buffer.EndsWith('\n')) ? string.Empty : parts[^1];

                    foreach (var line in parts)
                    {
                        if (TryParseToKg(line, o.RequireUnits, o.EnforceUnit, out _))
                        {
                            valid++;
                            if (valid >= o.MinValidSamples)
                                return sp; // Aceptamos el puerto
                        }
                    }
                }
                catch (TimeoutException) { /* seguir */ }
            }

            // No “olió” a peso → cerrar y fallar
            try { sp.Close(); } catch { }
            try { sp.Dispose(); } catch { }
            throw new InvalidOperationException("No se detectaron tramas de peso en el puerto especificado.");
        }

        // =================== Bucle de lectura continua ===================

        private void StartReadLoop(SerialOptions o)
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _loop = Task.Run(async () =>
            {
                string buffer = string.Empty;

                while (!ct.IsCancellationRequested)
                {
                    var sp = _sp;
                    if (sp is null) break;

                    try
                    {
                        if (_isDemand) TrySendEnq(sp);  // Cardinal/Demand: solicitar

                        buffer += sp.ReadExisting();
                        if (buffer.Length == 0)
                        {
                            await Task.Delay(40, ct);
                            continue;
                        }

                        var parts = buffer.Split(CRLF, StringSplitOptions.RemoveEmptyEntries);
                        buffer = (buffer.EndsWith('\r') || buffer.EndsWith('\n')) ? string.Empty : parts[^1];

                        foreach (var line in parts)
                        {
                            if (TryParseToKg(line, o.RequireUnits, o.EnforceUnit, out var kg))
                            {
                                double outVal = _emitLb ? kg * 2.20462262185 : kg;
                                WeightUpdated?.Invoke(outVal, line);
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        try { await Task.Delay(80, ct); } catch { break; }
                    }
                }
            }, ct);
        }
    }
}
