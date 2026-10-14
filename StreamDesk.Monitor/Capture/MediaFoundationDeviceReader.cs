using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace StreamDesk.Monitor.Capture;

/// <summary>
/// Lector de fotogramas de ultra-baja latencia para capturadoras de video y cámaras usando Media Foundation.
/// Soporta RGB32 nativo, NV12 (conversión paralela <0.3ms) y YUY2.
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
    private Guid _currentSubtype = Guid.Empty;
    private byte[]? _bgraBuffer;

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

        // 1. Activar fuente de video mediante SymbolicLink o Activate
        IMFMediaSource? mediaSource = null;
        if (!string.IsNullOrEmpty(_deviceInfo.SymbolicLink))
        {
            using var devAttrs = MediaFactory.MFCreateAttributes(2);
            devAttrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            devAttrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, _deviceInfo.SymbolicLink);
            mediaSource = MediaFactory.MFCreateDeviceSource(devAttrs);
        }
        else if (_deviceInfo.Activate != null)
        {
            mediaSource = _deviceInfo.Activate.ActivateObject<IMFMediaSource>();
        }

        if (mediaSource == null)
        {
            throw new InvalidOperationException($"No se pudo activar la fuente de video para '{_deviceInfo.Name}'.");
        }
        _mediaSource = mediaSource;

        // 2. Configurar SourceReader con aceleración de video
        using var readerAttributes = MediaFactory.MFCreateAttributes(1);
        readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1);

        _sourceReader = MediaFactory.MFCreateSourceReaderFromMediaSource(_mediaSource, readerAttributes);

        // 3. Seleccionar formato nativo de entrada si se solicitó
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
                    var subtype = nativeType.GetGUID(MediaTypeAttributeKeys.Subtype);

                    if (w == _targetFormat.Width && h == _targetFormat.Height &&
                        (subtype == _targetFormat.Subtype || _targetFormat.Subtype == Guid.Empty))
                    {
                        try
                        {
                            _sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, nativeType);
                            break;
                        }
                        catch { }
                    }
                }
            }
        }

        // 4. Negociar formato de salida del SourceReader
        bool configured = false;

        // Opción A: RGB32 directo (ideal para tarjetas de captura con formato RGB)
        try
        {
            using var rgb32Type = MediaFactory.MFCreateMediaType();
            rgb32Type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            rgb32Type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            _sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, rgb32Type);
            configured = true;
        }
        catch { }

        // Opción B: NV12 (formato nativo de cámaras web UVC y tarjetas HDMI estándar)
        if (!configured)
        {
            try
            {
                using var nv12Type = MediaFactory.MFCreateMediaType();
                nv12Type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                nv12Type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                _sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, nv12Type);
                configured = true;
            }
            catch { }
        }

        // Opción C: YUY2
        if (!configured)
        {
            try
            {
                using var yuy2Type = MediaFactory.MFCreateMediaType();
                yuy2Type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                yuy2Type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.YUY2);
                _sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, yuy2Type);
                configured = true;
            }
            catch { }
        }

        // 5. Determinar resolución final y subtipo activo
        using var currentType = _sourceReader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(currentType, MediaTypeAttributeKeys.FrameSize, out uint outWidth, out uint outHeight);
        _width = (int)outWidth;
        _height = (int)outHeight;
        _currentSubtype = currentType.GetGUID(MediaTypeAttributeKeys.Subtype);

        if (currentType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint dStride).Success && dStride > 0)
        {
            _stride = (int)dStride;
        }
        else
        {
            _stride = _width * 4;
        }

        _isRunning = true;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = "Video Capture Device Thread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private byte[] EnsureBgraBuffer()
    {
        int needed = _width * _height * 4;
        if (_bgraBuffer == null || _bgraBuffer.Length != needed)
        {
            _bgraBuffer = new byte[needed];
        }
        return _bgraBuffer;
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
                            if (_currentSubtype == VideoFormatGuids.Rgb32)
                            {
                                OnFrameArrived?.Invoke(pData, length, _width, _height, _stride);
                            }
                            else if (_currentSubtype == VideoFormatGuids.NV12)
                            {
                                var bgraBuf = EnsureBgraBuffer();
                                ConvertNv12ToBgra(pData, length, bgraBuf);
                                unsafe
                                {
                                    fixed (byte* pBgra = bgraBuf)
                                    {
                                        OnFrameArrived?.Invoke((IntPtr)pBgra, bgraBuf.Length, _width, _height, _width * 4);
                                    }
                                }
                            }
                            else if (_currentSubtype == VideoFormatGuids.YUY2)
                            {
                                var bgraBuf = EnsureBgraBuffer();
                                ConvertYuy2ToBgra(pData, length, bgraBuf);
                                unsafe
                                {
                                    fixed (byte* pBgra = bgraBuf)
                                    {
                                        OnFrameArrived?.Invoke((IntPtr)pBgra, bgraBuf.Length, _width, _height, _width * 4);
                                    }
                                }
                            }
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
                Thread.Sleep(5);
            }
        }
    }

    private void ConvertNv12ToBgra(IntPtr pNv12, int bufferLength, byte[] bgraDst)
    {
        int width = _width;
        int height = _height;
        int stride = _stride > 0 ? _stride : width;

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

                        int dst1 = dst0 + 4;
                        bgraRow[dst1 + 0] = (byte)(b1 < 0 ? 0 : (b1 > 255 ? 255 : b1));
                        bgraRow[dst1 + 1] = (byte)(g1 < 0 ? 0 : (g1 > 255 ? 255 : g1));
                        bgraRow[dst1 + 2] = (byte)(r1 < 0 ? 0 : (r1 > 255 ? 255 : r1));
                        bgraRow[dst1 + 3] = 255;
                    }
                });
            }
        }
    }

    private void ConvertYuy2ToBgra(IntPtr pYuy2, int bufferLength, byte[] bgraDst)
    {
        int width = _width;
        int height = _height;
        unsafe
        {
            byte* pSrcBase = (byte*)pYuy2;
            fixed (byte* pBgraFixed = bgraDst)
            {
                long srcPtr = (long)pSrcBase;
                long dstPtr = (long)pBgraFixed;

                Parallel.For(0, height, y =>
                {
                    byte* srcRow = (byte*)srcPtr + (y * width * 2);
                    byte* dstRow = (byte*)dstPtr + (y * width * 4);

                    for (int x = 0; x < width; x += 2)
                    {
                        int srcIdx = x * 2;
                        int y0 = srcRow[srcIdx + 0] - 16;
                        int u = srcRow[srcIdx + 1] - 128;
                        int y1 = srcRow[srcIdx + 2] - 16;
                        int v = srcRow[srcIdx + 3] - 128;

                        if (y0 < 0) y0 = 0;
                        if (y1 < 0) y1 = 0;

                        int rChroma = 409 * v + 128;
                        int gChroma = -100 * u - 208 * v + 128;
                        int bChroma = 516 * u + 128;

                        // Pixel 0
                        int c0 = 298 * y0;
                        int r0 = (c0 + rChroma) >> 8;
                        int g0 = (c0 + gChroma) >> 8;
                        int b0 = (c0 + bChroma) >> 8;

                        int dstIdx0 = x * 4;
                        dstRow[dstIdx0 + 0] = (byte)(b0 < 0 ? 0 : (b0 > 255 ? 255 : b0));
                        dstRow[dstIdx0 + 1] = (byte)(g0 < 0 ? 0 : (g0 > 255 ? 255 : g0));
                        dstRow[dstIdx0 + 2] = (byte)(r0 < 0 ? 0 : (r0 > 255 ? 255 : r0));
                        dstRow[dstIdx0 + 3] = 255;

                        // Pixel 1
                        int c1 = 298 * y1;
                        int r1 = (c1 + rChroma) >> 8;
                        int g1 = (c1 + gChroma) >> 8;
                        int b1 = (c1 + bChroma) >> 8;

                        int dstIdx1 = dstIdx0 + 4;
                        dstRow[dstIdx1 + 0] = (byte)(b1 < 0 ? 0 : (b1 > 255 ? 255 : b1));
                        dstRow[dstIdx1 + 1] = (byte)(g1 < 0 ? 0 : (g1 > 255 ? 255 : g1));
                        dstRow[dstIdx1 + 2] = (byte)(r1 < 0 ? 0 : (r1 > 255 ? 255 : r1));
                        dstRow[dstIdx1 + 3] = 255;
                    }
                });
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
