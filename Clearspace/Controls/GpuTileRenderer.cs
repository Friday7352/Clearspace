// Clearspace | GPU renderer for the disk usage map.
// Immutable tile vertices stay in Direct3D 9Ex buffers. Camera-only frames change
// a projection matrix; only new detail layers upload geometry. At most two
// layers are retained for transitions, with 4x MSAA and a shared D3DImage surface.
// WPF labels/overlays use the same camera. The CPU rasterizer remains a fallback.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Clearspace.Controls;

internal sealed unsafe class GpuTileRenderer : IDisposable
{
    // ---------------------------------------------------------------- Direct3D 9 constants
    private const uint SdkVersion = 32;                 // D3D_SDK_VERSION
    private const uint DeviceTypeHal = 1;               // D3DDEVTYPE_HAL
    private const uint CreateFpuPreserve = 0x2, CreateMultithreaded = 0x4,
        CreateSoftwareVertexProcessing = 0x20, CreateHardwareVertexProcessing = 0x40;
    private const uint FormatA8R8G8B8 = 21;             // D3DFMT_A8R8G8B8
    private const uint Multisample4 = 4;                // D3DMULTISAMPLE_4_SAMPLES
    // CHANGED (round 30): multisampling is back on. Turning it off was the wrong call: the seams
    // between blocks are now deliberately sub-pixel, and without coverage sampling a 0.4 px seam
    // either snaps to a whole pixel or disappears - which is the jaggedness on small tiles and the
    // borders that vanish between two of them. The software rasterizer computes fractional edge
    // coverage, which is exactly why it looked softer. The cost of the resolve is real but small
    // next to the per-frame surface copy D3DImage does anyway; the adapter fix below is the one
    // that matters for speed.
    private static readonly bool Multisampling = true;
    private const uint SwapEffectDiscard = 1;
    private const uint PresentIntervalImmediate = 0x80000000;
    private const uint ClearTarget = 1;                 // D3DCLEAR_TARGET
    private const uint PrimitiveTriangleList = 4;       // D3DPT_TRIANGLELIST
    private const uint FvfXyz = 0x002, FvfDiffuse = 0x040;
    private const uint FilterNone = 0;                  // D3DTEXF_NONE
    // Render states (D3DRENDERSTATETYPE) and values.
    private const uint RsZEnable = 7, RsSrcBlend = 19, RsDestBlend = 20, RsCullMode = 22, RsAlphaBlendEnable = 27,
        RsLighting = 137, RsMultisampleAntialias = 161, RsTextureFactor = 60;
    private const uint BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, CullNone = 1;
    // Texture stage states: output the vertex color only.
    private const uint TssColorOp = 1, TssColorArg1 = 2, TssAlphaOp = 4, TssAlphaArg1 = 5, TssAlphaArg2 = 6;

    // Vtable slots (IUnknown = 0..2). Checked against d3d9.h.
    private const int SlotRelease = 2;
    private const int SlotGetAdapterCount = 4, SlotGetAdapterIdentifier = 5, SlotGetAdapterMonitor = 15; // IDirect3D9
    private const int SlotCreateDeviceEx = 20;         // IDirect3D9Ex
    private const int SlotCreateVertexBuffer = 26, SlotCreateRenderTarget = 28, SlotStretchRect = 34, SlotSetRenderTarget = 37,
        SlotBeginScene = 41, SlotEndScene = 42, SlotClear = 43, SlotSetTransform = 44, SlotSetRenderState = 57,
        SlotSetTextureStageState = 67, SlotDrawPrimitive = 81, SlotSetFVF = 89, SlotSetStreamSource = 100;

