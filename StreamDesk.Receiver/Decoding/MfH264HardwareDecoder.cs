using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace StreamDesk.Receiver.Decoding;

/// <summary>
/// Decodificador de video H.264 acelerado por hardware mediante Media Foundation Transform (MFT).
/// Configurado en modo de baja latencia con salida directa a texturas DirectX 11, drenado continuo y priorización síncrona.
/// </summary>
public class MfH264HardwareDecoder : IDisposable
{
    private const uint MF_E_NOTACCEPTING = 0xC00D36B5;
    private static readonly Guid GuidAsyncUnlock = new("2477308d-c710-4ec7-bb4b-32b0a944ae4b"); // MF_TRANSFORM_ASYNC_UNLOCK
    private static readonly Guid GuidAsync = new("a2142eb9-80c9-4f61-a1ca-98d603d7723a");       // MF_TRANSFORM_ASYNC

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _bgraBuffer;
    private int _outputBufferSize = 4 * 1024 * 1024;
    private long _frameIndex;

    private IMFTransform? _decoder;
    private ID3D11Texture2D? _renderTexture;
    private ID3D11ShaderResourceView? _renderSrv;
    private bool _initialized;
    private bool _disposed;

    public event Action<ID3D11Texture2D>? OnFrameDecoded;

    public MfH264HardwareDecoder(ID3D11Device device, ID3D11DeviceContext context, int width = 1920, int height = 1080)
    {
        _device = device;
        _context = context;
        _width = width;
        _height = height;
        _bgraBuffer = new byte[width * height * 4];

        InitializeDecoder();
    }

    private void InitializeDecoder()
    {
        MediaFactory.MFStartup(true);

        var h264Input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = 0x0000003F; // MFT_ENUM_FLAG_ALL
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, flags, h264Input, null);

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

        var sortedCandidates = candidates.OrderBy(c => c.IsAsync ? 1 : 0);

        foreach (var (candidate, isAsync) in sortedCandidates)
        {
            try
            {
                if (isAsync)
                {
                    try { candidate.Attributes?.Set(GuidAsyncUnlock, 1); } catch { }
                }

                var guidLowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
                try { candidate.Attributes?.Set(guidLowLatency, 1); } catch { }

                // 1. Tipo de entrada: H.264
                var inputType = MediaFactory.MFCreateMediaType();
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
                candidate.SetInputType(0, inputType, 0);

                // 2. Tipo de salida: NV12
                var outputType = MediaFactory.MFCreateMediaType();
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
                candidate.SetOutputType(0, outputType, 0);

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

                _decoder = candidate;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Decoder] Candidato descartado ({ex.Message}). Probando siguiente...");
                candidate.Dispose();
            }
        }

        foreach (var (candidate, _) in candidates)
        {
            if (candidate != _decoder) candidate.Dispose();
        }

        if (_decoder == null)
        {
            throw new InvalidOperationException("No se encontró ningún decodificador H.264 compatible en el sistema.");
        }

        // 3. Crear textura DirectX 11 de renderizado para visualización
        var texDesc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None
        };
        _renderTexture = _device.CreateTexture2D(texDesc);
        _renderSrv = _device.CreateShaderResourceView(_renderTexture);

        _initialized = true;
    }

    public void DecodeFrame(byte[] h264Data)
    {
        if (!_initialized || _disposed || _decoder == null || h264Data.Length == 0) return;

        try
        {
            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(h264Data.Length);

            IntPtr pBuffer;
            buffer.Lock(out pBuffer, out _, out _);
            Marshal.Copy(h264Data, 0, pBuffer, h264Data.Length);
            buffer.Unlock();
            buffer.CurrentLength = h264Data.Length;

            sample.AddBuffer(buffer);
            sample.SampleTime = (_frameIndex++ * 10_000_000L) / 60;
            sample.SampleDuration = 10_000_000L / 60;

            PullDecodedFrame();

            try
            {
                _decoder.ProcessInput(0, sample, 0);
            }
            catch (SharpGen.Runtime.SharpGenException ex) when ((uint)ex.HResult == MF_E_NOTACCEPTING)
            {
                PullDecodedFrame();
                try
                {
                    _decoder.ProcessInput(0, sample, 0);
                }
                catch (SharpGen.Runtime.SharpGenException retryEx) when ((uint)retryEx.HResult == MF_E_NOTACCEPTING)
                {
                    return;
                }
            }

            PullDecodedFrame();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[H264 Decoder] Error: {ex.Message}");
        }
    }

    private void PullDecodedFrame()
    {
        if (_decoder == null || _renderTexture == null) return;

        while (true)
        {
            using var outSample = MediaFactory.MFCreateSample();
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            outSample.AddBuffer(outBuffer);

            var outputDataBuffer = new OutputDataBuffer
            {
                Sample = outSample
            };

            var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputDataBuffer, out _);
            if (result.Failure)
            {
                // MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72 (-1072870030)
                if ((uint)result.Code != 0xC00D6D72)
                {
                    Console.WriteLine($"[Decoder ProcessOutput] Code: 0x{(uint)result.Code:X8}");
                }
                break;
            }
            if (outputDataBuffer.Sample == null)
            {
                Console.WriteLine("[Decoder ProcessOutput] Sample is null");
                break;
            }

            using var sample = outputDataBuffer.Sample;
            using var buffer = sample.ConvertToContiguousBuffer();

            IntPtr pData;
            buffer.Lock(out pData, out _, out int currentLength);
            if (currentLength >= _width * _height)
            {
                ConvertNv12ToBgra(pData, _bgraBuffer);
                buffer.Unlock();

                _context.UpdateSubresource(_bgraBuffer, _renderTexture, 0, (uint)(_width * 4));

                OnFrameDecoded?.Invoke(_renderTexture);
            }
            else
            {
                buffer.Unlock();
                break;
            }
        }
    }

    private void ConvertNv12ToBgra(IntPtr pNv12, byte[] bgraDst)
    {
        int width = _width;
        int height = _height;

        unsafe
        {
            byte* pYBase = (byte*)pNv12;
            byte* pUVBase = pYBase + (width * height);

            fixed (byte* pBgraFixed = bgraDst)
            {
                long yPtr = (long)pYBase;
                long uvPtr = (long)pUVBase;
                long bgraPtr = (long)pBgraFixed;

                Parallel.For(0, height, y =>
                {
                    byte* pY = (byte*)yPtr;
                    byte* pUV = (byte*)uvPtr;
                    byte* pBgra = (byte*)bgraPtr;

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
                });
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderSrv?.Dispose();
        _renderTexture?.Dispose();
        _decoder?.Dispose();
        MediaFactory.MFShutdown();
    }
}
