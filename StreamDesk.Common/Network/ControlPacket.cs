using System.Runtime.InteropServices;

namespace StreamDesk.Common.Network;

public enum ControlPacketType : byte
{
    MouseMove = 0x01,
    MouseDown = 0x02,
    MouseUp = 0x03,
    MouseWheel = 0x04,
    KeyDown = 0x05,
    KeyUp = 0x06,
    SwitchDisplay = 0x07
}

public enum MouseButtonType : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

/// <summary>
/// Paquete binario ultra compacto de 16 bytes para reenvío de eventos KVM (mouse y teclado) sin sobrecarga.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ControlPacket
{
    public ControlPacketType PacketType;
    public byte ButtonOrFlags;      // MouseButtonType o modificadores
    public ushort NormalizedX;     // Coordenada X normalizada (0 a 65535)
    public ushort NormalizedY;     // Coordenada Y normalizada (0 a 65535)
    public ushort KeyCode;         // Virtual Key Code (Win32 VK_*)
    public short WheelDelta;       // Desplazamiento de rueda (+120 / -120)
    public byte TargetDisplay;     // 0 = Auto / Salida HDMI, 1 = Monitor 1, 2 = Monitor 2, 255 = Virtual Desktop
    public byte Reserved;          // Alineación a 16 bytes
    public uint Timestamp;         // Marca temporal en milisegundos

    public static unsafe byte[] Serialize(ref ControlPacket packet)
    {
        byte[] buffer = new byte[Marshal.SizeOf<ControlPacket>()];
        fixed (byte* pBuffer = buffer)
        {
            Marshal.StructureToPtr(packet, (IntPtr)pBuffer, false);
        }
        return buffer;
    }

    public static unsafe ControlPacket Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < Marshal.SizeOf<ControlPacket>())
        {
            return default;
        }

        fixed (byte* pData = data)
        {
            return Marshal.PtrToStructure<ControlPacket>((IntPtr)pData);
        }
    }
}