    [DllImport("d3d9.dll")]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr direct3D);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct PresentParameters
    {
        public uint BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount;
        public uint MultiSampleType, MultiSampleQuality, SwapEffect;
        public IntPtr DeviceWindow;
        public int Windowed, EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat, Flags, FullScreenRefreshRateInHz, PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y, Z;
        public uint Color; // D3DCOLOR, ARGB
    }

    private IntPtr _direct3D;
    private IntPtr _device;
    private IntPtr _shared;      // single-sample surface shown by D3DImage
    private IntPtr _multisample; // 4x MSAA render target resolved into _shared (0 if unsupported)
    private int _width, _height;
    private bool _failed;
    private bool _targetsInvalid;
    private bool _frameLocked;
    private readonly Dictionary<TileGeometry, IntPtr> _geometry = [];
    private readonly List<TileGeometry> _obsolete = [];
    internal int GeometryUploads { get; private set; }
    internal double LastUploadMilliseconds { get; private set; }
    internal int CachedGeometryCount => _geometry.Count;
    internal long CachedGeometryBytes => _geometry.Keys.Sum(g => (long)g.Commands.Length * 6 * sizeof(Vertex));

    // NEW (round 18): which adapter actually got the device, so the settings panel can tell the user
    // whether their blocks are being drawn by a real GPU, an integrated part, or a software adapter.
    public string AdapterName { get; private set; } = "Direct3D";

    public D3DImage Image { get; } = new();
    public bool IsAvailable => !_failed && _device != IntPtr.Zero && Image.IsFrontBufferAvailable;
    public bool IsMultisampled => _multisample != IntPtr.Zero;

    private GpuTileRenderer(IntPtr direct3D, IntPtr device)
    {
        _direct3D = direct3D;
        _device = device;
        // The front buffer can go away (lock screen, remote desktop, driver reset); rebuild the
        // surfaces the next time it is available.
        Image.IsFrontBufferAvailableChanged += (_, _) => _targetsInvalid = true;
    }

    public static GpuTileRenderer? TryCreate(IntPtr window = default)
    {
        try
        {
            if (Direct3DCreate9Ex(SdkVersion, out var direct3D) < 0 || direct3D == IntPtr.Zero) return null;
            // NEW (round 30): create the device on the adapter driving the monitor this window is on.
            // Taking adapter 0 blindly is the classic D3DImage performance trap: if WPF composes on a
            // different adapter than the surface lives on, every frame is copied between them through
            // system memory, and the software rasterizer wins.
            var adapter = AdapterFor(direct3D, window);
            var parameters = new PresentParameters
            {
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1,
                SwapEffect = SwapEffectDiscard,
                DeviceWindow = window != IntPtr.Zero ? window : GetDesktopWindow(),
                Windowed = 1,
                PresentationInterval = PresentIntervalImmediate
            };
            var createDevice = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, uint, PresentParameters*, IntPtr, IntPtr*, int>)
                Slot(direct3D, SlotCreateDeviceEx);
            IntPtr device;
            var result = createDevice(direct3D, adapter, DeviceTypeHal, parameters.DeviceWindow,
                CreateHardwareVertexProcessing | CreateMultithreaded | CreateFpuPreserve, &parameters, IntPtr.Zero, &device);
            if (result < 0)
                result = createDevice(direct3D, adapter, DeviceTypeHal, parameters.DeviceWindow,
                    CreateSoftwareVertexProcessing | CreateMultithreaded | CreateFpuPreserve, &parameters, IntPtr.Zero, &device);
            if (result < 0 || device == IntPtr.Zero)
            {
                Release(direct3D);
                return null;
            }
            return new GpuTileRenderer(direct3D, device) { AdapterName = Describe(direct3D, adapter) };
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: GPU renderer unavailable, using the CPU. {exception.Message}");
            return null;
        }
    }

    // D3DADAPTER_IDENTIFIER9: Driver[512], Description[512], DeviceName[32], then DriverVersion.
    // Only the description is wanted, and a failure here must never cost the renderer.
    // The adapter whose monitor shows this window, so WPF and Direct3D share one GPU.
    private static uint AdapterFor(IntPtr direct3D, IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero) return 0;
            var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor == IntPtr.Zero) return 0;
            var count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(direct3D, SlotGetAdapterCount))(direct3D);
            var monitorOf = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr>)Slot(direct3D, SlotGetAdapterMonitor);
            for (uint adapter = 0; adapter < count; adapter++)
                if (monitorOf(direct3D, adapter) == monitor) return adapter;
            return 0;
        }
        catch { return 0; }
    }

    private static string Describe(IntPtr direct3D, uint adapter)
    {
        try
        {
            var identifier = stackalloc byte[1104];
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, byte*, int>)Slot(direct3D, SlotGetAdapterIdentifier);
            if (get(direct3D, adapter, 0, identifier) < 0) return "Direct3D";
            var name = Marshal.PtrToStringAnsi((IntPtr)(identifier + 512))?.Trim();
            return string.IsNullOrEmpty(name) ? "Direct3D" : name;
        }
        catch { return "Direct3D"; }
    }

    // Reserve the shared surface before preparing any WPF labels or hit targets.
    // A busy compositor defers the whole frame, not just its colored rectangles.
    internal bool TryBeginFrame()
    {
        if (_frameLocked) throw new InvalidOperationException("A GPU frame is already active.");
        if (!IsAvailable) return false;
        // CHANGED (round 31): a zero wait meant any contention cost a whole frame. Called from the
        // rendering callback the surface is normally free, so this rarely waits at all - but a couple
        // of milliseconds is far cheaper than dropping to the next vsync.
        var acquired = Image.TryLock(new Duration(TimeSpan.FromMilliseconds(3)));
        if (!acquired) { Image.Unlock(); return false; }
        _frameLocked = true;
        return true;
    }

    internal void EndFrame()
    {
        if (!_frameLocked) return;
        _frameLocked = false;
        Image.Unlock();
    }

    // Convenience entry point for uncached callers and basic renderer tests.
    public bool Render(ReadOnlySpan<DiskUsageTreemap.TileCommand> commands, int width, int height,
        double scaleX, double scaleY, uint background, out bool busy)
        => RenderCached([new TileBatch(new TileGeometry(commands.ToArray()), 1, 0, 0, 1)],
            width, height, scaleX, scaleY, background, out busy);

    public bool RenderCached(ReadOnlySpan<TileBatch> batches, int width, int height,
        double scaleX, double scaleY, uint background, out bool busy)
    {
        if (batches.Length > 2) throw new ArgumentOutOfRangeException(nameof(batches));
        busy = false;
        if (!IsAvailable) return false;
        // The compositor may still own the last frame. Keep it visible and retry
        // next frame instead of waiting on the UI thread or switching renderers.
        // Unlike WriteableBitmap, D3DImage increments its lock count even when
        // TryLock returns false. Every completed attempt must reach Unlock.
        // https://source.dot.net/PresentationCore/System/Windows/InterOp/D3DImage.cs.html
        var ownsFrame = !_frameLocked;
        if (ownsFrame && !TryBeginFrame()) { busy = true; return false; }
        try
        {
            if (!EnsureTargets(width, height)) return false;
            var uploadStart = Stopwatch.GetTimestamp();
            TrimGeometry(batches);
            foreach (ref readonly var batch in batches)
                // CHANGED (round 32): a scene of a million blocks needs a vertex buffer the driver may
                // decline. That is a reason to fall back for this frame, not to give up on the GPU for
                // the rest of the session, so it is not treated as a device failure.
                if (!UploadGeometry(batch.Geometry)) return false;
            LastUploadMilliseconds = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
            var target = _multisample != IntPtr.Zero ? _multisample : _shared;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Slot(_device, SlotSetRenderTarget))(_device, 0, target);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, float, uint, int>)Slot(_device, SlotClear))(
                _device, 0, IntPtr.Zero, ClearTarget, 0xFF000000 | background, 0f, 0);
            if (((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_device, SlotBeginScene))(_device) < 0) return Fail();

            var setState = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Slot(_device, SlotSetRenderState);
            setState(_device, RsZEnable, 0);
            setState(_device, RsLighting, 0);
            setState(_device, RsCullMode, CullNone);
            setState(_device, RsAlphaBlendEnable, 1);
            setState(_device, RsSrcBlend, BlendSrcAlpha);
            setState(_device, RsDestBlend, BlendInvSrcAlpha);
            setState(_device, RsMultisampleAntialias, _multisample != IntPtr.Zero ? 1u : 0u);
            var setStage = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int>)Slot(_device, SlotSetTextureStageState);
            setStage(_device, 0, TssColorOp, 2); // SELECTARG1: diffuse color
            setStage(_device, 0, TssColorArg1, 0);
            setStage(_device, 0, TssAlphaOp, 4); // MODULATE: vertex alpha * layer opacity
            setStage(_device, 0, TssAlphaArg1, 0);
            setStage(_device, 0, TssAlphaArg2, 3); // TFACTOR
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(_device, SlotSetFVF))(_device, FvfXyz | FvfDiffuse);
            var transform = (delegate* unmanaged[Stdcall]<IntPtr, uint, Matrix4x4*, int>)Slot(_device, SlotSetTransform);
            var identity = Matrix4x4.Identity;
            transform(_device, 256, &identity); // world
            transform(_device, 2, &identity);   // view
            var draw = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int>)Slot(_device, SlotDrawPrimitive);
            foreach (ref readonly var batch in batches)
            {
                if (batch.Opacity <= 0 || batch.Geometry.Commands.Length == 0) continue;
                var projection = Projection(batch, width, height, scaleX, scaleY);
                transform(_device, 3, &projection);
                setState(_device, RsTextureFactor, (uint)Math.Round(Math.Clamp(batch.Opacity, 0, 1) * 255) << 24 | 0xFFFFFF);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, int>)Slot(_device, SlotSetStreamSource))(
                    _device, 0, _geometry[batch.Geometry], 0, (uint)sizeof(Vertex));
                // Keep primitive counts below the limits of older adapters.
                for (var first = 0; first < batch.Geometry.Commands.Length; first += 8192)
                {
                    var count = Math.Min(8192, batch.Geometry.Commands.Length - first);
                    if (draw(_device, PrimitiveTriangleList, (uint)(first * 6), (uint)(count * 2)) < 0)
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_device, SlotEndScene))(_device);
                        return Fail();
                    }
                }
            }

            if (((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_device, SlotEndScene))(_device) < 0) return Fail();
            if (_multisample != IntPtr.Zero)
            {
                // Resolve the antialiased image into the surface WPF displays.
                var resolve = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int>)Slot(_device, SlotStretchRect))(
                    _device, _multisample, IntPtr.Zero, _shared, IntPtr.Zero, FilterNone);
                if (resolve < 0) return Fail();
            }
            Image.AddDirtyRect(new Int32Rect(0, 0, width, height));
            return true;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: GPU frame failed, switching to the CPU. {exception.Message}");
            return Fail();
        }
        finally
        {
            if (ownsFrame) EndFrame();
        }
    }

    internal static Matrix4x4 Projection(TileBatch batch, int width, int height, double dpiX, double dpiY)
    {
        var matrix = Matrix4x4.Identity;
        matrix.M11 = (float)(2 * batch.Scale * dpiX / width);
        matrix.M22 = (float)(-2 * batch.Scale * dpiY / height);
        matrix.M41 = (float)(2 * (batch.X * dpiX - .5) / width - 1);
        matrix.M42 = (float)(1 - 2 * (batch.Y * dpiY - .5) / height);
        return matrix;
    }

    private void TrimGeometry(ReadOnlySpan<TileBatch> batches)
    {
        _obsolete.Clear();
        foreach (var geometry in _geometry.Keys)
        {
            var keep = false;
            foreach (ref readonly var batch in batches)
                if (ReferenceEquals(geometry, batch.Geometry)) { keep = true; break; }
            if (!keep) _obsolete.Add(geometry);
        }
        foreach (var geometry in _obsolete) { Release(_geometry[geometry]); _geometry.Remove(geometry); }
        _obsolete.Clear();
    }

    private bool UploadGeometry(TileGeometry geometry)
    {
        if (_geometry.ContainsKey(geometry)) return true;
        if (geometry.Commands.Length == 0) { _geometry.Add(geometry, IntPtr.Zero); return true; }
        var bytes = checked((uint)(geometry.Commands.Length * 6 * sizeof(Vertex)));
        var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr*, IntPtr, int>)Slot(_device, SlotCreateVertexBuffer);
        IntPtr buffer;
        if (create(_device, bytes, 8, FvfXyz | FvfDiffuse, 0, &buffer, IntPtr.Zero) < 0) return false;
        void* data;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void**, uint, int>)Slot(buffer, 11))(buffer, 0, bytes, &data, 0) < 0)
        { Release(buffer); return false; }
        var vertices = (Vertex*)data;
        foreach (ref readonly var command in geometry.Commands.AsSpan())
        {
            var color = (uint)command.Alpha << 24 | command.Color;
            vertices[0] = new Vertex { X = command.X0, Y = command.Y0, Color = color };
            vertices[1] = new Vertex { X = command.X1, Y = command.Y0, Color = color };
            vertices[2] = new Vertex { X = command.X0, Y = command.Y1, Color = color };
            vertices[3] = new Vertex { X = command.X1, Y = command.Y0, Color = color };
            vertices[4] = new Vertex { X = command.X1, Y = command.Y1, Color = color };
            vertices[5] = new Vertex { X = command.X0, Y = command.Y1, Color = color };
            vertices += 6;
        }
        if (((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 12))(buffer) < 0) { Release(buffer); return false; }
        _geometry.Add(geometry, buffer);
        GeometryUploads++;
        return true;
    }

    private bool EnsureTargets(int width, int height)
    {
        // Called only while Render owns the image lock.
        if (!_targetsInvalid && _shared != IntPtr.Zero && width == _width && height == _height) return true;
        ReleaseTargets(imageLocked: true);
        _targetsInvalid = false;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int, IntPtr*, IntPtr*, int>)
            Slot(_device, SlotCreateRenderTarget);
        IntPtr shared, handle = IntPtr.Zero;
        // A shareable surface is what D3DImage expects from a Direct3D 9Ex device.
        if (create(_device, (uint)width, (uint)height, FormatA8R8G8B8, 0, 0, 0, &shared, &handle) < 0 || shared == IntPtr.Zero)
            return Fail();
        IntPtr multisample = IntPtr.Zero;
        if (Multisampling && create(_device, (uint)width, (uint)height, FormatA8R8G8B8, Multisample4, 0, 0, &multisample, null) < 0)
            multisample = IntPtr.Zero; // no MSAA on this adapter: draw straight into the shared surface
        _shared = shared;
        _multisample = multisample;
        _width = width;
        _height = height;
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _shared);
        return true;
    }

    // Test-only readback: verify actual GPU pixels, not just successful HRESULTs.
    // Production animation never reads a surface back to the CPU.
    internal uint ReadPixelForTest(int x, int y)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height) throw new ArgumentOutOfRangeException(nameof(x));
        IntPtr surface = IntPtr.Zero;
        try
        {
            var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr*, IntPtr, int>)Slot(_device, 36);
            Marshal.ThrowExceptionForHR(create(_device, (uint)_width, (uint)_height, FormatA8R8G8B8, 2, &surface, IntPtr.Zero));
            var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(_device, 32);
            Marshal.ThrowExceptionForHR(copy(_device, _shared, surface));
            LockedRect locked;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)Slot(surface, 13))(
                surface, &locked, IntPtr.Zero, 0x10));
            try { return *(uint*)((byte*)locked.Bits + y * locked.Pitch + x * sizeof(uint)); }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(surface, 14))(surface); }
        }
        finally { Release(surface); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LockedRect { public int Pitch; public IntPtr Bits; }

    private void ReleaseTargets(bool imageLocked = false)
    {
        if (_shared != IntPtr.Zero && Image.Dispatcher.CheckAccess())
        {
            if (!imageLocked) Image.Lock();
            try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
            finally { if (!imageLocked) Image.Unlock(); }
        }
        Release(_multisample);
        Release(_shared);
        _multisample = _shared = IntPtr.Zero;
        _width = _height = 0;
    }

    private bool Fail()
    {
        _failed = true;
        return false;
    }

    // Function pointers convert from void*, so vtable slots are read as void*.
    private static void* Slot(IntPtr com, int slot) => (*(void***)com)[slot];

    private static void Release(IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, SlotRelease))(com);
    }

    public void Dispose()
    {
        foreach (var buffer in _geometry.Values) Release(buffer);
        _geometry.Clear();
        ReleaseTargets();
        Release(_device);
        Release(_direct3D);
        _device = _direct3D = IntPtr.Zero;
    }
}
