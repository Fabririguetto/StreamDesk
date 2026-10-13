using System;
using Vortice.MediaFoundation;
using Xunit;

namespace StreamDesk.Tests;

public class MftTests
{
    private static readonly Guid GuidAsyncUnlock = new("2477308d-c710-4ec7-bb4b-32b0a944ae4b"); // MF_TRANSFORM_ASYNC_UNLOCK
    private static readonly Guid GuidAsync = new("a2142eb9-80c9-4f61-a1ca-98d603d7723a");       // MF_TRANSFORM_ASYNC

    [Fact]
    public void EnumerateEncoders_TestAsyncUnlock()
    {
        MediaFactory.MFStartup(true);

        var h264Type = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = 0x0000003F; // MFT_ENUM_FLAG_ALL
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, h264Type);

        IMFTransform? workingTransform = null;
        foreach (var act in activates)
        {
            var transform = act.ActivateObject<IMFTransform>();
            if (transform == null) continue;

            try
            {
                // Desbloquear si tiene atributos
                try
                {
                    var attributes = transform.Attributes;
                    attributes?.Set(GuidAsyncUnlock, 1);
                }
                catch
                {
                    // Algunos transforms no tienen atributos generales
                }

                var outputType = MediaFactory.MFCreateMediaType();
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 25000000u);
                MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, 1920, 1080);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, 60, 1);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                transform.SetOutputType(0, outputType, 0);

                var inputType = MediaFactory.MFCreateMediaType();
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, 1920, 1080);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, 60, 1);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                transform.SetInputType(0, inputType, 0);

                workingTransform = transform;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Transform error: {ex.Message}");
                transform.Dispose();
            }
        }

        Assert.NotNull(workingTransform);
        workingTransform.Dispose();
        MediaFactory.MFShutdown();
    }
}
