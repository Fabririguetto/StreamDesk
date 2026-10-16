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
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public record DisplayInfo(int Index, string Name, RECT Bounds, bool IsPrimary);

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
    private readonly List<DisplayInfo> _displays = new();
    private volatile bool _isRunning;
    private bool _disposed;

    public IReadOnlyList<DisplayInfo> Displays => _displays;
    public byte ActiveDisplayMode { get; set; } = 2; // 1 = Laptop, 2 = HDMI, 3 = Dual
    public event Action<IPEndPoint>? OnControllerConnected;
    public event Action<ControlPacketType>? OnPacketReceived;
    public event Action<byte>? OnDisplayModeChanged;

    public Win32InputSimulator(int controlPort = 5005)
    {
        RefreshDisplays();

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

    public void RefreshDisplays()
    {
        lock (_displays)
        {
            _displays.Clear();
            int idx = 1;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    bool isPrimary = (mi.dwFlags & 1) != 0;
                    _displays.Add(new DisplayInfo(idx++, mi.szDevice, mi.rcMonitor, isPrimary));
                }
                return true;
            }, IntPtr.Zero);
        }
    }

    public (int normVx, int normVy) MapToVirtualDesktop(ushort normX, ushort normY, byte targetDisplay)
    {
        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vWidth <= 1 || vHeight <= 1)
        {
            return (normX, normY);
        }

        DisplayInfo? target = null;
        ushort finalNormX = normX;
        ushort finalNormY = normY;

        lock (_displays)
        {
            if (_displays.Count == 0)
            {
                RefreshDisplays();
            }

            if (targetDisplay == 255 || _displays.Count == 0)
            {
                return (normX, normY);
            }

            var primary = _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays[0];
            var secondary = _displays.FirstOrDefault(d => !d.IsPrimary) ?? primary;

            if (targetDisplay == 3) // Dual: Ambas pantallas lado a lado
            {
                if (normX < 32768)
                {
                    // Mitad izquierda: Pantalla 1 (Laptop)
                    target = primary;
                    finalNormX = (ushort)Math.Clamp(((long)normX * 65535) / 32767, 0, 65535);
                }
                else
                {
                    // Mitad derecha: Pantalla 2 (HDMI)
                    target = secondary;
                    finalNormX = (ushort)Math.Clamp(((long)(normX - 32768) * 65535) / 32767, 0, 65535);
                }
            }
            else if (targetDisplay == 1) // Pantalla 1 (Laptop)
            {
                target = primary;
            }
            else if (targetDisplay == 2 || targetDisplay == 0) // Pantalla 2 (HDMI) o Auto
            {
                target = secondary;
            }
            else
            {
                int idx = targetDisplay - 1;
                target = (idx >= 0 && idx < _displays.Count) ? _displays[idx] : primary;
            }
        }

        if (target == null)
        {
            return (normX, normY);
        }

        // 1. Calcular coordenadas en píxeles del monitor objetivo
        int pixelX = target.Bounds.Left + (int)((long)finalNormX * Math.Max(1, target.Bounds.Width - 1) / 65535);
        int pixelY = target.Bounds.Top + (int)((long)finalNormY * Math.Max(1, target.Bounds.Height - 1) / 65535);

        // 2. Normalizar respecto al Virtual Desktop global de Windows (0 a 65535)
        int normVx = (int)(((long)(pixelX - vLeft) * 65535) / Math.Max(1, vWidth - 1));
        int normVy = (int)(((long)(pixelY - vTop) * 65535) / Math.Max(1, vHeight - 1));

        return (Math.Clamp(normVx, 0, 65535), Math.Clamp(normVy, 0, 65535));
    }

    private IPEndPoint? _lastControllerEp;

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

                    if (_lastControllerEp == null || !_lastControllerEp.Address.Equals(remoteEp.Address))
                    {
                        _lastControllerEp = new IPEndPoint(remoteEp.Address, remoteEp.Port);
                        OnControllerConnected?.Invoke(_lastControllerEp);
                    }

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
                var (moveX, moveY) = MapToVirtualDesktop(packet.NormalizedX, packet.NormalizedY, packet.TargetDisplay);
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dx = moveX;
                inputs[0].u.mi.dy = moveY;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.MouseDown:
                var (downX, downY) = MapToVirtualDesktop(packet.NormalizedX, packet.NormalizedY, packet.TargetDisplay);
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dx = downX;
                inputs[0].u.mi.dy = downY;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | GetMouseDownFlag((MouseButtonType)packet.ButtonOrFlags);
                SendInput(1, inputs, Marshal.SizeOf<INPUT>());
                break;

            case ControlPacketType.MouseUp:
                var (upX, upY) = MapToVirtualDesktop(packet.NormalizedX, packet.NormalizedY, packet.TargetDisplay);
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dx = upX;
                inputs[0].u.mi.dy = upY;
                inputs[0].u.mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | GetMouseUpFlag((MouseButtonType)packet.ButtonOrFlags);
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

            case ControlPacketType.SwitchDisplay:
                ActiveDisplayMode = packet.TargetDisplay;
                OnDisplayModeChanged?.Invoke(packet.TargetDisplay);
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
