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

    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12; // ALT
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_F12 = 0x7B;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly Socket _udpSocket;
    private IPEndPoint _targetEndPoint;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private bool _isKvmActive;
    private Rectangle _windowBounds;
    private bool _disposed;

    public bool IsKvmActive => _isKvmActive;
    public event Action<bool>? OnKvmStateChanged; // true = Activo (en Notebook), false = Liberado (en PC)

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
        if (bounds.HasValue)
        {
            _windowBounds = bounds.Value;
        }

        if (_isKvmActive == active) return;

        _isKvmActive = active;

        if (_isKvmActive)
        {
            // Confinar cursor dentro de la ventana del monitor para que no se escape
            if (_windowBounds.Width > 0 && _windowBounds.Height > 0)
            {
                var rect = new RECT
                {
                    Left = _windowBounds.Left,
                    Top = _windowBounds.Top,
                    Right = _windowBounds.Right,
                    Bottom = _windowBounds.Bottom
                };
                ClipCursor(ref rect);
            }
        }
        else
        {
            // Liberar cursor al sistema operativo del host
            ClipCursor(IntPtr.Zero);
        }

        OnKvmStateChanged?.Invoke(_isKvmActive);
    }

    public void ToggleKvm(Rectangle? bounds = null)
    {
        SetKvmActive(!_isKvmActive, bounds);
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

            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vkCode = kbd.vkCode;

            // 1. Detectar atajo de escape: Ctrl + Alt presionado o F12
            bool isCtrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool isAltDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

            if (isKeyDown)
            {
                // Si presionó Ctrl + Alt, o F12
                if ((isCtrlDown && isAltDown) || vkCode == VK_F12)
                {
                    ToggleKvm();
                    return (IntPtr)1; // Consumir el atajo para no enviar ruido
                }
            }

            // 2. Si KVM está activo, traspasar tecla a la Notebook
            if (_isKvmActive)
            {
                var packet = new ControlPacket
                {
                    PacketType = isKeyDown ? ControlPacketType.KeyDown : ControlPacketType.KeyUp,
                    KeyCode = (ushort)vkCode,
                    Timestamp = (uint)Environment.TickCount
                };
                SendPacket(ref packet);

                // En modo KVM, consumimos las teclas especiales para que apliquen a la notebook y no al host
                if (vkCode == 0x5B || vkCode == 0x5C) // Windows Key
                {
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void SendMouseMove(int clientX, int clientY, int clientWidth, int clientHeight)
    {
        if (!_isKvmActive || clientWidth <= 0 || clientHeight <= 0) return;

        ushort normX = (ushort)Math.Clamp((clientX * 65535) / clientWidth, 0, 65535);
        ushort normY = (ushort)Math.Clamp((clientY * 65535) / clientHeight, 0, 65535);

        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.MouseMove,
            NormalizedX = normX,
            NormalizedY = normY,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    public void SendMouseButton(ControlPacketType type, MouseButtonType button, int clientX, int clientY, int clientWidth, int clientHeight)
    {
        if (!_isKvmActive || clientWidth <= 0 || clientHeight <= 0) return;

        ushort normX = (ushort)Math.Clamp((clientX * 65535) / clientWidth, 0, 65535);
        ushort normY = (ushort)Math.Clamp((clientY * 65535) / clientHeight, 0, 65535);

        var packet = new ControlPacket
        {
            PacketType = type,
            ButtonOrFlags = (byte)button,
            NormalizedX = normX,
            NormalizedY = normY,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    public void SendMouseWheel(int delta)
    {
        if (!_isKvmActive) return;

        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.MouseWheel,
            WheelDelta = (short)delta,
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

        ClipCursor(IntPtr.Zero);

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        try { _udpSocket.Close(); } catch { }
        try { _udpSocket.Dispose(); } catch { }
    }
}
