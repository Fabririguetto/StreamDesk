using System.Diagnostics;
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
    private ID3D11Texture2D? _currentFrameTexture;
    private Thread? _emitThread;
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    private int _targetFps = 120;
    private volatile bool _isRunning;
    private bool _disposed;

    public event Action<ID3D11Texture2D, long>? OnFrameCaptured;
    public bool IsRunning => _isRunning;

    public WindowsGraphicsCaptureEngine(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winRtDevice)
    {
        _d3d11Device = device;
        _d3d11Context = context;
        _winRtDevice = winRtDevice;
    }

    public void Start(IntPtr hMonitor, int width, int height, int fps = 120, bool captureCursor = true)
    {
        if (_isRunning) return;

        _targetFps = fps > 0 ? fps : 120;
        try { timeBeginPeriod(1); } catch { }

        _captureItem = CaptureInterop.CreateItemForMonitor(hMonitor);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            new SizeInt32 { Width = width, Height = height });

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = captureCursor;
        _session.StartCapture();

        _isRunning = true;
        _emitThread = new Thread(EmitLoop)
        {
            Name = $"WGC {_targetFps} FPS Emit Thread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _emitThread.Start();
    }

    private readonly object _syncLock = new();

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
                lock (_syncLock)
                {
                    if (_currentFrameTexture == null)
                    {
                        var desc = texture.Description;
                        desc.BindFlags = BindFlags.ShaderResource;
                        desc.Usage = ResourceUsage.Default;
                        desc.CPUAccessFlags = CpuAccessFlags.None;
                        _currentFrameTexture = _d3d11Device.CreateTexture2D(desc);
                    }
                    _d3d11Context.CopyResource(_currentFrameTexture, texture);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WGC Engine] Error al procesar frame: {ex.Message}");
        }
    }

    private void EmitLoop()
    {
        long targetIntervalTicks = Stopwatch.Frequency / _targetFps;
        long nextTick = Stopwatch.GetTimestamp();

        while (_isRunning && !_disposed)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick)
                {
                    // Evitar atraso acumulado si hubo pausas momentáneas
                    if (now - nextTick > targetIntervalTicks * 2)
                    {
                        nextTick = now + targetIntervalTicks;
                    }
                    else
                    {
                        nextTick += targetIntervalTicks;
                    }

                    ID3D11Texture2D? textureToEmit = null;
                    lock (_syncLock)
                    {
                        textureToEmit = _currentFrameTexture;
                    }

                    if (textureToEmit != null)
                    {
                        OnFrameCaptured?.Invoke(textureToEmit, now);
                    }
                }
                else
                {
                    int remainingMs = (int)((nextTick - now) * 1000 / Stopwatch.Frequency);
                    if (remainingMs > 2)
                    {
                        Thread.Sleep(remainingMs - 1);
                    }
                    else
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WGC EmitLoop] Error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        try { timeEndPeriod(1); } catch { }

        _emitThread?.Join(500);
        _emitThread = null;

        if (_framePool != null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
        _captureItem = null;

        lock (_syncLock)
        {
            _currentFrameTexture?.Dispose();
            _currentFrameTexture = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
