using System.Buffers.Binary;

namespace StreamDesk.Common.Network;

/// <summary>
/// Packetizer y Depacketizer RFC 6184 para transporte de video H.264 sobre RTP/UDP de ultra baja latencia.
/// </summary>
public static class RtpPacket
{
    public const int RtpHeaderSize = 12;
    public const byte PayloadTypeH264 = 96;

    /// <summary>
    /// Fragmenta un NALU H.264 en paquetes RTP según RFC 6184 (Single NALU o FU-A).
    /// </summary>
    public static List<byte[]> PacketizeH264Nalu(
        ReadOnlySpan<byte> nalu,
        ref ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        bool isLastNaluOfFrame,
        int maxPayloadSize = 1400)
    {
        var packets = new List<byte[]>();
        if (nalu.Length == 0) return packets;

        byte naluHeader = nalu[0];
        byte nalRefIdc = (byte)(naluHeader & 0x60);
        byte nalType = (byte)(naluHeader & 0x1F);

        // Caso 1: NALU cabe en un solo paquete RTP (Single NAL Unit)
        if (nalu.Length <= maxPayloadSize)
        {
            byte[] packet = new byte[RtpHeaderSize + nalu.Length];
            WriteRtpHeader(packet, isLastNaluOfFrame, sequenceNumber++, timestamp, ssrc);
            nalu.CopyTo(packet.AsSpan(RtpHeaderSize));
            packets.Add(packet);
            return packets;
        }

        // Caso 2: NALU requiere fragmentación FU-A (RFC 6184 Tipo 28)
        ReadOnlySpan<byte> payload = nalu.Slice(1); // Excluir la cabecera NAL original
        int offset = 0;
        int remaining = payload.Length;
        int maxFuPayload = maxPayloadSize - 2; // Descontar FU Indicator (1B) y FU Header (1B)

        while (remaining > 0)
        {
            int chunkSize = Math.Min(remaining, maxFuPayload);
            bool isFirstChunk = (offset == 0);
            bool isLastChunk = (remaining <= maxFuPayload);
            bool marker = isLastChunk && isLastNaluOfFrame;

            byte[] packet = new byte[RtpHeaderSize + 2 + chunkSize];
            WriteRtpHeader(packet, marker, sequenceNumber++, timestamp, ssrc);

            // FU Indicator: [F (1b) | NRI (2b) | Type=28 (5b)]
            packet[RtpHeaderSize] = (byte)(nalRefIdc | 28);

            // FU Header: [S (1b) | E (1b) | R (1b) | Type (5b)]
            byte fuHeader = nalType;
            if (isFirstChunk) fuHeader |= 0x80; // S = 1
            if (isLastChunk) fuHeader |= 0x40;  // E = 1
            packet[RtpHeaderSize + 1] = fuHeader;

            // Datos del chunk
            payload.Slice(offset, chunkSize).CopyTo(packet.AsSpan(RtpHeaderSize + 2));
            packets.Add(packet);

            offset += chunkSize;
            remaining -= chunkSize;
        }

        return packets;
    }

    private static void WriteRtpHeader(Span<byte> buffer, bool marker, ushort seq, uint timestamp, uint ssrc)
    {
        buffer[0] = 0x80; // V=2, P=0, X=0, CC=0
        buffer[1] = (byte)((marker ? 0x80 : 0x00) | (PayloadTypeH264 & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), ssrc);
    }
}
