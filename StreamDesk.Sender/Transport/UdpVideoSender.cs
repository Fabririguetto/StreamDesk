using System.Net;
using System.Net.Sockets;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;

namespace StreamDesk.Sender.Transport;

/// <summary>
/// Emisor de video de ultra baja latencia sobre UDP utilizando RFC 6184 RTP.
/// Soporta emisión Unicast y Broadcast (ej. 192.168.0.255).
/// </summary>
public class UdpVideoSender : IDisposable
{
    private readonly Socket _socket;
    private IPEndPoint? _targetEndPoint;
    private ushort _sequenceNumber;
    private uint _timestamp;
    private readonly uint _ssrc;
    private bool _disposed;

    public int PacketsSent { get; private set; }
    public long BytesSent { get; private set; }
    public string CurrentTarget => _targetEndPoint?.ToString() ?? "Sin destino";

    public UdpVideoSender()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            SendBufferSize = 8 * 1024 * 1024,
            EnableBroadcast = true // Permitir envío a direcciones broadcast como 192.168.0.255
        };
        _ssrc = (uint)Random.Shared.Next();
    }

    public void SetTarget(string ipAddress, int port)
    {
        _targetEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
        Console.WriteLine($"\n[Red] Destino de video actualizado a: {_targetEndPoint}");
    }

    /// <summary>
    /// Envía un frame completo de video (uno o más NALUs) a la red.
    /// </summary>
    public void SendFrame(ReadOnlySpan<byte> h264Bitstream, int maxPayloadSize = 1400)
    {
        if (_targetEndPoint == null || _disposed || h264Bitstream.Length == 0) return;

        _timestamp += 90000 / 60;

        int offset = 0;
        while (offset < h264Bitstream.Length)
        {
            int naluStart = FindNextNaluStart(h264Bitstream, offset, out int startCodeLen);
            if (naluStart == -1) break;

            int naluPayloadStart = naluStart + startCodeLen;
            int nextNaluStart = FindNextNaluStart(h264Bitstream, naluPayloadStart, out _);
            int naluLength = (nextNaluStart == -1)
                ? h264Bitstream.Length - naluPayloadStart
                : nextNaluStart - naluPayloadStart;

            bool isLastNalu = (nextNaluStart == -1);

            ReadOnlySpan<byte> nalu = h264Bitstream.Slice(naluPayloadStart, naluLength);
            var rtpPackets = RtpPacket.PacketizeH264Nalu(nalu, ref _sequenceNumber, _timestamp, _ssrc, isLastNalu, maxPayloadSize);

            foreach (var packet in rtpPackets)
            {
                try
                {
                    _socket.SendTo(packet, _targetEndPoint);
                    PacketsSent++;
                    BytesSent += packet.Length;
                }
                catch (Exception)
                {
                    // Si ocurre un error puntual de socket en broadcast, no interrumpir el loop
                }
            }

            offset = naluPayloadStart + naluLength;
        }
    }

    private static int FindNextNaluStart(ReadOnlySpan<byte> data, int startIndex, out int startCodeLen)
    {
        startCodeLen = 0;
        for (int i = startIndex; i < data.Length - 3; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLen = 3;
                    return i;
                }
                if (i < data.Length - 4 && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLen = 4;
                    return i;
                }
            }
        }
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
