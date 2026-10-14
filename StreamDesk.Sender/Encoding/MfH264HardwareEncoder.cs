using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace StreamDesk.Sender.Encoding;

/// <summary>
/// Codificador de video H.264 de ultra baja latencia acelerado por hardware mediante Windows Media Foundation (MFT).
/// Prioriza codificadores síncronos con compatibilidad directa para pipelines D3D11 y gestiona el ciclo de vida del stream.
/// </summary>
public class MfH264HardwareEncoder : IDisposable
{
    private const uint MF_E_NOTACCEPTING = 0xC00D36B5;
    private static readonly Guid GuidAsyncUnlock = new("2477308d-c710-4ec7-bb4b-32b0a944ae4b"); // MF_TRANSFORM_ASYNC_UNLOCK
    private static readonly Guid GuidAsync = new("a2142eb9-80c9-4f61-a1ca-98d603d7723a");       // MF_TRANSFORM_ASYNC
    private static readonly Guid GuidMpeg2Profile = new("ad76a21e-fede-404e-8866-03c4f696660f"); // MF_MT_MPEG2_PROFILE
    private static readonly Guid GuidLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");   // CODECAPI_AVLowLatencyMode

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _bitrate;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private IMFTransform? _encoder;
    private ID3D11Texture2D? _stagingTexture;
    private int _outputBufferSize = 4 * 1024 * 1024;
    private bool _initialized;
    private bool _disposed;
    private long _frameIndex;

    public event Action<byte[]>? OnEncodedFrame;

    public MfH264HardwareEncoder(ID3D11Device device, ID3D11DeviceContext context, int width, int height, int fps = 60, int bitrateKbps = 25000)
    {
        _device = device;
        _context = context;
        _width = width;
        _height = height;
        _fps = fps;
        _bitrate = bitrateKbps * 1000;

        InitializeEncoder();
    }

    private void InitializeEncoder()
    {
        MediaFactory.MFStartup(true);

        var h264Type = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = 0x0000003F; // MFT_ENUM_FLAG_ALL
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, h264Type);

        var candidates = new List<(IMFTransform Transform, bool IsAsync)>();

        foreach (var act in activates)
        {
            try
            {
                var candidate = act.ActivateObject<IMFTransform>();
                if (candidate == null) continue;

                bool isAsync = false;
                try
                {
                    if (candidate.Attributes != null && candidate.Attributes.GetUInt32(GuidAsync, out uint aVal).Success)
                    {
                        isAsync = (aVal == 1);
                    }
                }
                catch { }

                candidates.Add((candidate, isAsync));
            }
            catch { }
        }

        // Priorizar codificadores síncronos (directos sin event generator) y luego asíncronos desbloqueados
        var sortedCandidates = candidates.OrderBy(c => c.IsAsync ? 1 : 0);

