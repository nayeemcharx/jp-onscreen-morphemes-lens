using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using JPLens.Interfaces;
using JPLens.Models;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace JPLens.Services;

/// <summary>
/// Captures the primary monitor using the Windows.Graphics.Capture (WGC) API.
///
/// WGC reads frames directly from the GPU compositor's output surface, giving:
///   • Correct pixels for DirectX / hardware-accelerated / exclusive-fullscreen apps
///     that GDI CopyFromScreen cannot see.
///   • Clean BGRA pixels with no ClearType colour-fringing — GDI's BitBlt path
///     leaves RGB subpixel artefacts that blur character edges in grayscale space
///     and degrade OCR accuracy.
///   • Correct alpha values (255 for every opaque pixel) — the compositor writes
///     them, unlike GDI which always leaves alpha = 0 in a Format32bppArgb target.
///
/// The D3D11 device is created once on first use and reused for every subsequent
/// capture to avoid the per-call device-creation overhead.
///
/// Threading:
///   CapturePrimaryMonitor() is called from a thread-pool thread (Task.Run in
///   AppController), so blocking on async WinRT operations via GetAwaiter().GetResult()
///   is safe — no WPF dispatcher thread to deadlock.
/// </summary>
public sealed class WgcScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly ILogger<WgcScreenCaptureService> _logger;
    private IDirect3DDevice? _d3dDevice;
    private bool             _disposed;

    // ── IGraphicsCaptureItemInterop ───────────────────────────────────────────
    // This COM activation-factory interface is the only way to create a
    // GraphicsCaptureItem from an HMONITOR.  It is not projected into the WinRT
    // C# API; it must be accessed via the activation factory.
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow (IntPtr hwnd,     in Guid iid, out IntPtr ppv);
        [PreserveSig] int CreateForMonitor(IntPtr hmonitor, in Guid iid, out IntPtr ppv);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = false, ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    // Creates an HSTRING from a managed string (WinRT string primitive).
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    // Frees an HSTRING.
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    // Retrieves a WinRT activation factory for the named class and QIs for
    // the requested interface (identified by iid).
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, // HSTRING
        in Guid iid,
        out IntPtr factory);

    // Creates a hardware D3D11 device.
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice",
        SetLastError = false, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        IntPtr  pAdapter,
        int     driverType,            // D3D_DRIVER_TYPE_HARDWARE = 1
        IntPtr  software,
        uint    flags,                 // D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20
        IntPtr  pFeatureLevels,
        uint    featureLevels,
        uint    sdkVersion,            // D3D11_SDK_VERSION = 7
        out IntPtr ppDevice,
        IntPtr  pFeatureLevel,
        out IntPtr ppImmediateContext);

    // Wraps an IDXGIDevice as a WinRT IDirect3DDevice (IInspectable).
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = false, CharSet = CharSet.Unicode,
        ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // IDXGIDevice IID
    private static readonly Guid s_dxgiDeviceGuid =
        new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // IGraphicsCaptureItem WinRT default-interface IID
    // This is the iid argument passed to CreateForMonitor so the factory
    // returns a pointer to IGraphicsCaptureItem, which the CLR can then
    // unwrap as a GraphicsCaptureItem via Marshal.GetObjectForIUnknown.
    private static readonly Guid s_captureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // ── Constructor ───────────────────────────────────────────────────────────

    public WgcScreenCaptureService(ILogger<WgcScreenCaptureService> logger)
        => _logger = logger;

    // ── IScreenCaptureService ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public ScreenFrame CapturePrimaryMonitor()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException(
                "Windows.Graphics.Capture is not supported on this device / OS version.");

        var monitor = MonitorService.GetPrimaryMonitor();

        _logger.LogDebug(
            "WGC: capturing monitor {W}×{H} at ({X},{Y}), scale={S:F2}",
            monitor.Width, monitor.Height, monitor.X, monitor.Y, monitor.ScaleFactor);

        _d3dDevice ??= CreateD3D11Device();

        // GetPrimaryMonitor already computed the monitor centre; using it
        // with MONITOR_DEFAULTTONEAREST gives the same HMONITOR the OS uses.
        var hmonitor = MonitorFromPoint(
            monitor.X + monitor.Width  / 2,
            monitor.Y + monitor.Height / 2,
            dwFlags: 2u /* MONITOR_DEFAULTTONEAREST */);

        var item = CreateCaptureItem(hmonitor);

        // CreateFreeThreaded (Build 17763+) delivers FrameArrived on any MTA thread
        // without requiring a WinRT DispatcherQueue.  The regular Create() variant
        // routes the event through a DispatcherQueue, which doesn't exist on a plain
        // .NET thread-pool thread (Task.Run), causing the event to never fire and the
        // capture to time out.
        // Use the primary monitor's known pixel dimensions rather than item.Size.
        // On multi-monitor setups item.Size can reflect the combined virtual-desktop
        // size, causing the frame pool to allocate a surface wide enough to cover all
        // monitors and deliver pixels from secondary monitors alongside the primary.
        var primarySize = new Windows.Graphics.SizeInt32(monitor.Width, monitor.Height);
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3dDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 1,
            primarySize);

        using var session = framePool.CreateCaptureSession(item);

        // Suppress cursor from appearing in captured pixels.
        // Available since WGC v1 (Windows 10 1803 / Build 17134).
        session.IsCursorCaptureEnabled = false;

        // Suppress the yellow "you are being captured" border (Build 20348+ only).
        // Use reflection because the property is not present in the 19041 SDK headers.
        typeof(GraphicsCaptureSession)
            .GetProperty("IsBorderRequired")
            ?.SetValue(session, false);

        Direct3D11CaptureFrame? capturedFrame = null;
        using var frameReady = new ManualResetEventSlim(false);
        int frameReceived = 0;

        framePool.FrameArrived += (pool, _) =>
        {
            if (Interlocked.Exchange(ref frameReceived, 1) == 0)
            {
                // First frame — keep it.
                capturedFrame = pool.TryGetNextFrame();
            }
            else
            {
                // Subsequent frames (shouldn't happen with numberOfBuffers=1, but
                // guard defensively) — discard immediately to avoid buffer stalls.
                pool.TryGetNextFrame()?.Dispose();
            }
            frameReady.Set();
        };

        session.StartCapture();

        // WGC delivers the first frame within one vsync period (~16 ms at 60 Hz).
        // 500 ms is a conservative upper bound.
        if (!frameReady.Wait(TimeSpan.FromMilliseconds(500)))
            throw new TimeoutException("WGC did not deliver a frame within 500 ms.");

        using (capturedFrame)
        {
            if (capturedFrame is null)
                throw new InvalidOperationException("WGC: TryGetNextFrame returned null.");

            // frame.ContentSize is the actual monitor content within the D3D surface.
            // The surface itself may be larger due to GPU texture-alignment padding;
            // those extra rows/columns contain stale pixels from previous frames or
            // adjacent-monitor GPU memory — exactly the garbage text the OCR sees.
            var contentSize = capturedFrame.ContentSize;
            // Clamp to the primary monitor's physical dimensions: ContentSize may
            // still exceed the monitor bounds when the compositor delivers a surface
            // that spans multiple monitors (alignment or virtual-desktop artefact).
            int contentW    = Math.Min(contentSize.Width,  monitor.Width);
            int contentH    = Math.Min(contentSize.Height, monitor.Height);

            if (contentSize.Width != monitor.Width || contentSize.Height != monitor.Height)
                _logger.LogDebug(
                    "WGC: surface {SW}×{SH} → content {CW}×{CH} (clamped to primary monitor)",
                    contentSize.Width, contentSize.Height, contentW, contentH);

            var bitmap = ConvertFrameToBitmap(capturedFrame, contentW, contentH);

            _logger.LogDebug("WGC: captured {W}×{H} bitmap", bitmap.Width, bitmap.Height);

            // Use the WGC-reported content dimensions for Width/Height so that the
            // bitmap pixel dimensions always match what ScreenFrame advertises.
            return new ScreenFrame(
                image:       bitmap,
                width:       contentW,
                height:      contentH,
                monitorX:    monitor.X,
                monitorY:    monitor.Y,
                scaleFactor: monitor.ScaleFactor,
                capturedAt:  DateTime.UtcNow);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed  = true;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a hardware D3D11 device and wraps it as a WinRT
    /// <see cref="IDirect3DDevice"/> for use with <see cref="Direct3D11CaptureFramePool"/>.
    /// </summary>
    private static IDirect3DDevice CreateD3D11Device()
    {
        const int  D3D_DRIVER_TYPE_HARDWARE      = 1;
        const uint D3D11_CREATE_DEVICE_BGRA      = 0x20u;
        const uint D3D11_SDK_VERSION             = 7u;

        int hr = D3D11CreateDevice(
            pAdapter:           IntPtr.Zero,
            driverType:         D3D_DRIVER_TYPE_HARDWARE,
            software:           IntPtr.Zero,
            flags:              D3D11_CREATE_DEVICE_BGRA,
            pFeatureLevels:     IntPtr.Zero,
            featureLevels:      0u,
            sdkVersion:         D3D11_SDK_VERSION,
            ppDevice:           out IntPtr d3dDevicePtr,
            pFeatureLevel:      IntPtr.Zero,
            ppImmediateContext: out IntPtr contextPtr);

        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // QI: ID3D11Device → IDXGIDevice
            var dxgiGuid = s_dxgiDeviceGuid;
            hr = Marshal.QueryInterface(d3dDevicePtr, ref dxgiGuid, out IntPtr dxgiDevicePtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                // IDXGIDevice → WinRT IInspectable (IDirect3DDevice)
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out IntPtr winrtDevicePtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    // Must use CsWinRT's own unwrapping API, NOT Marshal.GetObjectForIUnknown.
                    // GetObjectForIUnknown always produces a legacy-RCW System.__ComObject which
                    // cannot be marshaled back to ABI by CsWinRT (fails with InvalidCastException
                    // when passed to Direct3D11CaptureFramePool.Create).
                    // MarshalInspectable<T>.FromAbi goes through CsWinRT's ComWrappers and
                    // produces the correct projected IDirect3DDevice instance.
                    return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(winrtDevicePtr);
                }
                finally
                {
                    Marshal.Release(winrtDevicePtr);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevicePtr);
            }
        }
        finally
        {
            Marshal.Release(d3dDevicePtr);
            if (contextPtr != IntPtr.Zero)
                Marshal.Release(contextPtr);
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> targeting the given HMONITOR
    /// via the <c>IGraphicsCaptureItemInterop</c> activation-factory interface.
    /// </summary>
    private static GraphicsCaptureItem CreateCaptureItem(IntPtr hmonitor)
    {
        // RoGetActivationFactory replaces the .NET 8-removed
        // WindowsRuntimeMarshal.GetActivationFactory.
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop

        int hr = WindowsCreateString(className, className.Length, out IntPtr hstring);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            hr = RoGetActivationFactory(hstring, in interopGuid, out IntPtr factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);

                hr = interop.CreateForMonitor(hmonitor, in s_captureItemGuid, out IntPtr itemPtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    // Marshal.GetObjectForIUnknown always uses the legacy COM RCW path
                    // and returns System.__ComObject — it never goes through CsWinRT's
                    // ComWrappers, so the cast to a WinRT runtime class always fails.
                    // WinRT.MarshalInspectable<T>.FromAbi is CsWinRT's own API for
                    // wrapping a raw IUnknown/IInspectable* into the correct projection.
                    return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// Converts a <see cref="Direct3D11CaptureFrame"/> to a GDI+ <see cref="Bitmap"/>
    /// by copying pixel data through a <see cref="SoftwareBitmap"/>.
    ///
    /// Pixel-format alignment:
    ///   WGC frame pool: <c>B8G8R8A8UIntNormalized</c> (BGRA8, byte order B·G·R·A).
    ///   SoftwareBitmap: <c>BitmapPixelFormat.Bgra8</c>   (same byte order).
    ///   GDI Format32bppArgb                               (same byte order in memory).
    ///   No byte-swapping or colour conversion is required.
    ///
    /// <c>BitmapAlphaMode.Ignore</c> ensures every alpha byte in the SoftwareBitmap
    /// is set to 255 (fully opaque), matching the expectation of downstream code
    /// that treats the bitmap as an opaque screen snapshot.
    /// </summary>
    private static Bitmap ConvertFrameToBitmap(Direct3D11CaptureFrame frame, int contentWidth, int contentHeight)
    {
        // GPU surface → SoftwareBitmap (CPU-readable pixels).
        // The SoftwareBitmap dimensions match the full D3D surface, which is padded
        // to GPU tile-alignment boundaries and will be LARGER than contentWidth×contentHeight.
        // We copy pixel-row-by-row, using only the first contentWidth columns per row,
        // discarding the alignment padding that would otherwise contain stale/foreign pixels.
        var softBitmap = SoftwareBitmap
            .CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore)
            .AsTask().GetAwaiter().GetResult();

        using (softBitmap)
        {
            int    surfaceWidth = softBitmap.PixelWidth;
            int    surfaceHeight = softBitmap.PixelHeight;
            int    bufferSize   = surfaceWidth * surfaceHeight * 4;
            byte[] pixels       = new byte[bufferSize];

            // CopyToBuffer writes BGRA8 pixels into the byte array.
            softBitmap.CopyToBuffer(pixels.AsBuffer());

            var bitmap  = new Bitmap(contentWidth, contentHeight, PixelFormat.Format32bppArgb);
            var bmpData = bitmap.LockBits(
                new Rectangle(0, 0, contentWidth, contentHeight),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int srcRowBytes  = surfaceWidth  * 4; // full surface row stride
                int destRowBytes = contentWidth  * 4; // only content columns

                for (int y = 0; y < contentHeight; y++)
                {
                    int    srcOffset = y * srcRowBytes;
                    IntPtr destRow   = bmpData.Scan0 + y * bmpData.Stride;
                    Marshal.Copy(pixels, srcOffset, destRow, destRowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            return bitmap;
        }
    }
}
