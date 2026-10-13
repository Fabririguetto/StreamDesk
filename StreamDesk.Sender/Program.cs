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

        var config = new StreamConfig
        {
            TargetWidth = screenWidth,
            TargetHeight = screenHeight,
            TargetFps = 60,
            BitrateKbps = 25000 // 25 Mbps para LAN Ethernet
        };

        Console.WriteLine($"[Config] Resolución nativa detectada: {config.TargetWidth}x{config.TargetHeight} @ {config.TargetFps} FPS");
        Console.WriteLine($"[Config] Bitrate objetivo: {config.BitrateKbps / 1000} Mbps | Codec: {config.PreferredCodec}");

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

        // Permite también fijar manualmente el destino si se pasa por parámetro CLI (ej. Sender.exe 192.168.0.255)
        string? targetIp = args.Length > 0 ? args[0] : null;
        if (!string.IsNullOrEmpty(targetIp))
        {
            Console.WriteLine($"[Red] IP del Receptor especificada manualmente: {targetIp}");
            videoSender.SetTarget(targetIp, StreamConfig.DefaultVideoPort);
        }
        else
        {
            Console.WriteLine($"[Red] Destino predeterminado por broadcast LAN: {StreamConfig.DefaultBroadcastSubnet}:{StreamConfig.DefaultVideoPort}");
            videoSender.SetTarget(StreamConfig.DefaultBroadcastSubnet, StreamConfig.DefaultVideoPort);
        }

        // 6. Iniciar Motor de Captura de Pantalla
        IntPtr hMonitor = MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_DEFAULTTOPRIMARY);
        ICaptureEngine captureEngine;

        try
        {
            Console.WriteLine("[Captura] Iniciando Windows.Graphics.Capture (VRAM Zero-Copy)...");
            var wgc = new WindowsGraphicsCaptureEngine(d3d11Device, d3d11Context, winRtDevice);
            wgc.Start(hMonitor, config.TargetWidth, config.TargetHeight);
            captureEngine = wgc;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[Captura] Windows.Graphics.Capture activo a 60 FPS.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Captura] Fallback a DXGI Desktop Duplication: {ex.Message}");
            Console.ResetColor();
            var dxgi = new DxgiDuplicationCaptureEngine(d3d11Device, d3d11Context);
            dxgi.Start(hMonitor, config.TargetWidth, config.TargetHeight);
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
