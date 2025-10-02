using System;
using System.Collections.Generic;
using System.Linq;

namespace TruckScale.Pos.Domain
{
    public sealed class AxleSessionTracker
    {
        // Eventos
        public event Action<AxleReading>? AxleCaptured;
        public event Action<AxleSession>? SessionCompleted;
        public event Action<AxleSession>? SessionStarted;

        // Parámetros (en lb / ms)
        private readonly double _emptyThresholdLb;   // por debajo = vacío (p.ej. 300–600 lb)
        private readonly double _minAxleLb;          // mínimo para considerar eje (p.ej. 1500 lb)
        private readonly double _stabilityDeltaLb;   // cuánta variación permitida en ventana (p.ej. 80 lb)
        private readonly int _stabilityWindowMs;     // tamaño ventana (p.ej. 500 ms)
        private readonly int _minDwellMs;            // mínimo tiempo “encima” para eje (p.ej. 250 ms)
        private readonly int _gapBelowEmptyMs;       // tiempo por debajo de vacío para “cerrar” un eje (p.ej. 350 ms)
        private readonly int _sessionIdleTimeoutMs;  // si no hay nada, cerrar sesión (p.ej. 30_000)
        private readonly int _maxAxles;

        // Estado
        private AxleSession? _current;
        private readonly LinkedList<(DateTime ts, double lb)> _window = new();
        private DateTime _lastAboveEmpty = DateTime.MinValue;
        private DateTime _lastBelowEmpty = DateTime.MinValue;
        private DateTime _lastActivity = DateTime.MinValue;
        private bool _stabilizing = false;
        private int _axleIndex = 0;

        public AxleSessionTracker(
            double emptyThresholdLb = 500,
            double minAxleLb = 1500,
            double stabilityDeltaLb = 80,
            int stabilityWindowMs = 500,
            int minDwellMs = 250,
            int gapBelowEmptyMs = 350,
            int sessionIdleTimeoutMs = 30_000,
            int maxAxles = 10)
        {
            _emptyThresholdLb = emptyThresholdLb;
            _minAxleLb = minAxleLb;
            _stabilityDeltaLb = stabilityDeltaLb;
            _stabilityWindowMs = stabilityWindowMs;
            _minDwellMs = minDwellMs;
            _gapBelowEmptyMs = gapBelowEmptyMs;
            _sessionIdleTimeoutMs = sessionIdleTimeoutMs;
            _maxAxles = maxAxles;
        }

        public AxleSession? CurrentSession => _current;

        public void Reset()
        {
            _current = null;
            _window.Clear();
            _lastAboveEmpty = _lastBelowEmpty = _lastActivity = DateTime.MinValue;
            _stabilizing = false;
            _axleIndex = 0;
        }

        public void FeedSample(double weightLb, DateTime utc)
        {
            _lastActivity = utc;

            // Mantenemos la ventana deslizante
            _window.AddLast((utc, weightLb));
            var cutoff = utc.AddMilliseconds(-_stabilityWindowMs);
            while (_window.First is not null && _window.First!.Value.ts < cutoff)
                _window.RemoveFirst();

            var min = _window.Min(n => n.lb);
            var max = _window.Max(n => n.lb);
            var span = max - min;

            bool isAboveEmpty = weightLb >= _emptyThresholdLb;
            if (isAboveEmpty) _lastAboveEmpty = utc; else _lastBelowEmpty = utc;

            // Abrir sesión al pasar de vacío a cargado
            if (_current == null && isAboveEmpty)
            {
                _current = new AxleSession { UtcStart = utc };
                _axleIndex = 0;
                SessionStarted?.Invoke(_current);
            }

            if (_current == null)
                return; // seguimos esperando

            // Si nos quedamos inactivos mucho rato, cerramos sesión
            if ((utc - _current.UtcStart).TotalMilliseconds > _sessionIdleTimeoutMs &&
                (utc - _lastAboveEmpty).TotalMilliseconds > _gapBelowEmptyMs)
            {
                CloseSession(utc);
                return;
            }

            // Estabilización cuando estamos por encima de vacío
            if (isAboveEmpty)
            {
                // ¿El peso lleva “plano” un rato?
                if (span <= _stabilityDeltaLb &&
                    (_window.Last!.Value.ts - _window.First!.Value.ts).TotalMilliseconds >= _minDwellMs)
                {
                    _stabilizing = true;
                }
            }

            // Captura: cuando veníamos estabilizando y volvemos a vacío lo suficiente
            bool gapLongEnough = (utc - _lastBelowEmpty).TotalMilliseconds >= _gapBelowEmptyMs;
            if (_stabilizing && !isAboveEmpty && gapLongEnough)
            {
                // Tomamos un valor representativo de la ventana (mediana para robustez)
                var sample = Median(_window.Select(n => n.lb));
                if (sample >= _minAxleLb)
                {
                    var axle = new AxleReading
                    {
                        Index = ++_axleIndex,
                        UtcTime = utc,
                        WeightLb = sample
                    };
                    _current.Axles.Add(axle);
                    _current.TotalLb += sample;
                    AxleCaptured?.Invoke(axle);
                }
                _stabilizing = false;

                // ¿terminamos?
                if (_current.Axles.Count >= _maxAxles)
                {
                    CloseSession(utc);
                    return;
                }
            }

            // Si estamos por debajo de vacío mucho rato y no hubo más ejes, cerrar
            if (!isAboveEmpty &&
                _current.Axles.Count > 0 &&
                (utc - _lastAboveEmpty).TotalMilliseconds >= _gapBelowEmptyMs * 2)
            {
                CloseSession(utc);
            }
        }

        private void CloseSession(DateTime utc)
        {
            if (_current == null) return;
            _current.UtcEnd = utc;
            SessionCompleted?.Invoke(_current);
            // no reseteamos de inmediato para permitir consultar CurrentSession
            // el próximo “arranque de carga” abrirá una nueva
            _window.Clear();
            _stabilizing = false;
            _axleIndex = 0;
            _current = null;
        }

        private static double Median(IEnumerable<double> seq)
        {
            var arr = seq.ToArray();
            if (arr.Length == 0) return 0;
            Array.Sort(arr);
            int mid = arr.Length / 2;
            return (arr.Length % 2 == 0) ? (arr[mid - 1] + arr[mid]) / 2.0 : arr[mid];
        }
    }
}
