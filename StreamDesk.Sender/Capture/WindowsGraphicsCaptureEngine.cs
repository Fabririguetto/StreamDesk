using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace StreamDesk.Sender.Capture;

/// <summary>
/// Motor de captura de ultra-baja latencia basado en la API moderna Windows.Graphics.Capture.
/// Diseñado para laptops y arquitecturas de GPU híbrida (Intel/AMD iGPU + NVIDIA dGPU) sin copia a CPU.
/// </summary>
public class WindowsGraphicsCaptureEngine : ICaptureEngine
{
    private readonly ID3D11Device _d3d11Device;
    private readonly ID3D11DeviceContext _d3d11Context;
    private readonly IDirect3DDevice _winRtDevice;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _captureItem;
    private bool _isRunning;
    private bool _disposed;

    public event Action<ID3D11Texture2D, long>? OnFrameCaptured;
    public bool IsRunning => _isRunning;

    public WindowsGraphicsCaptureEngine(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winRtDevice)
    {
        _d3d11Device = device;
        _d3d11Context = context;
        _winRtDevice = winRtDevice;
    }

    public void Start(IntPtr hMonitor, int width, int height)
    {
        if (_isRunning) return;

        _captureItem = CaptureInterop.CreateItemForMonitor(hMonitor);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            new SizeInt32 { Width = width, Height = height });

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = true;
        _session.StartCapture();

        _isRunning = true;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_isRunning || _disposed) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            // Extraer la textura ID3D11Texture2D directamente desde VRAM
            using var texture = CaptureInterop.GetTextureFromSurface(frame.Surface);
            if (texture != null)
            {
                OnFrameCaptured?.Invoke(texture, frame.SystemRelativeTime.Ticks);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WGC Engine] Error al procesar frame: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        if (_framePool != null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
        _captureItem = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
