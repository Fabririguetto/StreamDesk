using System;
using Vortice.MediaFoundation;
using Xunit;

namespace StreamDesk.Tests;

public class MftStreamingMessageTest
{
    [Fact]
    public void TestProcessMessages()
    {
        foreach (var name in typeof(TMessageType).GetEnumNames())
        {
            Console.WriteLine($"TMessageType: {name} = {(int)Enum.Parse(typeof(TMessageType), name)}");
        }
    }
}
