using System;
using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;

namespace TruckScale.Pos
{
    public partial class App : Application
    {
        private static Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool isNewInstance;

            _singleInstanceMutex = new Mutex(
                true,
                @"Global\ScaleSoftware",
                out isNewInstance
            );

            if (!isNewInstance)
            {
                MessageBox.Show(
                    "The application is already running.",
                    "Application already open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                Shutdown();
                return;
            }

            // Log de arranque: qué config se leyó, de qué ruta y a qué servidor/base apunta cada conexión
            Config.ConfigDiagnostics.LogStartup();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
                // Ignore if mutex was not owned
            }
            finally
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}