using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace OpenClawTray.Services;

/// <summary>
/// Shared Windows.Graphics.Capture (WGC) D3D11/WinRT interop used by both the
/// monitor-recording pipeline (<see cref="ScreenRecordingService"/>) and the
/// picker-free single-window capture helper (<see cref="WindowGraphicsCaptureHelper"/>).
/// Kept in one place so neither caller stands up its own D3D device stack.
/// </summary>
internal static class GraphicsCaptureInterop
{
    // IID_IDXGIDevice
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // IID_IInspectable: the standard WinRT interop IID used to marshal an
    // activated runtime class back into a managed projection. Kept for the
    // monitor path, which already works in production (screen recording);
    // left unchanged to avoid regressing that behavior.
    private static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    // IID_IGraphicsCaptureItem: the official WinRT interface IID for
    // Windows.Graphics.Capture.GraphicsCaptureItem, required by
    // IGraphicsCaptureItemInterop.CreateForWindow so the returned pointer is
    // queried for the real capture-item interface rather than the generic
    // IInspectable base.
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // D3D_DRIVER_TYPE_HARDWARE
    private const uint DriverTypeHardware = 1;

    // D3D_DRIVER_TYPE_WARP (software rasterizer fallback)
    private const uint DriverTypeWarp = 5;

    // DXGI_ERROR_UNSUPPORTED
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);

    internal static IDirect3DDevice CreateDirect3DDevice()
    {
        try
        {
            return CreateDirect3DDeviceCore(DriverTypeHardware);
        }
        catch (COMException ex) when (ex.HResult == DxgiErrorUnsupported)
        {
            // Some virtualized/RDP hosts have no hardware D3D adapter that
            // supports capture; WARP is a safe, always-available software
            // fallback for the same interop path.
            return CreateDirect3DDeviceCore(DriverTypeWarp);
        }
    }

    private static IDirect3DDevice CreateDirect3DDeviceCore(uint driverType)
    {
        // D3D11_CREATE_DEVICE_BGRA_SUPPORT=0x20, D3D11_SDK_VERSION=7
        var hr = D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out var d3dPtr, IntPtr.Zero, IntPtr.Zero);
        Marshal.ThrowExceptionForHR(hr);
        if (d3dPtr == IntPtr.Zero)
            throw new InvalidOperationException("D3D11 device creation returned a null device.");

        var iid = IidDxgiDevice;
        hr = Marshal.QueryInterface(d3dPtr, in iid, out var dxgiPtr);
        Marshal.Release(d3dPtr);
        Marshal.ThrowExceptionForHR(hr);
        if (dxgiPtr == IntPtr.Zero)
            throw new InvalidOperationException("D3D11 device did not expose IDXGIDevice.");

        hr = NativeCreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var winrtPtr);
        Marshal.Release(dxgiPtr);
        Marshal.ThrowExceptionForHR(hr);
        if (winrtPtr == IntPtr.Zero)
            throw new InvalidOperationException("WinRT Direct3D device creation returned a null device.");

        var device = MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);
        Marshal.Release(winrtPtr);
        return device;
    }

    internal static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hMonitor) =>
        CreateCaptureItem(factory =>
        {
            factory.CreateForMonitor(hMonitor, in IidInspectable, out var itemPtr);
            return itemPtr;
        });

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> directly for one HWND via
    /// <see cref="IGraphicsCaptureItemInterop.CreateForWindow"/>. This is the
    /// picker-free WGC app-window capture path: it never shows the
    /// GraphicsCapturePicker UI and never calls RequestAccessAsync/consent or
    /// capability APIs, which are the user-facing screen-recording consent
    /// flow and out of scope here. Uses the official
    /// IID_IGraphicsCaptureItem (79C3F95B-31F7-4EC2-A464-632EF5D30760)
    /// rather than IID_IInspectable, matching the documented
    /// CreateForWindow contract.
    /// </summary>
    internal static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr hwnd) =>
        CreateCaptureItem(factory =>
        {
            factory.CreateForWindow(hwnd, in IidGraphicsCaptureItem, out var itemPtr);
            return itemPtr;
        });

    private static GraphicsCaptureItem CreateCaptureItem(Func<IGraphicsCaptureItemInterop, IntPtr> create)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var iid = typeof(IGraphicsCaptureItemInterop).GUID;

        var hr = WindowsCreateString(classId, classId.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hr);
        if (hstring == IntPtr.Zero)
            throw new InvalidOperationException("GraphicsCaptureItem activation string was null.");

        try
        {
            hr = RoGetActivationFactory(hstring, ref iid, out var factoryPtr);
            Marshal.ThrowExceptionForHR(hr);
            if (factoryPtr == IntPtr.Zero)
                throw new InvalidOperationException("GraphicsCaptureItem activation factory was null.");

            var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            var itemPtr = create(factory);
            if (itemPtr == IntPtr.Zero)
                throw new InvalidOperationException("GraphicsCaptureItem creation returned a null item.");

            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    // P/Invoke declarations

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int NativeCreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr runtimeClassId, ref Guid iid, out IntPtr factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr hwnd, in Guid riid, out IntPtr ppv);
        void CreateForMonitor(IntPtr hMonitor, in Guid riid, out IntPtr ppv);
    }
}
