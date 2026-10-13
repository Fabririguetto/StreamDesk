using System.Net;
using System.Net.Sockets;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;

namespace StreamDesk.Receiver.Transport;

/// <summary>
/// Receptor UDP de alta prioridad para datagramas de video RTP H.264 con ensamblado en tiempo real.
/// </summary>
public class UdpVideoReceiver : IDisposable
{
    private readonly Socket _socket;
    private readonly RtpDepacketizer _depacketizer = new();
    private readonly Thread _receiveThread;
    private volatile bool _isRunning;
    private bool _disposed;

    public event Action<byte[]>? OnFrameAssembled;
    public event Action<IPEndPoint>? OnSenderDetected;

    public int PacketsReceived { get; private set; }
    public long BytesReceived { get; private set; }

    public UdpVideoReceiver(int port = StreamConfig.DefaultVideoPort)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 8 * 1024 * 1024, // Buffer de recepción de 8 MB
            EnableBroadcast = true // Permitir recepción de broadcast
        };
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));

        _depacketizer.OnFrameReady += frame => OnFrameAssembled?.Invoke(frame);

        _isRunning = true;
        _receiveThread = new Thread(ReceiveLoop)
        {
            Name = "UDP Video Receiver Thread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _receiveThread.Start();
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[65536];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (_isRunning && !_disposed)
        {
            try
            {
                int received = _socket.ReceiveFrom(buffer, ref remoteEp);
                if (received > 0)
                {
                    if (PacketsReceived == 0 && remoteEp is IPEndPoint senderEp)
                    {
                        OnSenderDetected?.Invoke(senderEp);
                    }
                    PacketsReceived++;
                    BytesReceived += received;
                    _depacketizer.ProcessPacket(buffer.AsSpan(0, received));
                }
            }
            catch (SocketException)
            {
                if (!_isRunning) break;
            }
            catch (Exception ex)
            {
                if (!_isRunning) break;
                Console.WriteLine($"[UDP Receiver] Error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isRunning = false;

        _socket.Close();
        _socket.Dispose();
        _depacketizer.Reset();
    }
}
