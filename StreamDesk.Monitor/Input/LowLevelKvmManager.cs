using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;

namespace StreamDesk.Monitor.Input;

public class LowLevelKvmManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly Socket _udpSocket;
    private IPEndPoint _targetEndPoint;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private bool _disposed;
    private bool _isControlEnabled = true;

    public bool IsControlEnabled
    {
        get => _isControlEnabled;
        set
        {
            if (_isControlEnabled != value)
            {
                _isControlEnabled = value;
                OnKvmStateChanged?.Invoke(_isControlEnabled);
            }
        }
    }

    public bool IsKvmActive => _isControlEnabled;
    public Func<bool>? CanSendKeyboard { get; set; }
    public event Action<bool>? OnKvmStateChanged;

    /// <summary>
    /// Monitor de destino en la Notebook: 0 = Auto / Secundario HDMI, 1 = Pantalla 1, 2 = Pantalla 2, 255 = Todo el escritorio extendido
    /// </summary>
    public byte TargetDisplay { get; set; } = 0;

    public bool ToggleControl()
    {
        IsControlEnabled = !IsControlEnabled;
        return IsControlEnabled;
    }

    public LowLevelKvmManager(string targetIp, int targetPort = 5005)
    {
        if (!IPAddress.TryParse(targetIp, out var parsedIp))
        {
            parsedIp = IPAddress.Broadcast;
        }

        _targetEndPoint = new IPEndPoint(parsedIp, targetPort);
        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true
        };

        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udpSocket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch { }

        InstallKeyboardHook();
    }

    public void UpdateTargetIp(string ip, int port = 5005)
    {
        if (IPAddress.TryParse(ip, out var parsedIp))
        {
            _targetEndPoint = new IPEndPoint(parsedIp, port);
        }
    }

    public void SetKvmActive(bool active, Rectangle? bounds = null)
    {
        IsControlEnabled = active;
    }

    public void ToggleKvm(Rectangle? bounds = null)
    {
        ToggleControl();
    }

    private void InstallKeyboardHook()
    {
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            bool isKeyUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

            if (isKeyDown || isKeyUp)
            {
                IntPtr foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint pid);

                // Solo enviar teclado a la Notebook si el control KVM está habilitado y la ventana en foco pertenece a nuestra aplicación
                if (_isControlEnabled && pid == (uint)Environment.ProcessId && (CanSendKeyboard == null || CanSendKeyboard()))
                {
                    var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    uint vkCode = kbd.vkCode;

                    // F12 se reserva para conmutar el switch local de KVM
                    if (vkCode == 0x7B)
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    var packet = new ControlPacket
                    {
                        PacketType = isKeyDown ? ControlPacketType.KeyDown : ControlPacketType.KeyUp,
                        KeyCode = (ushort)vkCode,
                        Timestamp = (uint)Environment.TickCount
                    };
                    SendPacket(ref packet);

                    // Consumir la tecla Windows para que no abra el menú inicio del host mientras se controla la Notebook
                    if (vkCode == 0x5B || vkCode == 0x5C)
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void SendMouseMove(int clientX, int clientY, int clientWidth, int clientHeight)
    {
        if (!_isControlEnabled || clientWidth <= 0 || clientHeight <= 0) return;

        ushort normX = (ushort)Math.Clamp((clientX * 65535) / clientWidth, 0, 65535);
        ushort normY = (ushort)Math.Clamp((clientY * 65535) / clientHeight, 0, 65535);

        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.MouseMove,
            NormalizedX = normX,
            NormalizedY = normY,
            TargetDisplay = TargetDisplay,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    public void SendMouseButton(ControlPacketType type, MouseButtonType button, int clientX, int clientY, int clientWidth, int clientHeight)
    {
        if (!_isControlEnabled || clientWidth <= 0 || clientHeight <= 0) return;

        ushort normX = (ushort)Math.Clamp((clientX * 65535) / clientWidth, 0, 65535);
        ushort normY = (ushort)Math.Clamp((clientY * 65535) / clientHeight, 0, 65535);

        var packet = new ControlPacket
        {
            PacketType = type,
            ButtonOrFlags = (byte)button,
            NormalizedX = normX,
            NormalizedY = normY,
            TargetDisplay = TargetDisplay,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    public void SendMouseWheel(int delta)
    {
        if (!_isControlEnabled) return;

        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.MouseWheel,
            WheelDelta = (short)delta,
            TargetDisplay = TargetDisplay,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    public void SendDisplaySwitch(byte targetDisplay)
    {
        TargetDisplay = targetDisplay;
        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.SwitchDisplay,
            TargetDisplay = targetDisplay,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    private void SendPacket(ref ControlPacket packet)
    {
        try
        {
            byte[] bytes = ControlPacket.Serialize(ref packet);
            _udpSocket.SendTo(bytes, _targetEndPoint);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        try { _udpSocket.Close(); } catch { }
        try { _udpSocket.Dispose(); } catch { }
    }
}
