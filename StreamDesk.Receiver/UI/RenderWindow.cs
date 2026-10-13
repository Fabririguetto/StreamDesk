using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;
using StreamDesk.Receiver.Decoding;
using StreamDesk.Receiver.Transport;

namespace StreamDesk.Receiver.UI;

/// <summary>
/// Ventana de visualización de escritorio con soporte para redimensionamiento fluido,
/// modo Borderless Fullscreen (F11), control KVM inverso (F12) y HUD de métricas (F3).
/// </summary>
public class RenderWindow : Form
{
    private readonly D3D11Renderer _renderer;
    private readonly MfH264HardwareDecoder _decoder;
    private readonly UdpVideoReceiver _videoReceiver;
    private readonly Socket _controlSocket;
    private IPEndPoint _senderControlEndPoint;

    private bool _isBorderless;
    private FormWindowState _previousWindowState;
    private FormBorderStyle _previousBorderStyle;
    private Rectangle _previousBounds;

    private bool _kvmEnabled = true;
    private bool _showHud = true;
    private long _frameCount;
    private double _currentFps;
    private double _currentMbps;
    private long _lastMetricsTime = Stopwatch.GetTimestamp();

    public RenderWindow(string senderIp, int videoPort = 5004, int controlPort = 5005)
    {
        Text = "StreamDesk Receiver - Pantalla Remota (LAN Ethernet)";
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 24);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);

        _senderControlEndPoint = new IPEndPoint(IPAddress.Parse(senderIp), controlPort);
        _controlSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true
        };

        // 1. Inicializar Renderizador DirectX 11 (1920x1080 con hardware scaling al HWND)
        _renderer = new D3D11Renderer(Handle, 1920, 1080);
        _renderer.RenderFrame(null);

        // 2. Inicializar Decodificador Hardware H.264
        _decoder = new MfH264HardwareDecoder(_renderer.Device, _renderer.Context, 1920, 1080);
        _decoder.OnFrameDecoded += texture =>
        {
            _frameCount++;
            _renderer.RenderFrame(texture);
        };

        // 3. Inicializar Receptor UDP de Video
        _videoReceiver = new UdpVideoReceiver(videoPort);
        _videoReceiver.OnFrameAssembled += frame =>
        {
            _decoder.DecodeFrame(frame);
        };
        _videoReceiver.OnSenderDetected += senderEp =>
        {
            if (_senderControlEndPoint.Address.Equals(IPAddress.Broadcast) || _senderControlEndPoint.Address.ToString().EndsWith(".255"))
            {
                _senderControlEndPoint = new IPEndPoint(senderEp.Address, controlPort);
                Console.WriteLine($"[KVM] Emisor detectado en {senderEp.Address}. Actualizando destino de control.");
            }
        };

        // Eventos de teclado y mouse
        KeyPreview = true;
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;
        MouseMove += OnWindowMouseMove;
        MouseDown += OnWindowMouseDown;
        MouseUp += OnWindowMouseUp;
        MouseWheel += OnWindowMouseWheel;
        Resize += (s, e) => _renderer.Resize(ClientSize.Width, ClientSize.Height);

        // Timer de métricas y renderizado de respaldo
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += OnTick;
        timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (double)(now - _lastMetricsTime) / Stopwatch.Frequency;
        if (elapsed >= 1.0)
        {
            _currentFps = _frameCount / elapsed;
            _currentMbps = (_videoReceiver.BytesReceived * 8.0) / (1024 * 1024 * elapsed);
            _frameCount = 0;
            _lastMetricsTime = now;

            if (_showHud)
            {
                if (_videoReceiver.PacketsReceived == 0)
                {
                    Text = $"StreamDesk Receiver | Esperando señal en puerto {StreamConfig.DefaultVideoPort}... (Paquetes: 0) | Destino KVM: {_senderControlEndPoint.Address}";
                }
                else
                {
                    Text = $"StreamDesk Receiver | {_currentFps:F1} FPS | {_currentMbps:F2} Mbps | Paquetes: {_videoReceiver.PacketsReceived} | KVM: {(_kvmEnabled ? "ACTIVO [F12 para liberar]" : "PAUSADO")}";
                }
            }
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            ToggleBorderless();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.F12)
        {
            _kvmEnabled = !_kvmEnabled;
            Console.WriteLine($"[KVM] Control remoto {(_kvmEnabled ? "ACTIVADO" : "DESACTIVADO")}");
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.F3)
        {
            _showHud = !_showHud;
            if (!_showHud) Text = "StreamDesk Receiver";
            e.Handled = true;
            return;
        }

        if (_kvmEnabled)
        {
            SendControlPacket(ControlPacketType.KeyDown, (ushort)e.KeyValue, 0);
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_kvmEnabled && e.KeyCode != Keys.F11 && e.KeyCode != Keys.F12 && e.KeyCode != Keys.F3)
        {
            SendControlPacket(ControlPacketType.KeyUp, (ushort)e.KeyValue, 0);
            e.Handled = true;
        }
    }

    private void OnWindowMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_kvmEnabled || ClientSize.Width == 0 || ClientSize.Height == 0) return;

        ushort normX = (ushort)Math.Clamp((e.X * 65535) / ClientSize.Width, 0, 65535);
        ushort normY = (ushort)Math.Clamp((e.Y * 65535) / ClientSize.Height, 0, 65535);

        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.MouseMove,
            NormalizedX = normX,
            NormalizedY = normY,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    private void OnWindowMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_kvmEnabled) return;
        SendMouseButtonPacket(ControlPacketType.MouseDown, e);
    }

    private void OnWindowMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_kvmEnabled) return;
        SendMouseButtonPacket(ControlPacketType.MouseUp, e);
    }

    private void OnWindowMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!_kvmEnabled) return;
        var packet = new ControlPacket
        {
            PacketType = ControlPacketType.MouseWheel,
            WheelDelta = (short)e.Delta,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    private void SendMouseButtonPacket(ControlPacketType type, MouseEventArgs e)
    {
        ushort normX = (ushort)Math.Clamp((e.X * 65535) / ClientSize.Width, 0, 65535);
        ushort normY = (ushort)Math.Clamp((e.Y * 65535) / ClientSize.Height, 0, 65535);

        MouseButtonType btn = e.Button switch
        {
            MouseButtons.Left => MouseButtonType.Left,
            MouseButtons.Right => MouseButtonType.Right,
            MouseButtons.Middle => MouseButtonType.Middle,
            _ => MouseButtonType.None
        };

        var packet = new ControlPacket
        {
            PacketType = type,
            ButtonOrFlags = (byte)btn,
            NormalizedX = normX,
            NormalizedY = normY,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    private void SendControlPacket(ControlPacketType type, ushort keyCode, short wheelDelta)
    {
        var packet = new ControlPacket
        {
            PacketType = type,
            KeyCode = keyCode,
            WheelDelta = wheelDelta,
            Timestamp = (uint)Environment.TickCount
        };
        SendPacket(ref packet);
    }

    private void SendPacket(ref ControlPacket packet)
    {
        try
        {
            byte[] bytes = ControlPacket.Serialize(ref packet);
            _controlSocket.SendTo(bytes, _senderControlEndPoint);
        }
        catch
        {
            // Evitar interrupción si hay desconexión temporal
        }
    }

    private void ToggleBorderless()
    {
        if (!_isBorderless)
        {
            _previousWindowState = WindowState;
            _previousBorderStyle = FormBorderStyle;
            _previousBounds = Bounds;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _isBorderless = true;
        }
        else
        {
            FormBorderStyle = _previousBorderStyle;
            Bounds = _previousBounds;
            WindowState = _previousWindowState;
            _isBorderless = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _videoReceiver.Dispose();
            _decoder.Dispose();
            _renderer.Dispose();
            _controlSocket.Dispose();
        }
        base.Dispose(disposing);
    }
}
