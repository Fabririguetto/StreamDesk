using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace StreamDesk.Monitor.Capture;

/// <summary>
/// Lector de fotogramas de ultra-baja latencia para capturadoras de video y cámaras usando Media Foundation.
/// </summary>
public class MediaFoundationDeviceReader : IDisposable
{
    private readonly VideoCaptureDeviceInfo _deviceInfo;
    private readonly VideoFormatInfo? _targetFormat;
    private IMFMediaSource? _mediaSource;
    private IMFSourceReader? _sourceReader;
    private Thread? _captureThread;
    private volatile bool _isRunning;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _stride;

    public int Width => _width;
    public int Height => _height;
    public bool IsRunning => _isRunning;

    public event Action<IntPtr, int, int, int, int>? OnFrameArrived; // (pData, length, width, height, stride)

    public MediaFoundationDeviceReader(VideoCaptureDeviceInfo deviceInfo, VideoFormatInfo? targetFormat = null)
    {
        _deviceInfo = deviceInfo;
        _targetFormat = targetFormat;
    }

    public void Start()
    {
        if (_isRunning) return;

        MediaFactory.MFStartup(true);

        _mediaSource = _deviceInfo.Activate.ActivateObject<IMFMediaSource>();
        if (_mediaSource == null)
        {
            throw new InvalidOperationException($"No se pudo activar la fuente de video para '{_deviceInfo.Name}'.");
        }

        // Configurar SourceReader para procesamiento avanzado de video
        using var readerAttributes = MediaFactory.MFCreateAttributes(1);
        readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1);

        _sourceReader = MediaFactory.MFCreateSourceReaderFromMediaSource(_mediaSource, readerAttributes);

        // Seleccionar formato de entrada preferido si se especificó
        if (_targetFormat != null)
        {
            for (int i = 0; ; i++)
            {
                IMFMediaType? nativeType = null;
                try
                {
                    nativeType = _sourceReader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i);
                }
                catch
                {
                    break;
                }

                if (nativeType == null) break;

                using (nativeType)
                {
                    MediaFactory.MFGetAttributeSize(nativeType, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
                    if (w == _targetFormat.Width && h == _targetFormat.Height)
                    {
                        var subtype = nativeType.GetGUID(MediaTypeAttributeKeys.Subtype);
                        if (subtype == _targetFormat.Subtype)
                        {
                            _sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, nativeType);
                            break;
                        }
                    }
                }
            }
        }

        // Configurar formato de salida a RGB32 (B8G8R8A8) para subida directa a DirectX 11
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        _sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

        // Obtener resolución y stride final de salida
        var currentType = _sourceReader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(currentType, MediaTypeAttributeKeys.FrameSize, out uint outWidth, out uint outHeight);
        _width = (int)outWidth;
        _height = (int)outHeight;

        if (currentType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint dStride).Success && dStride > 0)
        {
            _stride = (int)dStride;
        }
        else
        {
            _stride = _width * 4;
        }

        currentType.Dispose();

        _isRunning = true;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = "Video Capture Device Thread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        while (_isRunning && !_disposed && _sourceReader != null)
        {
            try
            {
                var sample = _sourceReader.ReadSample(
                    SourceReaderIndex.FirstVideoStream,
                    SourceReaderControlFlag.None,
                    out int actualStreamIndex,
                    out SourceReaderFlag streamFlags,
                    out long timestamp);

                if (sample != null)
                {
                    using (sample)
                    using (var buffer = sample.ConvertToContiguousBuffer())
                    {
                        buffer.Lock(out IntPtr pData, out _, out int length);
                        if (pData != IntPtr.Zero && length > 0)
                        {
                            OnFrameArrived?.Invoke(pData, length, _width, _height, _stride);
                        }
                        buffer.Unlock();
                    }
                }
                else if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                if (!_isRunning) break;
                Console.WriteLine($"[Capture Reader] Error leyendo fotograma: {ex.Message}");
                Thread.Sleep(10);
            }
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        _captureThread?.Join(500);
        _captureThread = null;

        _sourceReader?.Dispose();
        _sourceReader = null;
        _mediaSource?.Dispose();
        _mediaSource = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
