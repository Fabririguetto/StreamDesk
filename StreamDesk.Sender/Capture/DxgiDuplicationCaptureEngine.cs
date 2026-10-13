using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamDesk.Sender.Capture;

/// <summary>
/// Motor de captura de escritorio alternativo basado en DXGI Desktop Duplication API (IDXGIOutputDuplication).
/// Fallback nativo D3D11 para GPUs discretas dedicadas.
/// </summary>
public class DxgiDuplicationCaptureEngine : ICaptureEngine
{
    private readonly ID3D11Device _d3d11Device;
    private readonly ID3D11DeviceContext _d3d11Context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _currentFrameTexture;
    private Thread? _captureThread;
    private volatile bool _isRunning;
    private bool _disposed;

    public event Action<ID3D11Texture2D, long>? OnFrameCaptured;
    public bool IsRunning => _isRunning;

    public DxgiDuplicationCaptureEngine(ID3D11Device device, ID3D11DeviceContext context)
    {
        _d3d11Device = device;
        _d3d11Context = context;
    }

    public void Start(IntPtr hMonitor, int width, int height)
    {
        if (_isRunning) return;

        InitializeDuplication();

        _isRunning = true;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = "DXGI Desktop Capture Thread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private void InitializeDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;

        using var dxgiDevice = _d3d11Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        if (adapter.EnumOutputs(0, out IDXGIOutput? output).Failure || output == null)
        {
            throw new InvalidOperationException("No se pudo enumerar la salida DXGI primaria para Desktop Duplication.");
        }

        using (output)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        {
            _duplication = output1.DuplicateOutput(_d3d11Device);
        }
    }

    private void CaptureLoop()
    {
        long targetIntervalTicks = Stopwatch.Frequency / 60; // 60 FPS (~16.6ms)
        long nextTick = Stopwatch.GetTimestamp();

        while (_isRunning && !_disposed)
        {
            if (_duplication == null)
            {
                try { InitializeDuplication(); }
                catch { Thread.Sleep(100); continue; }
            }

            var duplication = _duplication;
            if (duplication == null) continue;

            try
            {
                // Intentar capturar nuevo frame con timeout corto (5 ms)
                var result = duplication.AcquireNextFrame(5, out var frameInfo, out var desktopResource);

                if (result.Success && desktopResource != null)
                {
                    using (desktopResource)
                    using (var texture = desktopResource.QueryInterface<ID3D11Texture2D>())
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
                    duplication.ReleaseFrame();
                }
                else if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                {
                    _duplication?.Dispose();
                    _duplication = null;
                    continue;
                }

                // Control de cadencia a 60 FPS ininterrumpido
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick)
                {
                    nextTick = now + targetIntervalTicks;

                    if (_currentFrameTexture != null)
                    {
                        OnFrameCaptured?.Invoke(_currentFrameTexture, now);
                    }
                }
                else
                {
                    int sleepMs = (int)((nextTick - now) * 1000 / Stopwatch.Frequency);
                    if (sleepMs > 1) Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                if (!_isRunning) break;
                Console.WriteLine($"[DXGI Duplication] Error en ciclo de captura: {ex.Message}");
                Thread.Sleep(16);
            }
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _captureThread?.Join(500);
        _captureThread = null;

        _duplication?.Dispose();
        _duplication = null;
        _currentFrameTexture?.Dispose();
        _currentFrameTexture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
