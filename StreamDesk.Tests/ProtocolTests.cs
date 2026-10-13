using StreamDesk.Common.Network;

namespace StreamDesk.Tests;

public class ProtocolTests
{
    [Fact]
    public void ControlPacket_SerializationRoundtrip_PreservesData()
    {
        var original = new ControlPacket
        {
            PacketType = ControlPacketType.MouseMove,
            ButtonOrFlags = (byte)MouseButtonType.Left,
            NormalizedX = 32768,
            NormalizedY = 49152,
            KeyCode = 0x41, // VK_A
            WheelDelta = 120,
            Timestamp = 987654321
        };

        byte[] serialized = ControlPacket.Serialize(ref original);
        Assert.NotNull(serialized);
        Assert.Equal(16, serialized.Length);

        var deserialized = ControlPacket.Deserialize(serialized);

        Assert.Equal(original.PacketType, deserialized.PacketType);
        Assert.Equal(original.ButtonOrFlags, deserialized.ButtonOrFlags);
        Assert.Equal(original.NormalizedX, deserialized.NormalizedX);
        Assert.Equal(original.NormalizedY, deserialized.NormalizedY);
        Assert.Equal(original.KeyCode, deserialized.KeyCode);
        Assert.Equal(original.WheelDelta, deserialized.WheelDelta);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public void RtpSingleNalu_PacketizeAndDepacketize_ReassemblesCorrectly()
    {
        // NALU pequeño (500 bytes)
        byte[] nalu = new byte[500];
        nalu[0] = 0x67; // SPS NALU header
        for (int i = 1; i < nalu.Length; i++) nalu[i] = (byte)(i % 256);

        ushort seq = 100;
        var packets = RtpPacket.PacketizeH264Nalu(nalu, ref seq, timestamp: 90000, ssrc: 12345, isLastNaluOfFrame: true, maxPayloadSize: 1400);

        Assert.Single(packets);

        var depacketizer = new RtpDepacketizer();
        byte[]? reassembledFrame = null;
        depacketizer.OnFrameReady += frame => reassembledFrame = frame;

        depacketizer.ProcessPacket(packets[0]);

        Assert.NotNull(reassembledFrame);
        // Debe contener el StartCode (4 bytes) + nalu (500 bytes) = 504 bytes
        Assert.Equal(504, reassembledFrame.Length);
        Assert.Equal(0x00, reassembledFrame[0]);
        Assert.Equal(0x00, reassembledFrame[1]);
        Assert.Equal(0x00, reassembledFrame[2]);
        Assert.Equal(0x01, reassembledFrame[3]);
        Assert.Equal(0x67, reassembledFrame[4]);
    }

    [Fact]
    public void RtpFuA_LargeNalu_FragmentationAndReassembly_Success()
    {
        // NALU grande (35 KB - IDR Keyframe típico)
        byte[] largeNalu = new byte[35 * 1024];
        largeNalu[0] = 0x65; // IDR Slice
        for (int i = 1; i < largeNalu.Length; i++) largeNalu[i] = (byte)((i * 3) % 256);

        ushort seq = 1;
        var packets = RtpPacket.PacketizeH264Nalu(largeNalu, ref seq, timestamp: 90000, ssrc: 54321, isLastNaluOfFrame: true, maxPayloadSize: 1400);

        Assert.True(packets.Count > 1, "Un NALU de 35 KB debe fragmentarse en múltiples paquetes FU-A");

        var depacketizer = new RtpDepacketizer();
        byte[]? reassembledFrame = null;
        depacketizer.OnFrameReady += frame => reassembledFrame = frame;

        foreach (var packet in packets)
        {
            depacketizer.ProcessPacket(packet);
        }

        Assert.NotNull(reassembledFrame);
        Assert.Equal(largeNalu.Length + 4, reassembledFrame.Length);

        // Verificar start code
        Assert.Equal(0x00, reassembledFrame[0]);
        Assert.Equal(0x00, reassembledFrame[1]);
        Assert.Equal(0x00, reassembledFrame[2]);
        Assert.Equal(0x01, reassembledFrame[3]);

        // Verificar primer byte del NALU reconstruido
        Assert.Equal(0x65, reassembledFrame[4]);

        // Verificar integridad del contenido
        for (int i = 1; i < largeNalu.Length; i++)
        {
            Assert.Equal(largeNalu[i], reassembledFrame[4 + i]);
        }
    }
}
