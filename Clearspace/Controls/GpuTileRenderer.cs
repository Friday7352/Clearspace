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
    // NEW (round 43): the second colour of each vertex rides in the specular slot (COLOR1).
    private const uint FvfSpecular = 0x080;
    private const uint VertexFormat = FvfXyz | FvfDiffuse | FvfSpecular;
    private const uint FilterNone = 0;                  // D3DTEXF_NONE
    // Render states (D3DRENDERSTATETYPE) and values.
    private const uint RsZEnable = 7, RsSrcBlend = 19, RsDestBlend = 20, RsCullMode = 22, RsAlphaBlendEnable = 27,
        RsLighting = 137, RsMultisampleAntialias = 161, RsTextureFactor = 60;
    private const uint BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, CullNone = 1;
    // Texture stage states: output the vertex color only.
    private const uint TssColorOp = 1, TssColorArg1 = 2, TssAlphaOp = 4, TssAlphaArg1 = 5, TssAlphaArg2 = 6;
    // NEW (round 43): fixed-function fallback for the colour blend - LERP(TFACTOR, SPECULAR, DIFFUSE).
    private const uint TssColorArg2 = 3, TssColorArg0 = 26, OpSelectArg1 = 2, OpLerp = 26;
    private const uint ArgDiffuse = 0, ArgTFactor = 3, ArgSpecular = 4;
    private const int SlotCreateVertexShader = 91, SlotSetVertexShader = 92, SlotSetVertexShaderConstantF = 94;

    // Vtable slots (IUnknown = 0..2). Checked against d3d9.h.
    private const int SlotAddRef = 1, SlotRelease = 2;   // CHANGED (round 40): AddRef for background fills
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
        public uint Color;  // D3DCOLOR, ARGB: the colour under the scene's level
        public uint ColorB; // NEW (round 43): under its second level; the shader mixes the two
    }

    // NEW (round 43): mixes a block's two colours around the colour wheel rather than straight across.
    // A straight mix of blue and orange passes through grey; mixing lightness, chroma and hue separately
    // (OKLCH, a perceptual space) keeps every in-between colour as saturated as its ends, so a folder's
    // colours turn from one to the other instead of washing out on the way. It runs per vertex, so a
    // colour change costs nothing but a constant per frame; positions are mapped exactly as the
    // fixed-function projection did (a scale and an offset per axis).
    private const string ColorShader = @"
