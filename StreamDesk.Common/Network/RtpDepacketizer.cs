using System.Buffers.Binary;

namespace StreamDesk.Common.Network;

/// <summary>
/// Reensamblador en tiempo real de flujos RTP H.264 FU-A hacia bitstream Annex-B.
/// </summary>
public class RtpDepacketizer
{
    private readonly MemoryStream _frameStream = new(64 * 1024);
    private readonly MemoryStream _nalStream = new(32 * 1024);
    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    private bool _assemblingFuA;
    private byte _currentFuNalHeader;
    private ushort _lastSeq;
    private bool _hasLastSeq;

    public event Action<byte[]>? OnFrameReady;

    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < RtpPacket.RtpHeaderSize) return;

        // Leer cabecera RTP
        byte v = (byte)((packet[0] >> 6) & 0x03);
        if (v != 2) return; // Versión RTP debe ser 2

        bool marker = (packet[1] & 0x80) != 0;
        byte payloadType = (byte)(packet[1] & 0x7F);
        if (payloadType != RtpPacket.PayloadTypeH264) return;

        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));

        if (_hasLastSeq && (ushort)(seq - _lastSeq) > 1)
        {
            // Se detectó pérdida de paquete en la red
            _assemblingFuA = false;
            _frameStream.SetLength(0);
        }
        _lastSeq = seq;
        _hasLastSeq = true;

        ReadOnlySpan<byte> payload = packet.Slice(RtpPacket.RtpHeaderSize);
        if (payload.Length == 0) return;

        byte nalHeader = payload[0];
        byte nalType = (byte)(nalHeader & 0x1F);

        // Caso 1: Single NAL Unit (1-23)
        if (nalType >= 1 && nalType <= 23)
        {
            _frameStream.Write(StartCode);
            _frameStream.Write(payload);
        }
        // Caso 2: FU-A Fragmentation Unit (Tipo 28)
        else if (nalType == 28)
        {
            if (payload.Length < 2) return;

            byte fuIndicator = payload[0];
            byte fuHeader = payload[1];
            bool start = (fuHeader & 0x80) != 0;
            bool end = (fuHeader & 0x40) != 0;
            byte reconstructedType = (byte)(fuHeader & 0x1F);
            byte reconstructedNri = (byte)(fuIndicator & 0x60);
            byte reconstructedHeader = (byte)(reconstructedNri | reconstructedType);

            ReadOnlySpan<byte> fuPayload = payload.Slice(2);

            if (start)
            {
                _assemblingFuA = true;
                _currentFuNalHeader = reconstructedHeader;
                _nalStream.SetLength(0);
                _nalStream.WriteByte(reconstructedHeader);
                _nalStream.Write(fuPayload);
            }
            else if (_assemblingFuA)
            {
                _nalStream.Write(fuPayload);
                if (end)
                {
                    _assemblingFuA = false;
                    _frameStream.Write(StartCode);
                    _nalStream.WriteTo(_frameStream);
                    _nalStream.SetLength(0);
                }
            }
        }

        // Si el bit de marcador RTP está activo, el frame H.264 está completo
        if (marker && _frameStream.Length > 0)
        {
            byte[] frame = _frameStream.ToArray();
            _frameStream.SetLength(0);
            OnFrameReady?.Invoke(frame);
        }
    }

    public void Reset()
    {
        _assemblingFuA = false;
        _frameStream.SetLength(0);
        _nalStream.SetLength(0);
        _hasLastSeq = false;
    }
}
