using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StreamDesk.Common.Network;

namespace StreamDesk.Common.Input;

/// <summary>
/// Simulador de periféricos Win32 de ultra-baja latencia para control KVM remoto sobre UDP.
/// Consume 0.0% de CPU cuando no hay eventos de entrada.
/// </summary>
public class Win32InputSimulator : IDisposable
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private readonly UdpClient _udpClient;
    private readonly Thread _listenerThread;
    private volatile bool _isRunning;
    private bool _disposed;

    public event Action<IPEndPoint>? OnControllerConnected;
    public event Action<ControlPacketType>? OnPacketReceived;

    public Win32InputSimulator(int controlPort = 5005)
    {
        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, controlPort));

        _isRunning = true;
        _listenerThread = new Thread(ListenLoop)
        {
            Name = "KVM Remote Input Thread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _listenerThread.Start();
    }

    private void ListenLoop()
    {
        IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (_isRunning && !_disposed)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remoteEp);
                if (data.Length >= Marshal.SizeOf<ControlPacket>())
                {
                    var packet = ControlPacket.Deserialize(data);
                    OnControllerConnected?.Invoke(remoteEp);
                    OnPacketReceived?.Invoke(packet.PacketType);
                    ProcessControlPacket(packet);
                }
            }
            catch (SocketException)
            {
                if (!_isRunning) break;
            }
            catch (Exception ex)
            {
                if (!_isRunning) break;
                Console.WriteLine($"[KVM Input] Error: {ex.Message}");
            }
        }
    }

    private void ProcessControlPacket(ControlPacket packet)
    {
        var inputs = new INPUT[1];

        switch (packet.PacketType)
        {
            case ControlPacketType.MouseMove:
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dx = packet.NormalizedX;
                inputs[0].u.mi.dy = packet.NormalizedY;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.MouseDown:
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dx = packet.NormalizedX;
                inputs[0].u.mi.dy = packet.NormalizedY;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_ABSOLUTE | GetMouseDownFlag((MouseButtonType)packet.ButtonOrFlags);
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.MouseUp:
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dx = packet.NormalizedX;
                inputs[0].u.mi.dy = packet.NormalizedY;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_ABSOLUTE | GetMouseUpFlag((MouseButtonType)packet.ButtonOrFlags);
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.MouseWheel:
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.mouseData = (uint)packet.WheelDelta;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.KeyDown:
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = packet.KeyCode;
                inputs[0].u.ki.dwFlags = KEYEVENTF_KEYDOWN;
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.KeyUp:
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = packet.KeyCode;
                inputs[0].u.ki.dwFlags = KEYEVENTF_KEYUP;
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;
        }
    }

    private static uint GetMouseDownFlag(MouseButtonType button) => button switch
    {
        MouseButtonType.Left => MOUSEEVENTF_LEFTDOWN,
        MouseButtonType.Right => MOUSEEVENTF_RIGHTDOWN,
        MouseButtonType.Middle => MOUSEEVENTF_MIDDLEDOWN,
        _ => 0
    };

    private static uint GetMouseUpFlag(MouseButtonType button) => button switch
    {
        MouseButtonType.Left => MOUSEEVENTF_LEFTUP,
        MouseButtonType.Right => MOUSEEVENTF_RIGHTUP,
        MouseButtonType.Middle => MOUSEEVENTF_MIDDLEUP,
        _ => 0
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isRunning = false;
        try { _udpClient.Close(); } catch { }
        try { _udpClient.Dispose(); } catch { }
    }
}
