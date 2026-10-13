using System.Net;
using System.Net.Sockets;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;

namespace StreamDesk.Sender.Transport;

/// <summary>
/// Emisor de video de ultra baja latencia sobre UDP utilizando RFC 6184 RTP.
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

    public UdpVideoSender()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            SendBufferSize = 4 * 1024 * 1024 // Buffer de envío de 4 MB
        };
        _ssrc = (uint)Random.Shared.Next();
    }

    public void SetTarget(string ipAddress, int port)
    {
        _targetEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
    }

    /// <summary>
    /// Envía un frame completo de video (uno o más NALUs) a la PC receptora.
    /// </summary>
    public void SendFrame(ReadOnlySpan<byte> h264Bitstream, int maxPayloadSize = 1400)
    {
        if (_targetEndPoint == null || _disposed || h264Bitstream.Length == 0) return;

        _timestamp += 90000 / 60; // Incremento estándar para 60 FPS (90 kHz clock)

        // Parsear y fragmentar NALUs en formato Annex-B
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
                _socket.SendTo(packet, _targetEndPoint);
                PacketsSent++;
                BytesSent += packet.Length;
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
