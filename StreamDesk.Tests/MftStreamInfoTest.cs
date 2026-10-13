using System;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using StreamDesk.Sender.Capture;
using Xunit;

namespace StreamDesk.Tests;

public class MftStreamInfoTest
{
    [Fact]
    public void CheckStreamInfo()
    {
        MediaFactory.MFStartup(true);

        var h264Type = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x0000003F, null, h264Type);

        foreach (var act in activates)
        {
            var transform = act.ActivateObject<IMFTransform>();
            if (transform == null) continue;

            try
            {
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

                var inInfo = transform.GetInputStreamInfo(0);
                var outInfo = transform.GetOutputStreamInfo(0);

                Console.WriteLine($"Input Size: {inInfo.Size}, Flags: {inInfo.Flags}");
                Console.WriteLine($"Output Size: {outInfo.Size}, Flags: {outInfo.Flags}");

                foreach (var f in typeof(TransformAttributeKeys).GetFields())
                {
                    Console.WriteLine($"Key: {f.Name} = {f.GetValue(null)}");
                }
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            transform.Dispose();
        }

        MediaFactory.MFShutdown();
    }

    [Fact]
    public void CheckDecoderOutputTypes()
    {
        MediaFactory.MFStartup(true);

        var h264Input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, 0x0000003F, h264Input, null);

        foreach (var act in activates)
        {
            var transform = act.ActivateObject<IMFTransform>();
            if (transform == null) continue;

            try
            {
                var inputType = MediaFactory.MFCreateMediaType();
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, 1920, 1080);
                transform.SetInputType(0, inputType, 0);

                Console.WriteLine("Available output types for decoder:");
                int typeIndex = 0;
                while (true)
                {
                    try
                    {
                        var outType = transform.GetOutputAvailableType(0, typeIndex);
                        var subtype = outType.GetGUID(MediaTypeAttributeKeys.Subtype);
                        Console.WriteLine($"Type {typeIndex}: Subtype = {subtype}");
                        typeIndex++;
                    }
                    catch
                    {
                        break;
                    }
                }
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Decoder error: {ex.Message}");
            }
            transform.Dispose();
        }

        MediaFactory.MFShutdown();
    }

    [Fact]
    public void TestNv12ToBgraPerformance()
    {
        int width = 1920;
        int height = 1080;
        int nv12Size = width * height * 3 / 2;
        byte[] nv12 = new byte[nv12Size];
        byte[] bgra = new byte[width * height * 4];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        unsafe
        {
            fixed (byte* pNv12 = nv12)
            fixed (byte* pBgra = bgra)
            {
                byte* pY = pNv12;
                byte* pUV = pNv12 + (width * height);

                for (int y = 0; y < height; y++)
                {
                    byte* yRow = pY + (y * width);
                    byte* uvRow = pUV + ((y / 2) * width);
                    byte* bgraRow = pBgra + (y * width * 4);

                    for (int x = 0; x < width; x++)
                    {
                        int yVal = yRow[x];
                        int uvIdx = (x & ~1);
                        int uVal = uvRow[uvIdx] - 128;
                        int vVal = uvRow[uvIdx + 1] - 128;

                        int c = yVal - 16;
                        if (c < 0) c = 0;

                        int r = (298 * c + 409 * vVal + 128) >> 8;
                        int g = (298 * c - 100 * uVal - 208 * vVal + 128) >> 8;
                        int b = (298 * c + 516 * uVal + 128) >> 8;

                        int dstIdx = x * 4;
                        bgraRow[dstIdx + 0] = (byte)Math.Clamp(b, 0, 255);
                        bgraRow[dstIdx + 1] = (byte)Math.Clamp(g, 0, 255);
                        bgraRow[dstIdx + 2] = (byte)Math.Clamp(r, 0, 255);
                        bgraRow[dstIdx + 3] = 255;
                    }
                }
            }
        }
        sw.Stop();
        Console.WriteLine($"NV12 to BGRA took: {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Fact]
    public void TestUpdateSubresourceAndCopyResource()
    {
        var (device, context, _) = Direct3D11DeviceFactory.CreateDevice();

        var desc = new Texture2DDescription
        {
            Width = 1920,
            Height = 1080,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None
        };

        using var tex1 = device.CreateTexture2D(desc);
        using var tex2 = device.CreateTexture2D(desc);

        byte[] dummy = new byte[1920 * 1080 * 4];
        dummy[0] = 255;

        context.UpdateSubresource(dummy, tex1, 0, (uint)(1920 * 4));
        context.CopyResource(tex2, tex1);
        Console.WriteLine("UpdateSubresource and CopyResource succeeded!");
    }
}
