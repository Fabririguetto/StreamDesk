using System.Net;
using System.Net.Sockets;
using StreamDesk.Common.Input;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;
using StreamDesk.Common.Power;

namespace StreamDesk.Agent;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.Title = "StreamDesk Agent - Receptor de Teclado y Mouse (0% CPU)";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==================================================================");
        Console.WriteLine("    StreamDesk Agent - KVM Ultraligero para Notebook / Emisor");
        Console.WriteLine("==================================================================");
        Console.ResetColor();

        Console.WriteLine("[Rendimiento] Modo de consumo cero activo (0.0% CPU / GPU inactiva).");
        Console.WriteLine("[Hardware] Conecta la salida HDMI de esta notebook a tu capturadora.");

        // Mostrar IPs locales
        var localIps = Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .Select(ip => ip.ToString())
            .ToList();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[Red] IP(s) local(es) de esta Notebook: {(localIps.Count > 0 ? string.Join(", ", localIps) : "127.0.0.1")}");
        Console.ResetColor();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 0. Prevenir que Windows bloquee la pantalla o suspenda la notebook
        using var keepAwake = new ScreenKeepAwake();
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("[Energía] Pantalla siempre encendida: Bloqueo y suspensión de Windows desactivados.");
        Console.ResetColor();

        // 1. Iniciar simulador de periféricos Win32 (SendInput directo)
        int controlPort = StreamConfig.DefaultControlPort;
        using var simulator = new Win32InputSimulator(controlPort);
        Console.WriteLine($"[KVM] Servidor de entrada remoto escuchando en puerto UDP {controlPort}...");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[Monitores] Detectados ({simulator.Displays.Count}):");
        foreach (var disp in simulator.Displays)
        {
            Console.WriteLine($"  - Monitor {disp.Index}: {disp.Bounds.Width}x{disp.Bounds.Height} en ({disp.Bounds.Left},{disp.Bounds.Top}) {(disp.IsPrimary ? "[Principal/Notebook]" : "[Secundario/HDMI]")}");
        }
        Console.ResetColor();

        string? lastControllerIp = null;
        simulator.OnControllerConnected += ep =>
        {
            string ip = ep.Address.ToString();
            if (lastControllerIp != ip)
            {
                lastControllerIp = ip;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] [KVM] PC de control conectada ({ip}). ¡Control remoto activo!");
                Console.ResetColor();
            }
        };

        // 2. Iniciar descubrimiento automático en LAN (UDP Broadcast)
        using var discovery = new LanDiscoveryService(isSender: true, port: StreamConfig.DefaultDiscoveryPort);
        var announcement = new DiscoveryResponse
        {
            SenderName = Environment.MachineName,
            ControlPort = controlPort,
            VideoPort = 0 // El video va por cable HDMI
        };

        string? lastDiscoveredIp = null;
        _ = Task.Run(() => discovery.StartSenderListenerAsync(
            announcement,
            cts.Token,
            receiverEp =>
            {
                string ip = receiverEp.Address.ToString();
                if (lastDiscoveredIp != ip)
                {
                    lastDiscoveredIp = ip;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Discovery] PC de escritorio detectada en {ip}.");
                }
            }));

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n>>> AGENTE ACTIVO. Presiona Ctrl+C para detener. <<<\n");
        Console.ResetColor();

        // Esperar cancelación
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Console.WriteLine("\n[Apagado] Deteniendo agente KVM...");
    }
}
