using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace StreamDesk.Sender.Capture;

public static class Direct3D11DeviceFactory
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static (ID3D11Device Device, ID3D11DeviceContext Context, IDirect3DDevice WinRtDevice) CreateDevice()
    {
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
#if DEBUG
        flags |= DeviceCreationFlags.Debug;
#endif

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (result.Failure || device == null || context == null)
        {
            // Fallback si Debug flag falla en máquinas sin SDK Graphics Tools
            flags &= ~DeviceCreationFlags.Debug;
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                flags,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out device,
                out context).CheckError();
        }

        // Habilitar soporte multihilo para DirectX
        using var multithread = device!.QueryInterfaceOrNull<ID3D11Multithread>();
        multithread?.SetMultithreadProtected(true);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pWinRtDevice);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        IDirect3DDevice winRtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pWinRtDevice);
        Marshal.Release(pWinRtDevice);

        return (device, context!, winRtDevice);
    }
}
