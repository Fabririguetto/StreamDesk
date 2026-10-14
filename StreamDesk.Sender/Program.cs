using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;
using StreamDesk.Sender.Capture;
using StreamDesk.Sender.Encoding;
using StreamDesk.Sender.Input;
using StreamDesk.Sender.Transport;

namespace StreamDesk.Sender;

public static class Program
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CRASH NO CONTROLADO]: {e.ExceptionObject}");
            Console.ResetColor();
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[TASK CRASH]: {e.Exception}");
            Console.ResetColor();
        };

        try
        {
            await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[FATAL ERROR EN SENDER]: {ex}");
            Console.ResetColor();
            Console.WriteLine("\nPresiona cualquier tecla para continuar...");
            try { Console.ReadKey(); } catch { }
        }
    }

    private static async Task RunAsync(string[] args)
    {
        Console.Title = "StreamDesk - Notebook Screen Sender (H.264 Low-Latency)";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==================================================================");
        Console.WriteLine("       StreamDesk Sender - Transmisor de Pantalla en LAN");
        Console.WriteLine("==================================================================");
        Console.ResetColor();

        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);
        if (screenWidth <= 0) screenWidth = 1920;
        if (screenHeight <= 0) screenHeight = 1080;

        // Por defecto: resolución dinámica y nativa del monitor (1:1 pixel perfect, soporta 4K, 2K, 1080p, etc.)
        int targetWidth = screenWidth;
        int targetHeight = screenHeight;

        bool force1080p = args.Any(a => a.Equals("--1080p", StringComparison.OrdinalIgnoreCase) || a.Equals("--scale-1080p", StringComparison.OrdinalIgnoreCase));
        if (force1080p && screenWidth > 1920)
        {
            double aspect = (double)screenWidth / screenHeight;
            targetWidth = 1920;
            targetHeight = (int)Math.Round(1920.0 / aspect);
            Console.WriteLine($"[Resolución] Escala reducida a 1080p: {targetWidth}x{targetHeight}");
        }
        else
        {
            Console.WriteLine($"[Resolución] Dinámica / Nativa de pantalla: {targetWidth}x{targetHeight} (1:1 pixel perfect)");
        }

        // Asegurar dimensiones pares para codificación H.264
        if ((targetWidth % 2) != 0) targetWidth--;
        if ((targetHeight % 2) != 0) targetHeight--;

        // Soporte de FPS configurables (predeterminado: 120 FPS para máxima fluidez y mínimo input lag)
        int targetFps = 120;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i].Equals("--fps", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-fps", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedFps))
            {
                targetFps = Math.Clamp(parsedFps, 30, 240);
                break;
            }
        }

        bool captureCursor = !args.Any(a => a.Equals("--no-cursor", StringComparison.OrdinalIgnoreCase) || a.Equals("--hide-cursor", StringComparison.OrdinalIgnoreCase));

        // Bitrate óptimo según la resolución de pantalla
        int? userBitrateMbps = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i].Equals("--bitrate", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-bitrate", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedBitrate))
            {
                userBitrateMbps = Math.Clamp(parsedBitrate, 5, 120);
                break;
            }
        }

        int defaultBitrateKbps = targetWidth >= 3840 ? 45000 : (targetWidth >= 2560 ? 35000 : 25000);
        int bitrateKbps = userBitrateMbps.HasValue ? userBitrateMbps.Value * 1000 : defaultBitrateKbps;

        var config = new StreamConfig
        {
            TargetWidth = targetWidth,
            TargetHeight = targetHeight,
            TargetFps = targetFps,
            BitrateKbps = bitrateKbps,
            CaptureCursor = captureCursor
        };

        Console.WriteLine($"[Config] Resolución de streaming: {config.TargetWidth}x{config.TargetHeight} @ {config.TargetFps} FPS");
        Console.WriteLine($"[Config] Bitrate objetivo: {config.BitrateKbps / 1000} Mbps | Codec: {config.PreferredCodec} | Cursor remoto: {(config.CaptureCursor ? "Visible" : "Oculto (--no-cursor)")}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 1. Inicializar dispositivo DirectX 11 y WinRT
        Console.WriteLine("[DirectX] Inicializando ID3D11Device y aceleración por GPU...");
        var (d3d11Device, d3d11Context, winRtDevice) = Direct3D11DeviceFactory.CreateDevice();

        // 2. Inicializar Codificador Hardware H.264
        Console.WriteLine("[Encoder] Configurando Media Foundation Hardware H.264 MFT...");
        using var encoder = new MfH264HardwareEncoder(
            d3d11Device,
            d3d11Context,
            config.TargetWidth,
            config.TargetHeight,
            config.TargetFps,
            config.BitrateKbps);

        // 3. Inicializar Transporte UDP RTP de video
        using var videoSender = new UdpVideoSender();
        encoder.OnEncodedFrame += data =>
        {
            videoSender.SendFrame(data, config.MaxRtpPayloadSize);
        };

        // 4. Inicializar simulador de control KVM
        Console.WriteLine($"[KVM] Servidor de entrada remota activo en puerto UDP {StreamConfig.DefaultControlPort}...");
        using var inputSimulator = new Win32InputSimulator(StreamConfig.DefaultControlPort);

        // 5. Iniciar servicio de descubrimiento LAN UDP Broadcast
        Console.WriteLine($"[Discovery] Escuchando solicitudes en LAN (UDP Broadcast {StreamConfig.DefaultDiscoveryPort})...");
        using var discoveryService = new LanDiscoveryService(isSender: true, port: StreamConfig.DefaultDiscoveryPort);

        var senderAnnouncement = new DiscoveryResponse
        {
            SenderName = Environment.MachineName,
            VideoPort = StreamConfig.DefaultVideoPort,
            ControlPort = StreamConfig.DefaultControlPort,
            Width = config.TargetWidth,
            Height = config.TargetHeight,
            Fps = config.TargetFps,
            Codec = config.PreferredCodec
        };

        var discoveryTask = Task.Run(() => discoveryService.StartSenderListenerAsync(
            senderAnnouncement,
            cts.Token,
            receiverEp =>
            {
                Console.WriteLine($"\n[Discovery] Receptor detectado en {receiverEp.Address}. Ajustando destino de streaming.");
                videoSender.SetTarget(receiverEp.Address.ToString(), StreamConfig.DefaultVideoPort);
            }));

        var localIps = Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .Select(ip => ip.ToString())
            .ToList();
        Console.WriteLine($"[Red] IP(s) local(es) de esta Notebook: {(localIps.Count > 0 ? string.Join(", ", localIps) : "127.0.0.1")}");

        string? targetIp = args.FirstOrDefault(a => !a.StartsWith("--") && IPAddress.TryParse(a, out _))
            ?? args.FirstOrDefault(a => !a.StartsWith("--"));
        if (!string.IsNullOrEmpty(targetIp))
        {
            Console.WriteLine($"[Red] IP del Receptor especificada manualmente (Unicast directo): {targetIp}");
            videoSender.SetTarget(targetIp, StreamConfig.DefaultVideoPort);
        }
        else
        {
            Console.WriteLine($"[Red] Destino predeterminado por broadcast LAN: {StreamConfig.DefaultBroadcastSubnet}:{StreamConfig.DefaultVideoPort}");
            Console.WriteLine($"[Red] Nota: Si el receptor está en otra IP, puedes pasarla directamente: run-sender.bat <IP_RECEPTOR>");
            videoSender.SetTarget(StreamConfig.DefaultBroadcastSubnet, StreamConfig.DefaultVideoPort);
        }

        // 6. Iniciar Motor de Captura de Pantalla
        IntPtr hMonitor = MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_DEFAULTTOPRIMARY);
        ICaptureEngine captureEngine;

        try
        {
            Console.WriteLine($"[Captura] Iniciando Windows.Graphics.Capture (VRAM Zero-Copy) a {config.TargetFps} FPS...");
            var wgc = new WindowsGraphicsCaptureEngine(d3d11Device, d3d11Context, winRtDevice);
            wgc.Start(hMonitor, config.TargetWidth, config.TargetHeight, config.TargetFps, config.CaptureCursor);
            captureEngine = wgc;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Captura] Windows.Graphics.Capture activo a {config.TargetFps} FPS.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Captura] Fallback a DXGI Desktop Duplication: {ex.Message}");
            Console.ResetColor();
            var dxgi = new DxgiDuplicationCaptureEngine(d3d11Device, d3d11Context);
            dxgi.Start(hMonitor, config.TargetWidth, config.TargetHeight, config.TargetFps, config.CaptureCursor);
            captureEngine = dxgi;
        }

        long frameCount = 0;
        long lastReportTime = Stopwatch.GetTimestamp();

        captureEngine.OnFrameCaptured += (texture, timestamp) =>
        {
            frameCount++;
            encoder.EncodeTexture(texture, timestamp);
        };

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n>>> TRANSMISIÓN ACTIVA. Presiona Ctrl+C para detener. <<<\n");
        Console.ResetColor();

        // Bucle de métricas en consola
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (double)(now - lastReportTime) / Stopwatch.Frequency;
            double fps = frameCount / elapsedSec;
            double mbps = (videoSender.BytesSent * 8.0) / (1024 * 1024 * elapsedSec);

            Console.Write($"\r[Stats] FPS: {fps:F1} | Packets: {videoSender.PacketsSent} | Video TX: {mbps:F2} Mbps    ");

            frameCount = 0;
            lastReportTime = now;
        }

        Console.WriteLine("\n[Apagado] Deteniendo captura y liberando recursos...");
        captureEngine.Stop();
        captureEngine.Dispose();
        d3d11Context.Dispose();
        d3d11Device.Dispose();
        Console.WriteLine("[Apagado] Finalizado.");
    }
}
