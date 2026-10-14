using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StreamDesk.Monitor.Rendering;

/// <summary>
/// Renderizador DirectX 11 de ultra-baja latencia con presentación inmediata (Present 0, 0)
/// y escalado por hardware DirectX 11 para la visualización del monitor remoto.
/// </summary>
public class DirectX11MonitorRenderer : IDisposable
{
    private readonly IntPtr _hwnd;
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID3D11Texture2D? _videoTexture;
    private bool _disposed;

    private uint _currentWidth;
    private uint _currentHeight;

    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _context;

    public DirectX11MonitorRenderer(IntPtr hwnd, int initialWidth = 1920, int initialHeight = 1080)
    {
        _hwnd = hwnd;
        _currentWidth = (uint)Math.Max(initialWidth, 1);
        _currentHeight = (uint)Math.Max(initialHeight, 1);

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
            Width = _currentWidth,
            Height = _currentHeight,
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

    public void RenderFrame(IntPtr pData, int length, int width, int height, int stride)
    {
        if (_disposed || pData == IntPtr.Zero || width <= 0 || height <= 0) return;

        // Si la resolución del video cambió, recrear buffers
        if (_videoTexture == null || _currentWidth != (uint)width || _currentHeight != (uint)height)
        {
            _currentWidth = (uint)width;
            _currentHeight = (uint)height;

            _videoTexture?.Dispose();
            _renderTargetView?.Dispose();
            _renderTargetView = null;

            var texDesc = new Texture2DDescription
            {
                Width = _currentWidth,
                Height = _currentHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None
            };
            _videoTexture = _device.CreateTexture2D(texDesc);

            _swapChain.ResizeBuffers(2, _currentWidth, _currentHeight, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateRenderTarget();
        }

        // Subir datos RGB32 directo a la textura de video GPU
        _context.UpdateSubresource(_videoTexture, 0, null, pData, (uint)stride, 0);

        // Copiar al backbuffer de la SwapChain
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.CopyResource(backBuffer, _videoTexture);

        // Presentación inmediata (0, 0) para cero latencia añadida
        _swapChain.Present(0, PresentFlags.None);
    }

    public void ClearScreen(Color4 color)
    {
        if (_disposed || _renderTargetView == null) return;
        _context.ClearRenderTargetView(_renderTargetView, color);
        _swapChain.Present(0, PresentFlags.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _videoTexture?.Dispose();
        _renderTargetView?.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
