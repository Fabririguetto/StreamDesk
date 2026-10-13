using System;
using Vortice.MediaFoundation;
using Xunit;

namespace StreamDesk.Tests;

public class MftDiagnosticTests
{
    private static readonly Guid GuidAsync = new("a2142eb9-80c9-4f61-a1ca-98d603d7723a");       // MF_TRANSFORM_ASYNC

    [Fact]
    public void PrintAllEncodersInfo()
    {
        MediaFactory.MFStartup(true);

        var h264Type = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = 0x0000003F; // MFT_ENUM_FLAG_ALL
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, h264Type);

        int count = 0;
        foreach (var act in activates)
        {
            count++;
            var transform = act.ActivateObject<IMFTransform>();
            if (transform == null) continue;

            bool isAsync = false;
            try
            {
                var attr = transform.Attributes;
                if (attr != null && attr.GetUInt32(GuidAsync, out uint aVal).Success)
                {
                    isAsync = (aVal == 1);
                }
            }
            catch { }

            Console.WriteLine($"Encoder [{count}]: isAsync={isAsync}");
            transform.Dispose();
        }

        MediaFactory.MFShutdown();
    }
}
