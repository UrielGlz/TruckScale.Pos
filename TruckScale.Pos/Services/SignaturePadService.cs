using System;
using System.Diagnostics;
using Topaz;

namespace TruckScale.Pos.Services
{
    /// <summary>
    /// Servicio ligero para verificar disponibilidad del pad Topaz.
    ///
    /// La gestión del control (HWND, OpenTablet, captura, timers) vive ahora
    /// dentro de SignatureCaptureWindow, igual que el demo oficial WPF de Topaz.
    ///
    /// IsDeviceConnected() abre una conexión temporal, consulta y la cierra de
    /// inmediato — sin retener ninguna instancia entre llamadas.
    /// </summary>
    public sealed class SignaturePadService : IDisposable
    {
        /// <summary>
        /// Verifica si el pad Topaz está conectado.
        /// Llamar ANTES de abrir SignatureCaptureWindow (no concurrentemente con el modal).
        /// </summary>
        public bool IsDeviceConnected()
        {
            SigPlusNET? pad = null;
            try
            {
                pad = new SigPlusNET();
                pad.SetTabletModel("11");

                if (!pad.OpenTablet(false))
                {
                    Debug.WriteLine("[Sig] IsDeviceConnected — OpenTablet returned false");
                    return false;
                }

                var ok = pad.TabletConnectQuery();
                Debug.WriteLine($"[Sig] IsDeviceConnected = {ok}");
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Sig] IsDeviceConnected error: {ex.Message}");
                return false;
            }
            finally
            {
                try { pad?.CloseTablet(); } catch { }
                pad?.Dispose();
            }
        }

        public void Dispose() { }
    }
}
