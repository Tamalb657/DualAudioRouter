using System;
using System.IO;
using System.Windows;

namespace DualAudioRouter
{
    public partial class App : System.Windows.Application
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                File.WriteAllText(LogPath, $"AppDomain Unhandled: {args.ExceptionObject}\n");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                File.WriteAllText(LogPath, $"Dispatcher Unhandled: {args.Exception}\n");
            };

            base.OnStartup(e);
        }
    }
}
