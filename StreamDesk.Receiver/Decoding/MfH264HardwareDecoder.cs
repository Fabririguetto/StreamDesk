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
    private int _width;
    private int _height;
    private int _currentStride;
    private byte[] _bgraBuffer;
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
                try
                {
                    var curType = candidate.GetOutputCurrentType(0);
                    if (curType != null && curType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint dStride).Success && dStride > 0)
                    {
                        _currentStride = (int)dStride;
                    }
                }
                catch { }
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
                // MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61
                if ((uint)result.Code == 0xC00D6D61)
                {
                    HandleStreamChange();
                    continue;
                }

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

            using var buffer = outSample.ConvertToContiguousBuffer();

            IntPtr pData;
            buffer.Lock(out pData, out _, out int currentLength);
            if (currentLength >= _width * _height)
            {
                ConvertNv12ToBgra(pData, currentLength, _bgraBuffer);
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

    private void HandleStreamChange()
    {
        if (_decoder == null) return;

        try
        {
            var outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            _decoder.SetOutputType(0, outputType, 0);

            var currentType = _decoder.GetOutputCurrentType(0);
            if (currentType != null)
            {
                if (currentType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint dStride).Success && dStride > 0)
                {
                    _currentStride = (int)dStride;
                }

                MediaFactory.MFGetAttributeSize(currentType, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
                if (w > 0 && h > 0 && ((int)w != _width || (int)h != _height))
                {
                    Console.WriteLine($"[Decoder] Cambio dinámico de resolución detectado en stream: {w}x{h}");
                    _width = (int)w;
                    _height = (int)h;
                    _bgraBuffer = new byte[_width * _height * 4];

                    _renderSrv?.Dispose();
                    _renderTexture?.Dispose();

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
                }
            }

            try
            {
                var streamInfo = _decoder.GetOutputStreamInfo(0);
                _outputBufferSize = Math.Max(streamInfo.Size, 4 * 1024 * 1024);
            }
            catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Decoder Stream Change] Error: {ex.Message}");
        }
    }

    private void ConvertNv12ToBgra(IntPtr pNv12, int bufferLength, byte[] bgraDst)
    {
        int width = _width;
        int height = _height;
        int stride = _currentStride > 0 ? _currentStride : width;

        // En Media Foundation, la altura asignada del búfer NV12 se alinea a macrobloques
        // de 16 líneas (ej. 1080p se asigna en un búfer de altura 1088).
        // Calculamos la altura real asignada del plano Y para que pUVBase apunte
        // exactamente al inicio del plano UV y no a las líneas de relleno negro de Y.
        int allocatedHeight = height;
        if (stride > 0 && bufferLength >= stride * height * 3 / 2)
        {
            allocatedHeight = (int)((bufferLength * 2L) / (stride * 3L));
        }

        unsafe
        {
            byte* pYBase = (byte*)pNv12;
            byte* pUVBase = pYBase + (stride * allocatedHeight);

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

                    byte* yRow = pY + (y * stride);
                    byte* uvRow = pUV + ((y / 2) * stride);
                    byte* bgraRow = pBgra + (y * width * 4);

                    for (int x = 0; x < width; x += 2)
                    {
                        int uVal = uvRow[x] - 128;
                        int vVal = uvRow[x + 1] - 128;

                        int rChroma = 409 * vVal + 128;
                        int gChroma = -100 * uVal - 208 * vVal + 128;
                        int bChroma = 516 * uVal + 128;

                        // Pixel 0
                        int y0 = yRow[x] - 16;
                        if (y0 < 0) y0 = 0;
                        int c0 = 298 * y0;
                        int r0 = (c0 + rChroma) >> 8;
                        int g0 = (c0 + gChroma) >> 8;
                        int b0 = (c0 + bChroma) >> 8;

                        int dst0 = x * 4;
                        bgraRow[dst0 + 0] = (byte)(b0 < 0 ? 0 : (b0 > 255 ? 255 : b0));
                        bgraRow[dst0 + 1] = (byte)(g0 < 0 ? 0 : (g0 > 255 ? 255 : g0));
                        bgraRow[dst0 + 2] = (byte)(r0 < 0 ? 0 : (r0 > 255 ? 255 : r0));
                        bgraRow[dst0 + 3] = 255;

                        // Pixel 1
                        int y1 = yRow[x + 1] - 16;
                        if (y1 < 0) y1 = 0;
                        int c1 = 298 * y1;
                        int r1 = (c1 + rChroma) >> 8;
                        int g1 = (c1 + gChroma) >> 8;
                        int b1 = (c1 + bChroma) >> 8;

                        int dst1 = (x + 1) * 4;
                        bgraRow[dst1 + 0] = (byte)(b1 < 0 ? 0 : (b1 > 255 ? 255 : b1));
                        bgraRow[dst1 + 1] = (byte)(g1 < 0 ? 0 : (g1 > 255 ? 255 : g1));
                        bgraRow[dst1 + 2] = (byte)(r1 < 0 ? 0 : (r1 > 255 ? 255 : r1));
                        bgraRow[dst1 + 3] = 255;
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
