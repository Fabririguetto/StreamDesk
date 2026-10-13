using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace StreamDesk.Sender.Capture;

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGraphicsCaptureItemInterop
{
    [PreserveSig]
    int CreateForWindow([In] IntPtr hWnd, [In] ref Guid riid, out IntPtr result);

    [PreserveSig]
    int CreateForMonitor([In] IntPtr hMonitor, [In] ref Guid riid, out IntPtr result);
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDirect3DDxgiInterfaceAccess
{
    [PreserveSig]
    int GetInterface([In] ref Guid iid, out IntPtr pInterface);
}

public static class CaptureInterop
{
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(className, className.Length, out IntPtr hstring);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            Guid interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hstring, ref interopGuid, out IntPtr factoryPtr);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            Guid itemGuid = typeof(GraphicsCaptureItem).GUID;
            hr = interop.CreateForMonitor(hMonitor, ref itemGuid, out IntPtr itemPtr);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            var item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr pUnknown = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid dxgiAccessGuid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            int hr = Marshal.QueryInterface(pUnknown, ref dxgiAccessGuid, out IntPtr pDxgiAccess);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            var dxgiAccess = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(pDxgiAccess);
            Marshal.Release(pDxgiAccess);

            Guid textureGuid = typeof(ID3D11Texture2D).GUID;
            hr = dxgiAccess.GetInterface(ref textureGuid, out IntPtr pTexture);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            // Crear wrapper ID3D11Texture2D de Vortice a partir del puntero COM
            return new ID3D11Texture2D(pTexture);
        }
        finally
        {
            Marshal.Release(pUnknown);
        }
    }
}