        foreach (var (candidate, isAsync) in sortedCandidates)
        {
            try
            {
                if (isAsync)
                {
                    try { candidate.Attributes?.Set(GuidAsyncUnlock, 1); } catch { }
                }

                try { candidate.Attributes?.Set(GuidLowLatency, 1); } catch { }

                // 1. Configurar tipo de salida (H.264 Baseline Profile para cero B-frames y latencia mínima)
                var outputType = MediaFactory.MFCreateMediaType();
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrate);
                try { outputType.Set(GuidMpeg2Profile, 66u); } catch { } // Baseline profile (sin B-frames)
                MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                candidate.SetOutputType(0, outputType, 0);

                // 2. Configurar tipo de entrada (NV12)
                var inputType = MediaFactory.MFCreateMediaType();
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                candidate.SetInputType(0, inputType, 0);

                // 3. Notificar inicio de streaming al codificador
                try
                {
                    candidate.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                    candidate.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                }
                catch { }

                try
                {
                    var streamInfo = candidate.GetOutputStreamInfo(0);
                    _outputBufferSize = Math.Max(streamInfo.Size, 4 * 1024 * 1024);
                }
                catch
                {
                    _outputBufferSize = 4 * 1024 * 1024;
                }

                _encoder = candidate;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encoder] Candidato descartado ({ex.Message}). Probando siguiente...");
                candidate.Dispose();
            }
        }

        // Limpiar candidatos no utilizados
        foreach (var (candidate, _) in candidates)
        {
            if (candidate != _encoder)
            {
                candidate.Dispose();
            }
        }

        if (_encoder == null)
        {
            throw new InvalidOperationException("No se encontró ningún codificador H.264 compatible en el sistema.");
        }

        // 4. Crear textura staging D3D11
        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        };
        _stagingTexture = _device.CreateTexture2D(desc);

        _initialized = true;
    }

    public void EncodeTexture(ID3D11Texture2D texture, long timestampTicks)
    {
        if (!_initialized || _disposed || _encoder == null || _stagingTexture == null) return;

        try
        {
            _context.CopyResource(_stagingTexture, texture);

            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            if (mapped.DataPointer == IntPtr.Zero) return;

            int width = _width;
            int height = _height;
            int halfHeight = height / 2;
            int nv12Size = width * height * 3 / 2;

            using var sample = MediaFactory.MFCreateSample();
            using var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(nv12Size);

            IntPtr pBuffer;
            mediaBuffer.Lock(out pBuffer, out _, out _);

            unsafe
            {
                byte* pSrc = (byte*)mapped.DataPointer;
                int srcPitch = (int)mapped.RowPitch;
                byte* pDst = (byte*)pBuffer;
                byte* pY = pDst;
                byte* pUV = pDst + (width * height);

                Parallel.For(0, halfHeight, yHalf =>
                {
                    int y0 = yHalf * 2;
                    int y1 = y0 + 1;

                    byte* srcRow0 = pSrc + (y0 * srcPitch);
                    byte* srcRow1 = pSrc + (y1 * srcPitch);

                    byte* dstYRow0 = pY + (y0 * width);
                    byte* dstYRow1 = pY + (y1 * width);
                    byte* dstUVRow = pUV + (yHalf * width);

                    for (int x = 0; x < width; x += 2)
                    {
                        // Pixel (x, y0)
                        byte b00 = srcRow0[x * 4 + 0];
                        byte g00 = srcRow0[x * 4 + 1];
                        byte r00 = srcRow0[x * 4 + 2];
                        int y00 = ((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16;
                        dstYRow0[x] = (byte)(y00 < 0 ? 0 : (y00 > 255 ? 255 : y00));

                        // Pixel (x+1, y0)
                        byte b10 = srcRow0[(x + 1) * 4 + 0];
                        byte g10 = srcRow0[(x + 1) * 4 + 1];
                        byte r10 = srcRow0[(x + 1) * 4 + 2];
                        int y10 = ((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16;
                        dstYRow0[x + 1] = (byte)(y10 < 0 ? 0 : (y10 > 255 ? 255 : y10));

                        // Pixel (x, y1)
                        byte b01 = srcRow1[x * 4 + 0];
                        byte g01 = srcRow1[x * 4 + 1];
                        byte r01 = srcRow1[x * 4 + 2];
                        int y01 = ((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16;
                        dstYRow1[x] = (byte)(y01 < 0 ? 0 : (y01 > 255 ? 255 : y01));

                        // Pixel (x+1, y1)
                        byte b11 = srcRow1[(x + 1) * 4 + 0];
                        byte g11 = srcRow1[(x + 1) * 4 + 1];
                        byte r11 = srcRow1[(x + 1) * 4 + 2];
                        int y11 = ((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16;
                        dstYRow1[x + 1] = (byte)(y11 < 0 ? 0 : (y11 > 255 ? 255 : y11));

                        // Submuestreo 2x2 chroma con promedio anti-aliasing
                        int avgR = (r00 + r10 + r01 + r11 + 2) >> 2;
                        int avgG = (g00 + g10 + g01 + g11 + 2) >> 2;
                        int avgB = (b00 + b10 + b01 + b11 + 2) >> 2;

                        int uVal = ((-38 * avgR - 74 * avgG + 112 * avgB + 128) >> 8) + 128;
                        int vVal = ((112 * avgR - 94 * avgG - 18 * avgB + 128) >> 8) + 128;

                        dstUVRow[x + 0] = (byte)(uVal < 0 ? 0 : (uVal > 255 ? 255 : uVal));
                        dstUVRow[x + 1] = (byte)(vVal < 0 ? 0 : (vVal > 255 ? 255 : vVal));
                    }
                });
            }

            _context.Unmap(_stagingTexture, 0);
            mediaBuffer.Unlock();
            mediaBuffer.CurrentLength = nv12Size;

            sample.AddBuffer(mediaBuffer);
            long sampleTime = (_frameIndex++ * 10_000_000L) / _fps;
            sample.SampleTime = sampleTime;
            sample.SampleDuration = 10_000_000L / _fps;

            // Drenar antes de ingresar nuevo frame
            DrainOutput();

            try
            {
                _encoder.ProcessInput(0, sample, 0);
            }
            catch (SharpGen.Runtime.SharpGenException ex) when ((uint)ex.HResult == MF_E_NOTACCEPTING)
            {
                // Drenar y reintentar una vez; si aún no acepta, descartar este frame para no congelar el stream en vivo
                DrainOutput();
                try
                {
                    _encoder.ProcessInput(0, sample, 0);
                }
                catch (SharpGen.Runtime.SharpGenException retryEx) when ((uint)retryEx.HResult == MF_E_NOTACCEPTING)
                {
                    // Frame descartado con gracia
                    return;
                }
            }

            DrainOutput();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[H264 Encoder] Error: {ex.Message}");
        }
    }

    private void DrainOutput()
    {
        if (_encoder == null) return;

        while (true)
        {
            using var outSample = MediaFactory.MFCreateSample();
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            outSample.AddBuffer(outBuffer);

            var outputDataBuffer = new OutputDataBuffer
            {
                Sample = outSample
            };

            var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputDataBuffer, out _);
            if (result.Failure || outputDataBuffer.Sample == null)
            {
                break;
            }

            using var buffer = outSample.ConvertToContiguousBuffer();

            IntPtr pData;
            buffer.Lock(out pData, out _, out int currentLength);
            if (currentLength > 0)
            {
                byte[] encodedBytes = new byte[currentLength];
                Marshal.Copy(pData, encodedBytes, 0, currentLength);
                buffer.Unlock();

                OnEncodedFrame?.Invoke(encodedBytes);
            }
            else
            {
                buffer.Unlock();
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stagingTexture?.Dispose();
        _encoder?.Dispose();
        MediaFactory.MFShutdown();
    }
}
