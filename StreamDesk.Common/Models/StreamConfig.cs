namespace StreamDesk.Common.Models;

/// <summary>
/// Configuración global de transmisión de pantalla y red para StreamDesk.
/// </summary>
public class StreamConfig
{
    public const int DefaultDiscoveryPort = 45454;
    public const int DefaultVideoPort = 5004;
    public const int DefaultControlPort = 5005;
    public const string DefaultBroadcastSubnet = "192.168.0.255"; // Subred local LAN predeterminada

    public int TargetWidth { get; set; } = 1920;
    public int TargetHeight { get; set; } = 1080;
    public int TargetFps { get; set; } = 120;
    public int BitrateKbps { get; set; } = 25000; // 25 Mbps para LAN Ethernet Gigabit
    public int MaxRtpPayloadSize { get; set; } = 1400; // MTU Ethernet seguro para evitar fragmentación IP
    public bool EnableHardwareAcceleration { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
    public string PreferredCodec { get; set; } = "H264";
}
