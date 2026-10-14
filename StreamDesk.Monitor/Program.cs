using System.Net;
using StreamDesk.Monitor.UI;

namespace StreamDesk.Monitor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            MessageBox.Show($"Error fatal en StreamDesk Monitor:\n\n{e.ExceptionObject}", "StreamDesk Monitor - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.ThreadException += (s, e) =>
        {
            MessageBox.Show($"Error de UI en StreamDesk Monitor:\n\n{e.Exception}", "StreamDesk Monitor - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            ApplicationConfiguration.Initialize();

            string? defaultIp = null;
            if (args.Length > 0 && IPAddress.TryParse(args[0], out _))
            {
                defaultIp = args[0];
            }

            Application.Run(new MonitorWindow(defaultIp));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al iniciar la aplicación:\n\n{ex.Message}", "StreamDesk Monitor - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
