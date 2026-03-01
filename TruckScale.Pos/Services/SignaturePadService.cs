using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Topaz;

namespace TruckScale.Pos.Services
{
    /// <summary>
    /// Wrapper tipado del SDK Topaz SigPlusNET. Instancia única durante la sesión.
    /// Requiere: SigPlusNET.dll en Libs\ y UseWindowsForms=true en el .csproj.
    /// </summary>
    public sealed class SignaturePadService : IDisposable
    {
        private SigPlusNET? _pad;
        private bool        _initialized;

        // ── Inicialización (una sola vez) ────────────────────────────────────────

        private bool EnsureInitialized()
        {
            if (_initialized) return _pad != null;
            _initialized = true;

            try
            {
                _pad = new SigPlusNET();
                _pad.SetTabletState(0);
                _pad.SetImageXSize(500);
                _pad.SetImageYSize(100);

                if (!_pad.OpenTablet(false))
                {
                    _pad.Dispose();
                    _pad = null;
                    return false;
                }

                // OpenTablet puede activar captura internamente.
                // Forzamos state=0 para que el buffer no acumule puntos
                // entre IsDeviceConnected() y StartCapture().
                _pad.SetTabletState(0);
                return true;
            }
            catch
            {
                _pad?.Dispose();
                _pad = null;
                return false;
            }
        }

        // ── API pública ──────────────────────────────────────────────────────────

        public bool IsDeviceConnected()
        {
            try
            {
                if (!EnsureInitialized()) return false;
                return _pad!.TabletConnectQuery();
            }
            catch { return false; }
        }

        public void StartCapture(string line1, string line2)
        {
            if (_pad == null) return;
            SafeClearAndActivate();
        }

        public void ClearSignature()
        {
            if (_pad == null) return;
            SafeClearAndActivate();
        }

        public bool HasSignature()
        {
            if (_pad == null) return false;
            try { return _pad.NumberOfTabletPoints() > 0; }
            catch { return false; }
        }

        public byte[] GetSignaturePng()
        {
            if (_pad == null) return Array.Empty<byte>();
            try
            {
                var bmp = _pad.GetSigImage();
                if (bmp == null) return Array.Empty<byte>();

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch { return Array.Empty<byte>(); }
        }

        public void StopCapture()
        {
            if (_pad == null) return;
            try { _pad.SetTabletState(0); } catch { }
        }

        public void Dispose()
        {
            StopCapture();
            try { _pad?.CloseTablet(); } catch { }
            _pad?.Dispose();
            _pad = null;
        }

        // ── Lógica interna ───────────────────────────────────────────────────────

        /// <summary>
        /// Detiene la captura, limpia el PointBuffer y reactiva.
        ///
        /// Por qué NO usamos ClearSigWindow(1):
        ///   Su implementación interna (lock DataLock { List[count]=...; count++; })
        ///   puede correr mientras el hilo HID escribe simultáneamente →
        ///   IndexOutOfRangeException en hilo de fondo (no catcheable).
        ///
        /// Por qué SetSigString("") es seguro:
        ///   Es una API pública diseñada para el thread de aplicación.
        ///   Internamente adquiere DataLock antes de modificar el buffer,
        ///   y establece count=0, limpiando la secuencia de puntos.
        ///   El Sleep(100) antes garantiza que el hilo HID no esté en medio
        ///   de PutPointInBuffer cuando hacemos el clear.
        /// </summary>
        private void SafeClearAndActivate()
        {
            try { _pad!.SetTabletState(0); } catch { }
            Thread.Sleep(100);
            try { _pad!.SetSigString(""); } catch { }
            try { _pad!.SetTabletState(1); } catch { }
        }
    }
}