float4 Projection : register(c0);   // x scale, y scale, x offset, y offset
float4 Blend : register(c1);        // x: 0 = first colour, 1 = second
struct VIn { float3 Pos : POSITION; float4 A : COLOR0; float4 B : COLOR1; };
struct VOut { float4 Pos : POSITION; float4 Color : COLOR0; };
float3 ToLab(float3 c)
{
    float3 lin = pow(max(c, 0.00001), 2.2);
    float3 lms = float3(dot(lin, float3(0.4122214708, 0.5363325363, 0.0514459929)),
                        dot(lin, float3(0.2119034982, 0.6806995451, 0.1073969566)),
                        dot(lin, float3(0.0883024619, 0.2817188376, 0.6299787005)));
    lms = pow(max(lms, 0.0000001), 1.0 / 3.0);
    return float3(dot(lms, float3(0.2104542553, 0.7936177850, -0.0040720468)),
                  dot(lms, float3(1.9779984951, -2.4285922050, 0.4505937099)),
                  dot(lms, float3(0.0259040371, 0.7827717662, -0.8086757660)));
}
float3 FromLab(float3 lab)
{
    float3 lms = float3(dot(lab, float3(1.0, 0.3963377774, 0.2158037573)),
                        dot(lab, float3(1.0, -0.1055613458, -0.0638541728)),
                        dot(lab, float3(1.0, -0.0894841775, -1.2914855480)));
    lms = lms * lms * lms;
    float3 lin = float3(dot(lms, float3(4.0767416621, -3.3077115913, 0.2309699292)),
                        dot(lms, float3(-1.2684380046, 2.6097574011, -0.3413193965)),
                        dot(lms, float3(-0.0041960863, -0.7034186147, 1.7076147010)));
    return pow(max(saturate(lin), 0.00001), 1.0 / 2.2);
}
VOut main(VIn v)
{
    VOut o;
    o.Pos = float4(v.Pos.x * Projection.x + Projection.z, v.Pos.y * Projection.y + Projection.w, 0.0, 1.0);
    float t = Blend.x;
    float3 a = ToLab(v.A.rgb);
    float3 b = ToLab(v.B.rgb);
    float ca = length(a.yz);
    float cb = length(b.yz);
    float ha = atan2(a.z, a.y);
    float hb = atan2(b.z, b.y);
    // A grey end has no hue of its own: it takes the other end's, so only chroma changes.
    ha = ca < 0.02 ? hb : ha;
    hb = cb < 0.02 ? ha : hb;
    float d = hb - ha;
    d -= 6.28318531 * floor((d + 3.14159265) / 6.28318531);   // the short way round
    float h = ha + d * t;
    float c = lerp(ca, cb, t);
    float3 mixed = FromLab(float3(lerp(a.x, b.x, t), c * cos(h), c * sin(h)));
    // The ends are the exact colours, not a round trip through the conversion.
    float3 rgb = t <= 0.001 ? v.A.rgb : (t >= 0.999 ? v.B.rgb : mixed);
    o.Color = float4(rgb, v.A.a);
    return o;
}";

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte* source, nuint sourceSize, string? sourceName, IntPtr defines, IntPtr include,
        string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    private IntPtr _colorShader;   // IDirect3DVertexShader9, or zero: then the fixed-function mix is used

    /// <summary>NEW (round 43): whether colour changes are mixed in OKLCH (true) or straight across RGB.</summary>
    internal bool HasColorShader => _colorShader != IntPtr.Zero;

    // Compiled by Windows' own compiler at startup; any failure leaves the fixed-function path in place.
    private void CreateColorShader()
    {
        IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
        try
        {
            var source = System.Text.Encoding.ASCII.GetBytes(ColorShader);
            int result;
            fixed (byte* text = source)
                result = D3DCompile(text, (nuint)source.Length, "ColorShader", IntPtr.Zero, IntPtr.Zero, "main", "vs_2_0",
                    1u << 15 /* D3DCOMPILE_OPTIMIZATION_LEVEL3 */, 0, out code, out errors);
            if (result < 0 || code == IntPtr.Zero)
            {
                if (errors != IntPtr.Zero)
                    Trace.WriteLine("Clearspace: colour shader did not compile. " +
                        Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(errors, 3))(errors)));
                return;
            }
            var function = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(code, 3))(code);   // ID3DBlob::GetBufferPointer
            IntPtr shader;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(_device, SlotCreateVertexShader))(_device, function, &shader) >= 0)
                _colorShader = shader;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: colour shader unavailable, mixing colours directly. {exception.Message}");
        }
        finally
        {
            Release(code);
            Release(errors);
        }
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
    // NEW (round 40): vertex buffers filled on a worker thread for geometry the UI thread has not drawn
    // yet. The device is created multithreaded, so creating and filling a buffer off the UI thread is
    // allowed; the lock only keeps Dispose from releasing the device while that happens.
    private readonly object _deviceLock = new();
    private readonly List<(TileGeometry Geometry, IntPtr Buffer)> _prepared = [];
    // NEW (round 41): vertex buffers are reused rather than created and released for every scene. In
    // "render everything" each one is 60 MB or more; creating one per rebuild, several a second while
    // zooming, left the driver holding released allocations and the process's memory climbing. Two
    // spares are kept, with the size each was created at, and a new scene fills a spare that fits.
    private readonly Dictionary<IntPtr, uint> _capacity = [];
    private readonly List<IntPtr> _spare = [];
    private const int MaxSpares = 1;   // CHANGED (round 42): one; each can be 60 MB or more
    private readonly List<TileGeometry> _obsolete = [];
    internal int GeometryUploads { get; private set; }
    internal double LastUploadMilliseconds { get; private set; }
    internal int CachedGeometryCount => _geometry.Count;
    internal long CachedGeometryBytes => _geometry.Keys.Sum(g => (long)g.Count * 6 * sizeof(Vertex));

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
            var renderer = new GpuTileRenderer(direct3D, device) { AdapterName = Describe(direct3D, adapter) };
            renderer.CreateColorShader();   // NEW (round 43)
            return renderer;
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
        if (batches.Length > 3) throw new ArgumentOutOfRangeException(nameof(batches));   // CHANGED (round 42): + the zoom blend
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
            setStage(_device, 0, TssColorOp, OpSelectArg1); // diffuse color (the shader has already mixed it)
            setStage(_device, 0, TssColorArg1, ArgDiffuse);
            setStage(_device, 0, TssAlphaOp, 4); // MODULATE: vertex alpha * layer opacity
            setStage(_device, 0, TssAlphaArg1, 0);
            setStage(_device, 0, TssAlphaArg2, 3); // TFACTOR
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(_device, SlotSetFVF))(_device, VertexFormat);
            // NEW (round 43): the colour shader when there is one; the fixed-function pipeline otherwise.
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(_device, SlotSetVertexShader))(_device, _colorShader);
            var setConstants = (delegate* unmanaged[Stdcall]<IntPtr, uint, float*, uint, int>)Slot(_device, SlotSetVertexShaderConstantF);
            var constants = stackalloc float[8];   // c0: projection, c1: colour blend
            var transform = (delegate* unmanaged[Stdcall]<IntPtr, uint, Matrix4x4*, int>)Slot(_device, SlotSetTransform);
            var identity = Matrix4x4.Identity;
            transform(_device, 256, &identity); // world
            transform(_device, 2, &identity);   // view
            var draw = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int>)Slot(_device, SlotDrawPrimitive);
            foreach (ref readonly var batch in batches)
            {
                if (batch.Opacity <= 0 || batch.Geometry.Count == 0) continue;
                var projection = Projection(batch, width, height, scaleX, scaleY);
                var opacity = (uint)Math.Round(Math.Clamp(batch.Opacity, 0, 1) * 255) << 24;
                var colorBlend = (float)Math.Clamp(batch.ColorBlend, 0, 1);
                if (_colorShader != IntPtr.Zero)
                {
                    constants[0] = projection.M11;
                    constants[1] = projection.M22;
                    constants[2] = projection.M41;
                    constants[3] = projection.M42;
                    constants[4] = colorBlend;
                    setConstants(_device, 0, constants, 2);
                    setState(_device, RsTextureFactor, opacity | 0xFFFFFF);
                }
                else
                {
                    transform(_device, 3, &projection);
                    // Straight across RGB: the same colours at either end, a duller middle.
                    var level = (uint)Math.Round(colorBlend * 255);
                    setState(_device, RsTextureFactor, opacity | level << 16 | level << 8 | level);
                    setStage(_device, 0, TssColorOp, colorBlend > 0 ? OpLerp : OpSelectArg1);
                    setStage(_device, 0, TssColorArg0, ArgTFactor);
                    setStage(_device, 0, TssColorArg1, colorBlend > 0 ? ArgSpecular : ArgDiffuse);
                    setStage(_device, 0, TssColorArg2, ArgDiffuse);
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, int>)Slot(_device, SlotSetStreamSource))(
                    _device, 0, _geometry[batch.Geometry], 0, (uint)sizeof(Vertex));
                // Keep primitive counts below the limits of older adapters.
                for (var first = 0; first < batch.Geometry.Count; first += 8192)
                {
                    var count = Math.Min(8192, batch.Geometry.Count - first);
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
        foreach (var geometry in _obsolete) { Recycle(_geometry[geometry]); _geometry.Remove(geometry); }
        _obsolete.Clear();
    }

    private bool UploadGeometry(TileGeometry geometry)
    {
        if (_geometry.ContainsKey(geometry)) return true;
        if (geometry.Count == 0) { _geometry.Add(geometry, IntPtr.Zero); return true; }
        // NEW (round 40): a buffer already filled in the background is simply taken over.
        lock (_deviceLock)
        {
            for (var i = 0; i < _prepared.Count; i++)
                if (ReferenceEquals(_prepared[i].Geometry, geometry))
                {
                    _geometry.Add(geometry, _prepared[i].Buffer);
                    _prepared.RemoveAt(i);
                    GeometryUploads++;
                    return true;
                }
        }
        var buffer = CreateFilled(geometry, _device);
        if (buffer == IntPtr.Zero) return false;
        _geometry.Add(geometry, buffer);
        GeometryUploads++;
        return true;
    }

    /// <summary>
    /// NEW (round 40): fills <paramref name="geometry"/>'s vertex buffer now, from any thread, so drawing
    /// it later costs the UI thread nothing. Failing is harmless: the draw fills it as before.
    /// </summary>
    internal void Prepare(TileGeometry geometry, TileGeometry? partner = null, TileGeometry? approach = null)
    {
        // The device is held by a reference of our own for the length of the fill, so the lock is only
        // taken to read and write the list - never while tens of megabytes are being written, which
        // would make a UI thread that wanted the lock wait for all of it.
        IntPtr device;
        lock (_deviceLock)
        {
            if (_failed || _device == IntPtr.Zero) return;
            device = _device;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(device, SlotAddRef))(device);
        }
        var filled = new List<(TileGeometry, IntPtr)>(2);
        try
        {
            foreach (var item in new[] { geometry, partner, approach })
                if (item is not null && item.Count > 0 && CreateFilled(item, device) is var buffer && buffer != IntPtr.Zero)
                    filled.Add((item, buffer));
        }
        finally
        {
            lock (_deviceLock)
            {
                // Only the newest background build can still be waiting to be drawn; older ones were
                // replaced before they ever were, so their buffers are released rather than kept.
                foreach (var (_, stale) in _prepared) RecycleLocked(stale);
                _prepared.Clear();
                if (_device == device) _prepared.AddRange(filled);
                else foreach (var (_, buffer) in filled) Release(buffer);   // disposed meanwhile: the spares are gone too
            }
            Release(device);
        }
    }

    /// <summary>NEW (round 40): releases a background-filled buffer whose geometry will never be drawn.</summary>
    internal void Forget(TileGeometry geometry)
    {
        lock (_deviceLock)
            for (var i = _prepared.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_prepared[i].Geometry, geometry))
                {
                    RecycleLocked(_prepared[i].Buffer);
                    _prepared.RemoveAt(i);
                }
    }

    // Creates a write-only vertex buffer holding two triangles per command.
    private IntPtr CreateFilled(TileGeometry geometry, IntPtr device)
    {
        var bytes = checked((uint)(geometry.Count * 6 * sizeof(Vertex)));
        var buffer = IntPtr.Zero;
        lock (_deviceLock)
        {
            // The smallest spare that holds it, and not one more than twice its size.
            var best = -1;
            for (var i = 0; i < _spare.Count; i++)
            {
                var size = _capacity[_spare[i]];
                if (size >= bytes && size / 2 <= bytes && (best < 0 || size < _capacity[_spare[best]])) best = i;
            }
            if (best >= 0) { buffer = _spare[best]; _spare.RemoveAt(best); }
        }
        if (buffer == IntPtr.Zero)
        {
            // An eighth of headroom, so the next scene - usually a little larger or smaller - fits.
            var capacity = checked(bytes + bytes / 8 + (uint)(6 * sizeof(Vertex)));
            var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr*, IntPtr, int>)Slot(device, SlotCreateVertexBuffer);
            if (create(device, capacity, 8, VertexFormat, 0, &buffer, IntPtr.Zero) < 0) return IntPtr.Zero;
            lock (_deviceLock) _capacity[buffer] = capacity;
        }
        void* data;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void**, uint, int>)Slot(buffer, 11))(buffer, 0, bytes, &data, 0) < 0)
        { lock (_deviceLock) Discard(buffer); return IntPtr.Zero; }
        var vertices = (Vertex*)data;
        foreach (ref readonly var command in geometry.Span)
        {
            var color = (uint)command.Alpha << 24 | command.Color;
            var colorB = (uint)command.Alpha << 24 | command.To;   // NEW (round 43)
            vertices[0] = new Vertex { X = command.X0, Y = command.Y0, Color = color, ColorB = colorB };
            vertices[1] = new Vertex { X = command.X1, Y = command.Y0, Color = color, ColorB = colorB };
            vertices[2] = new Vertex { X = command.X0, Y = command.Y1, Color = color, ColorB = colorB };
            vertices[3] = new Vertex { X = command.X1, Y = command.Y0, Color = color, ColorB = colorB };
            vertices[4] = new Vertex { X = command.X1, Y = command.Y1, Color = color, ColorB = colorB };
            vertices[5] = new Vertex { X = command.X0, Y = command.Y1, Color = color, ColorB = colorB };
            vertices += 6;
        }
        if (((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 12))(buffer) < 0) { lock (_deviceLock) Discard(buffer); return IntPtr.Zero; }
        return buffer;
    }

    // A buffer no scene uses any more goes to the spares, or is released if there are enough.
    private void Recycle(IntPtr buffer)
    {
        lock (_deviceLock) RecycleLocked(buffer);
    }

    private void RecycleLocked(IntPtr buffer)
    {
        if (buffer == IntPtr.Zero) return;
        if (_capacity.ContainsKey(buffer) && _spare.Count < MaxSpares) _spare.Add(buffer);
        else Discard(buffer);
    }

    private void Discard(IntPtr buffer)
    {
        _capacity.Remove(buffer);
        Release(buffer);
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
        // CHANGED (round 40): under the lock a background Prepare takes, so it never uses a released device.
        lock (_deviceLock)
        {
            foreach (var (_, buffer) in _prepared) Release(buffer);
            _prepared.Clear();
            foreach (var buffer in _spare) Release(buffer);   // NEW (round 41)
            Release(_colorShader);                            // NEW (round 43)
            _colorShader = IntPtr.Zero;
            _spare.Clear();
            _capacity.Clear();
            Release(_device);
            Release(_direct3D);
            _device = _direct3D = IntPtr.Zero;
        }
    }
}
