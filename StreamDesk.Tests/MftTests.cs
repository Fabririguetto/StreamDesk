using System;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Xunit;

namespace StreamDesk.Tests;

public class MftTests
{
    private static readonly Guid GuidAsyncUnlock = new("2477308d-c710-4ec7-bb4b-32b0a944ae4b"); // MF_TRANSFORM_ASYNC_UNLOCK

    [Fact]
    public void TestEncodingAndDraining()
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
                try { transform.Attributes?.Set(GuidAsyncUnlock, 1); } catch { }

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

        // Probar envío de 3 frames y drenado
        int nv12Size = 1920 * 1080 * 3 / 2;
        byte[] dummyData = new byte[nv12Size];

        for (int frame = 0; frame < 5; frame++)
        {
            using var sample = MediaFactory.MFCreateSample();
            using var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(nv12Size);

            IntPtr pBuffer;
            mediaBuffer.Lock(out pBuffer, out _, out _);
            Marshal.Copy(dummyData, 0, pBuffer, nv12Size);
            mediaBuffer.Unlock();
            mediaBuffer.CurrentLength = nv12Size;

            sample.AddBuffer(mediaBuffer);
            sample.SampleTime = (frame * 10_000_000L) / 60;
            sample.SampleDuration = 10_000_000L / 60;

            // Drenar primero si hay algo pendiente
            DrainOutput(workingTransform);

            try
            {
                workingTransform.ProcessInput(0, sample, 0);
            }
            catch (SharpGen.Runtime.SharpGenException ex) when ((uint)ex.HResult == 0xC00D36B5) // MF_E_NOTACCEPTING
            {
                Console.WriteLine("Got MF_E_NOTACCEPTING! Draining...");
                DrainOutput(workingTransform);
                workingTransform.ProcessInput(0, sample, 0); // Retry
            }

            DrainOutput(workingTransform);
        }

        workingTransform.Dispose();
        MediaFactory.MFShutdown();
    }

    private static void DrainOutput(IMFTransform transform)
    {
        while (true)
        {
            using var outSample = MediaFactory.MFCreateSample();
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(1024 * 1024);
            outSample.AddBuffer(outBuffer);

            var outputDataBuffer = new OutputDataBuffer
            {
                Sample = outSample
            };

            var res = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outputDataBuffer, out _);
            if (res.Failure || outputDataBuffer.Sample == null)
            {
                break;
            }

            // Output recibido con éxito
            using var s = outputDataBuffer.Sample;
            using var b = s.ConvertToContiguousBuffer();
            b.Lock(out _, out _, out int len);
            b.Unlock();
            if (len == 0) break;
            Console.WriteLine($"Drained encoded packet of {len} bytes");
        }
    }
}
