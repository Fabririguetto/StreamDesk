using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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

    [Fact]
    public void TestStartReaderOnWebcam()
    {
        MediaFactory.MFStartup(true);

        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);

        using var devices = MediaFactory.MFEnumDeviceSources(attributes);
        Assert.True(devices.Count() > 0);

        var firstDev = devices.First();
        string name = firstDev.GetString(CaptureDeviceAttributeKeys.FriendlyName);
        string symLink = firstDev.GetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
        Console.WriteLine($"Name: {name}, SymLink: {symLink}");

        using var devAttrs = MediaFactory.MFCreateAttributes(2);
        devAttrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        devAttrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, symLink);

        var mediaSource = MediaFactory.MFCreateDeviceSource(devAttrs);
        Assert.NotNull(mediaSource);
        Console.WriteLine("Created mediaSource successfully via symbolic link!");

        using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(mediaSource, null);

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

        var currentType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(currentType, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
        Console.WriteLine($"Selected format: {w}x{h} NV12");
        currentType.Dispose();

        IMFSample? sample = null;
        for (int i = 0; i < 20; i++)
        {
            sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out int actualStreamIndex, out var streamFlags, out long timestamp);
            if (sample != null)
            {
                using var buf = sample.ConvertToContiguousBuffer();
                buf.Lock(out IntPtr pData, out _, out int len);
                Console.WriteLine($"Got real video frame! Length={len} bytes at {w}x{h}");
                buf.Unlock();
                sample.Dispose();
                break;
            }
            Thread.Sleep(30);
        }

        mediaSource.Dispose();
        MediaFactory.MFShutdown();
    }

    [Fact]
    public void TestDeviceReaderLifecycleOnRealDevice()
    {
        var devices = StreamDesk.Monitor.Capture.VideoCaptureDeviceEnumerator.GetConnectedDevices();
        if (devices.Count == 0) return; // Si no hay dispositivos conectados en el entorno, saltear

        var device = devices.First();
        Console.WriteLine($"Testing device: {device.Name} (Formats: {device.SupportedFormats.Count})");
        Assert.NotEmpty(device.SupportedFormats);

        var format = device.SupportedFormats.FirstOrDefault();
        using var reader = new StreamDesk.Monitor.Capture.MediaFoundationDeviceReader(device, format);

        int frameCount = 0;
        reader.OnFrameArrived += (pData, len, w, h, stride) =>
        {
            Interlocked.Increment(ref frameCount);
        };

        // 1. Iniciar captura
        reader.Start();
        Assert.True(reader.IsRunning);

        // Esperar primeros fotogramas (máximo 2 segundos)
        var sw = Stopwatch.StartNew();
        while (frameCount == 0 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(50);
        }

        Console.WriteLine($"Frames captured on first run: {frameCount} (Width={reader.Width}, Height={reader.Height})");
        Assert.True(frameCount > 0, "Debería haber recibido al menos un fotograma de video.");

        // 2. Detener captura
        reader.Stop();
        Assert.False(reader.IsRunning);

        int countBeforeRestart = frameCount;

        // 3. Reiniciar captura para verificar que el ciclo de vida y SymbolicLink funcionan repetidamente
        reader.Start();
        Assert.True(reader.IsRunning);

        sw.Restart();
        while (frameCount == countBeforeRestart && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(50);
        }

        Console.WriteLine($"Frames captured after restart: {frameCount - countBeforeRestart}");
        Assert.True(frameCount > countBeforeRestart, "Debería recibir nuevos fotogramas tras reiniciar el lector.");

        reader.Stop();
        Assert.False(reader.IsRunning);
    }
}
