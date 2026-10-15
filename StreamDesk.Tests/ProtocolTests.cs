using StreamDesk.Common.Network;
using Xunit;

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
            TargetDisplay = 2,
            Reserved = 0,
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
        Assert.Equal(original.TargetDisplay, deserialized.TargetDisplay);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
    }
}
