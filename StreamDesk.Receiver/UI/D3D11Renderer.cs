using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamDesk.Receiver.UI;

/// <summary>
/// Motor de renderizado DirectX 11 de ultra baja latencia con SwapChain en modo FLIP_DISCARD (SyncInterval = 0).
/// </summary>
public class D3D11Renderer : IDisposable
{
    private readonly IntPtr _hwnd;
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private bool _disposed;

    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _context;

    public D3D11Renderer(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;

        var creationFlags = DeviceCreationFlags.BgraSupport;
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            creationFlags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device,
            out _context).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var swapChainDesc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, swapChainDesc);
        CreateRenderTarget();
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);
    }

    public void Resize(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0) return;

        _renderTargetView?.Dispose();
        _renderTargetView = null;

        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateRenderTarget();
    }

    public void RenderFrame(ID3D11Texture2D? sourceTexture)
    {
        if (_disposed || _renderTargetView == null) return;

        if (sourceTexture != null)
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.CopyResource(backBuffer, sourceTexture);
        }
        else
        {
            // Fondo oscuro mientras espera señal
            _context.ClearRenderTargetView(_renderTargetView, new Vortice.Mathematics.Color4(0.08f, 0.08f, 0.10f, 1.0f));
        }

        // Present(0, 0): Presentación inmediata sin bloqueo artificial de VSync para latencia mínima
        _swapChain.Present(0, PresentFlags.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderTargetView?.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
