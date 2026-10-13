using System;
using System.Runtime.InteropServices;
using StreamDesk.Sender.Capture;
using StreamDesk.Sender.Encoding;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Xunit;

namespace StreamDesk.Tests;

public class EncoderEndToEndTest
{
    [Fact]
    public void TestEncoderProducesOutput()
    {
        var (device, context, _) = Direct3D11DeviceFactory.CreateDevice();

        MediaFactory.MFStartup(true);

        var h264Type = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x0000003F, null, h264Type);

        IMFTransform? encoder = null;
        foreach (var act in activates)
        {
            try
            {
                var candidate = act.ActivateObject<IMFTransform>();
                if (candidate == null) continue;

                var outputType = MediaFactory.MFCreateMediaType();
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 25000000u);
                MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, 1920, 1080);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, 60, 1);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                candidate.SetOutputType(0, outputType, 0);

                var inputType = MediaFactory.MFCreateMediaType();
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, 1920, 1080);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, 60, 1);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                candidate.SetInputType(0, inputType, 0);

                candidate.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                candidate.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                encoder = candidate;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Init candidate failed: {ex.Message}");
            }
        }

        Assert.NotNull(encoder);

        int nv12Size = 1920 * 1080 * 3 / 2;
        byte[] dummy = new byte[nv12Size];
        int packets = 0;

        for (int frame = 0; frame < 30; frame++)
        {
            using var sample = MediaFactory.MFCreateSample();
            using var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(nv12Size);
            mediaBuffer.Lock(out IntPtr pBuf, out _, out _);
            Marshal.Copy(dummy, 0, pBuf, nv12Size);
            mediaBuffer.Unlock();
            mediaBuffer.CurrentLength = nv12Size;

            sample.AddBuffer(mediaBuffer);
            sample.SampleTime = (frame * 10_000_000L) / 60;
            sample.SampleDuration = 10_000_000L / 60;

            encoder.ProcessInput(0, sample, 0);

            // Drenar
            while (true)
            {
                using var outSample = MediaFactory.MFCreateSample();
                using var outBuffer = MediaFactory.MFCreateMemoryBuffer(4 * 1024 * 1024);
                outSample.AddBuffer(outBuffer);

                var outData = new OutputDataBuffer { Sample = outSample };
                var outRes = encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outData, out _);
                if (outRes.Failure || outData.Sample == null)
                {
                    break;
                }

                using var s = outData.Sample;
                using var b = s.ConvertToContiguousBuffer();
                b.Lock(out IntPtr pBytes, out _, out int len);
                byte[] firstBytes = new byte[Math.Min(len, 16)];
                Marshal.Copy(pBytes, firstBytes, 0, firstBytes.Length);
                b.Unlock();
                if (len > 0)
                {
                    packets++;
                    Console.WriteLine($"Frame {frame}: Produced {len} bytes NALU. First bytes: {BitConverter.ToString(firstBytes)}");
                }
                else
                {
                    break;
                }
            }
        }

        Console.WriteLine($"Total packets after 30 frames: {packets}");
        encoder.Dispose();
        MediaFactory.MFShutdown();

        Assert.True(packets > 0, $"Packets: {packets}");
    }

    [Fact]
    public void TestFullEncoderAndDecoderPipeline()
    {
        var (device, context, _) = Direct3D11DeviceFactory.CreateDevice();

        using var encoder = new MfH264HardwareEncoder(device, context, 1280, 720, 60, 15000);
        using var decoder = new StreamDesk.Receiver.Decoding.MfH264HardwareDecoder(device, context, 1280, 720);

        int encodedFrames = 0;
        int decodedFrames = 0;

        encoder.OnEncodedFrame += data =>
        {
            encodedFrames++;
            decoder.DecodeFrame(data);
        };

        decoder.OnFrameDecoded += tex =>
        {
            decodedFrames++;
        };

        var desc = new Texture2DDescription
        {
            Width = 1280,
            Height = 720,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None
        };
        using var testTex = device.CreateTexture2D(desc);

        // Enviar 60 texturas a codificar y decodificar
        for (int i = 0; i < 60; i++)
        {
            encoder.EncodeTexture(testTex, i * 166666L);
        }

        Console.WriteLine($"Full pipeline: Encoded frames = {encodedFrames}, Decoded frames = {decodedFrames}");
        Assert.True(encodedFrames > 0, $"Encoded: {encodedFrames}");
        Assert.True(decodedFrames > 0, $"Decoded: {decodedFrames}");
    }
}
