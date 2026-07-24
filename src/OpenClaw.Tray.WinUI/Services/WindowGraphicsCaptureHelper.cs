using System;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClawTray.Services;

/// <summary>
/// One-shot, picker-free Windows.Graphics.Capture (WGC) capture of a single
/// HWND. Reuses <see cref="GraphicsCaptureInterop"/> for the D3D11/WinRT
/// device and <see cref="GraphicsCaptureItem"/> activation boilerplate
/// instead of standing up a second device stack.
///
/// This never shows the system GraphicsCapturePicker UI and never calls
/// RequestAccessAsync, a capability declaration, or any other
/// consent/authorization API: <see cref="GraphicsCaptureInterop.CreateCaptureItemForWindow"/>
/// activates the capture item directly for the given HWND via
/// IGraphicsCaptureItemInterop.CreateForWindow, which requires no user
/// prompt. It is intended for app-window screenshot proof capture, not
/// screen recording.
/// </summary>
internal static class WindowGraphicsCaptureHelper
{
    /// <summary>Result of one single-frame window capture attempt.</summary>
    internal sealed class CaptureOutcome
    {
        public bool Success { get; private init; }
        public byte[]? PngBytes { get; private init; }
        public int Width { get; private init; }
        public int Height { get; private init; }

        /// <summary>Method-specific diagnostic detail for private test output only; never surfaced in redacted proof artifacts.</summary>
        public string? FailureKind { get; private init; }
        public int? HResult { get; private init; }
        public string? FailureMessage { get; private init; }

        public static CaptureOutcome Succeeded(byte[] pngBytes, int width, int height) => new()
        {
            Success = true,
            PngBytes = pngBytes,
            Width = width,
            Height = height,
        };

        public static CaptureOutcome Failed(string kind, int? hResult, string message) => new()
        {
            Success = false,
            FailureKind = kind,
            HResult = hResult,
            FailureMessage = message,
        };
    }

    /// <summary>
    /// True when the current host reports WGC support at all. This is a
    /// capability query only (Windows 10 1803+), not a consent check. Lets
    /// exceptions propagate so callers can capture the exact API evidence
    /// instead of a silently-swallowed false.
    /// </summary>
    internal static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    /// <summary>
    /// Captures exactly one frame of the given HWND, bounded by
    /// <paramref name="timeout"/>, and encodes it to PNG bytes. Returns a
    /// failed <see cref="CaptureOutcome"/> (never throws) so callers can
    /// collect method-specific exception type/HResult/message for private
    /// diagnostics without letting a WGC-specific failure surface in
    /// redacted, publishable proof output.
    /// </summary>
    internal static CaptureOutcome TryCaptureWindow(IntPtr hwnd, TimeSpan timeout)
    {
        bool supported;
        try
        {
            supported = IsSupported();
        }
        catch (Exception ex)
        {
            // Distinct from a clean "false" result: an exception here is
            // exact API evidence (type/HResult/message) that the capability
            // query itself failed, not just that WGC is unsupported.
            return CaptureOutcome.Failed(ex.GetType().Name, ex.HResult, ex.Message);
        }

        if (!supported)
            return CaptureOutcome.Failed("NotSupported", null, "GraphicsCaptureSession.IsSupported() returned false on this host.");

        GraphicsCaptureItem item;
        try
        {
            item = GraphicsCaptureInterop.CreateCaptureItemForWindow(hwnd);
        }
        catch (Exception ex)
        {
            return CaptureOutcome.Failed(ex.GetType().Name, ex.HResult, ex.Message);
        }

        global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? d3d = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        Direct3D11CaptureFrame? capturedFrame = null;
        using var frameReady = new SemaphoreSlim(0, 1);

        void OnFrameArrived(Direct3D11CaptureFramePool p, object _)
        {
            var frame = p.TryGetNextFrame();
            if (frame == null)
                return;
            if (Interlocked.CompareExchange(ref capturedFrame, frame, null) != null)
                frame.Dispose();
            // slopwatch-ignore: SW003 Best-effort signal; a race here cannot corrupt the already-captured frame.
            try { frameReady.Release(); } catch { }
        }

        try
        {
            d3d = GraphicsCaptureInterop.CreateDirect3DDevice();
            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return CaptureOutcome.Failed("InvalidWindowSize", null, $"GraphicsCaptureItem reported a non-positive size ({size.Width}x{size.Height}).");

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            pool.FrameArrived += OnFrameArrived;

            session.StartCapture();
            if (!frameReady.Wait(timeout))
                return CaptureOutcome.Failed("Timeout", null, $"No frame arrived from Windows.Graphics.Capture within {timeout.TotalMilliseconds:0}ms.");

            using var frame = Interlocked.Exchange(ref capturedFrame, null);
            if (frame == null)
                return CaptureOutcome.Failed("NoFrame", null, "The frame pool signaled readiness but produced no frame.");

            using var softwareBitmap = SoftwareBitmap
                .CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            var pngBytes = EncodeToPng(softwareBitmap);
            return CaptureOutcome.Succeeded(pngBytes, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
        }
        catch (Exception ex)
        {
            return CaptureOutcome.Failed(ex.GetType().Name, ex.HResult, ex.Message);
        }
        finally
        {
            if (pool != null)
                pool.FrameArrived -= OnFrameArrived;
            // Deterministic teardown order: session before pool, then the
            // D3D device, matching ScreenRecordingService's cleanup order.
            session?.Dispose();
            pool?.Dispose();
            (d3d as IDisposable)?.Dispose();
            capturedFrame?.Dispose();
        }
    }

    private static byte[] EncodeToPng(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        var size = (uint)stream.Size;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        reader.LoadAsync(size).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
