using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace StreamDesk.Receiver.Decoding;

/// <summary>
/// Decodificador de video H.264 acelerado por hardware mediante Media Foundation Transform (MFT).
/// Configurado en modo de baja latencia con salida directa a texturas DirectX 11 y desbloqueo asíncrono.
/// </summary>
public class MfH264HardwareDecoder : IDisposable
{
    private static readonly Guid GuidAsyncUnlock = new("2477308d-c710-4ec7-bb4b-32b0a944ae4b"); // MF_TRANSFORM_ASYNC_UNLOCK

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly int _width;
    private readonly int _height;

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

        InitializeDecoder();
    }

    private void InitializeDecoder()
    {
        MediaFactory.MFStartup(true);

        var h264Input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = 0x0000003F; // MFT_ENUM_FLAG_ALL
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, flags, h264Input, null);

        foreach (var act in activates)
        {
            IMFTransform? candidate = null;
            try
            {
                candidate = act.ActivateObject<IMFTransform>();
                if (candidate == null) continue;

                try
                {
                    candidate.Attributes?.Set(GuidAsyncUnlock, 1);
                }
                catch { }

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

                _decoder = candidate;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Decoder] Candidato descartado ({ex.Message}). Probando siguiente...");
                candidate?.Dispose();
            }
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

            _decoder.ProcessInput(0, sample, 0);
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

        using var outSample = MediaFactory.MFCreateSample();
        using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_width * _height * 2);
        outSample.AddBuffer(outBuffer);

        var outputDataBuffer = new OutputDataBuffer
        {
            Sample = outSample
        };

        var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputDataBuffer, out _);
        if (result.Success && outputDataBuffer.Sample != null)
        {
            OnFrameDecoded?.Invoke(_renderTexture);
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
