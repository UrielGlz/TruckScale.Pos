using System;
using System.IO;
using System.IO.Ports;
using System.Linq;

namespace TruckScale.Pos
{
    public enum SerialMode { Auto, Simulation, FixedPort }

    public class SerialOptions
    {
        // Separadores reutilizables (evita crear arrays en cada Split)
        private static readonly char[] SepCommaSemi = { ',', ';' };

        // --- Básico ---
        public bool Enabled { get; set; } = true;
        public SerialMode Mode { get; set; } = SerialMode.Auto;

        public string FixedPort { get; set; } = "";
        public string[] PreferredPorts { get; set; } = Array.Empty<string>();

        public int Baud { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Handshake Handshake { get; set; } = Handshake.None;

        public int MaxConnectAttempts { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 700;

        // --- Verificación / Anti-puerto fantasma ---
        public int VerifyTimeoutMs { get; set; } = 1500;
        public int MinValidSamples { get; set; } = 1;
        public bool RequireUnits { get; set; } = true;

        // --- Marca / Modo ---
        public string Brand { get; set; } = "Cardinal";          // "Generic" | "Cardinal"
        public string CardinalMode { get; set; } = "Continuous";  // "Continuous" | "Demand" (ENQ)

        // --- Unidad ---
        // (1) Solo aceptar si la línea trae esta unidad ("" = no exigir)
        // (2) Unidad de salida que emitirá el lector en el evento (kg|lb)
        public string EnforceUnit { get; set; } = "lb";
        public string TargetUnit { get; set; } = "lb";

        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;

        // SerialOptions.cs (nuevas props)
        public double MinValidWeightLb { get; set; } = 300;   // ignora < este peso
        public bool RequireStableFlag { get; set; } = false;  // si el frame trae bandera "estable", exigirla
        public int StableWindowMs { get; set; } = 500;        // ventana para estabilidad por software
        public double StableDeltaLb { get; set; } = 80;       // variación máxima permitida en la ventana
        public int MinAxleGapMs { get; set; } = 350;          // tiempo bajo umbral para separar ejes
        public double DropBelowLb { get; set; } = 200;        // (alternativa) caída por debajo para separar ejes
        public double TelemetryMaxHz { get; set; } = 4;       // límite de inserción a BD (Hz)




        // ====== CARGA DESDE TXT ======
        public static SerialOptions LoadFromFile(string path)
        {
            var s = new SerialOptions();

            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, DefaultTemplate());
                    return s;
                }

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//") || line.StartsWith(';'))
                        continue;

                    var idx = line.IndexOf('=');
                    if (idx < 0) continue;

                    var key = line[..idx].Trim();
                    var val = line[(idx + 1)..].Trim();

                    switch (key.ToLowerInvariant())
                    {
                        case "enabled":
                            if (bool.TryParse(val, out var en)) s.Enabled = en;
                            break;
                        case "mode":
                            if (Enum.TryParse<SerialMode>(val, true, out var m)) s.Mode = m;
                            break;
                        case "fixedport":
                            s.FixedPort = val;
                            break;
                        case "preferredports":
                            s.PreferredPorts = val
                                .Split(SepCommaSemi, StringSplitOptions.RemoveEmptyEntries)
                                .Select(static p => p.Trim())
                                .Where(static p => p.Length > 0)
                                .ToArray();
                            break;
                        case "baud":
                            if (int.TryParse(val, out var b)) s.Baud = b;
                            break;
                        case "databits":
                            if (int.TryParse(val, out var db)) s.DataBits = db;
                            break;
                        case "parity":
                            if (Enum.TryParse<Parity>(val, true, out var par)) s.Parity = par;
                            break;
                        case "stopbits":
                            if (Enum.TryParse<StopBits>(val, true, out var sb)) s.StopBits = sb;
                            break;
                        case "handshake":
                            if (Enum.TryParse<Handshake>(val, true, out var hs)) s.Handshake = hs;
                            break;
                        case "maxconnectattempts":
                            if (int.TryParse(val, out var att)) s.MaxConnectAttempts = Math.Max(1, att);
                            break;
                        case "retrydelayms":
                            if (int.TryParse(val, out var d)) s.RetryDelayMs = Math.Max(100, d);
                            break;

                        case "verifytimeoutms":
                            if (int.TryParse(val, out var vt)) s.VerifyTimeoutMs = Math.Max(300, vt);
                            break;
                        case "minvalidsamples":
                            if (int.TryParse(val, out var mv)) s.MinValidSamples = Math.Max(1, mv);
                            break;
                        case "requireunits":
                            if (bool.TryParse(val, out var ru)) s.RequireUnits = ru;
                            break;

                        case "brand":
                            if (!string.IsNullOrWhiteSpace(val)) s.Brand = val;
                            break;
                        case "cardinal.mode":
                            if (!string.IsNullOrWhiteSpace(val)) s.CardinalMode = val;
                            break;

                        case "enforceunit":
                            s.EnforceUnit = val?.Trim() ?? "";
                            break;
                        case "targetunit":
                            if (!string.IsNullOrWhiteSpace(val)) s.TargetUnit = val;
                            break;
                        case "dtr": if (bool.TryParse(val, out var dtr)) s.DtrEnable = dtr; break;
                        case "rts": if (bool.TryParse(val, out var rts)) s.RtsEnable = rts; break;
                        case "minvalidweightlb": if (double.TryParse(val, out var mvw)) s.MinValidWeightLb = Math.Max(0, mvw); break;
                        case "requirestableflag": if (bool.TryParse(val, out var rsf)) s.RequireStableFlag = rsf; break;
                        case "stablewindowms": if (int.TryParse(val, out var sw)) s.StableWindowMs = Math.Max(100, sw); break;
                        case "stabledeltalb": if (double.TryParse(val, out var sd)) s.StableDeltaLb = Math.Max(0, sd); break;
                        case "minaxlegapms": if (int.TryParse(val, out var mag)) s.MinAxleGapMs = Math.Max(100, mag); break;
                        case "dropbelowlb": if (double.TryParse(val, out var dbl)) s.DropBelowLb = Math.Max(0, dbl); break;
                        case "telemetrymaxhz": if (double.TryParse(val, out var h)) s.TelemetryMaxHz = Math.Max(0.5, h); break;

                    }
                }
            }
            catch
            {
                // Si hay error, se quedan defaults.
            }

            return s;
        }

        public static string DefaultTemplate() =>
@"Enabled=true
# --- Modo ---
Mode=FixedPort
FixedPort=COM3
PreferredPorts=COM3, COM4, COM5

# --- Parámetros del puerto ---
Baud=9600
DataBits=8
Parity=None
StopBits=One
Handshake=None
DTR=true
RTS=true

# --- Reintentos ---
MaxConnectAttempts=4
RetryDelayMs=700

# --- Verificación ---
VerifyTimeoutMs=3000
MinValidSamples=1
RequireUnits=false

# --- Marca / Modo ---
Brand=Cardinal
Cardinal.Mode=Continuous

# --- Unidades ---
EnforceUnit=
TargetUnit=lb
# --- Gates / Estabilidad ---
MinValidWeightLb=300
RequireStableFlag=false
StableWindowMs=500
StableDeltaLb=80
MinAxleGapMs=350
DropBelowLb=200
TelemetryMaxHz=4


";
    }
}
