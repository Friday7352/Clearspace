// Clearspace | GPU renderer for the disk usage map.
// NEW (round 14): tiles are drawn by Direct3D 9Ex into a render target that WPF composites through
// D3DImage, so labels and overlays stay ordinary WPF content on top. Direct3D is called through its
// COM function tables (no extra packages). Every tile is two triangles in one vertex batch, drawn
// with 4x multisample antialiasing and resolved into the surface WPF shows. If anything fails, the
// map falls back to its CPU rasterizer.

using System.Diagnostics;
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
    private const uint SwapEffectDiscard = 1;
    private const uint PresentIntervalImmediate = 0x80000000;
    private const uint ClearTarget = 1;                 // D3DCLEAR_TARGET
    private const uint PrimitiveTriangleList = 4;       // D3DPT_TRIANGLELIST
    private const uint FvfXyzRhw = 0x004, FvfDiffuse = 0x040;
    private const uint FilterNone = 0;                  // D3DTEXF_NONE
    // Render states (D3DRENDERSTATETYPE) and values.
    private const uint RsZEnable = 7, RsSrcBlend = 19, RsDestBlend = 20, RsCullMode = 22, RsAlphaBlendEnable = 27,
        RsLighting = 137, RsMultisampleAntialias = 161;
    private const uint BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, CullNone = 1;
    // Texture stage states: output the vertex color only.
    private const uint TssColorOp = 1, TssAlphaOp = 4, TopSelectArg2 = 3;

    // Vtable slots (IUnknown = 0..2). Checked against d3d9.h.
    private const int SlotRelease = 2;
    private const int SlotCreateDeviceEx = 20;         // IDirect3D9Ex
    private const int SlotCreateRenderTarget = 28, SlotStretchRect = 34, SlotSetRenderTarget = 37,
        SlotBeginScene = 41, SlotEndScene = 42, SlotClear = 43, SlotSetRenderState = 57,
        SlotSetTextureStageState = 67, SlotDrawPrimitiveUP = 83, SlotSetFVF = 89; // IDirect3DDevice9

    [DllImport("d3d9.dll")]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr direct3D);

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
        public float X, Y, Z, Rhw;
        public uint Color; // D3DCOLOR, ARGB
    }

    private const int RectsPerBatch = 4096;

    private IntPtr _direct3D;
    private IntPtr _device;
    private IntPtr _shared;      // single-sample surface shown by D3DImage
    private IntPtr _multisample; // 4x MSAA render target resolved into _shared (0 if unsupported)
    private int _width, _height;
    private bool _failed;
    private readonly Vertex[] _vertices = new Vertex[RectsPerBatch * 6];

    public D3DImage Image { get; } = new();
    public bool IsAvailable => !_failed && _device != IntPtr.Zero && Image.IsFrontBufferAvailable;
    public bool IsMultisampled => _multisample != IntPtr.Zero;

    private GpuTileRenderer(IntPtr direct3D, IntPtr device)
    {
        _direct3D = direct3D;
        _device = device;
        // The front buffer can go away (lock screen, remote desktop, driver reset); rebuild the
        // surfaces the next time it is available.
        Image.IsFrontBufferAvailableChanged += (_, _) => ReleaseTargets();
    }

    public static GpuTileRenderer? TryCreate()
    {
        try
        {
            if (Direct3DCreate9Ex(SdkVersion, out var direct3D) < 0 || direct3D == IntPtr.Zero) return null;
            var parameters = new PresentParameters
            {
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1,
                SwapEffect = SwapEffectDiscard,
                DeviceWindow = GetDesktopWindow(),
                Windowed = 1,
                PresentationInterval = PresentIntervalImmediate
            };
            var createDevice = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, uint, PresentParameters*, IntPtr, IntPtr*, int>)
                Slot(direct3D, SlotCreateDeviceEx);
            IntPtr device;
            var result = createDevice(direct3D, 0, DeviceTypeHal, parameters.DeviceWindow,
                CreateHardwareVertexProcessing | CreateMultithreaded | CreateFpuPreserve, &parameters, IntPtr.Zero, &device);
            if (result < 0)
                result = createDevice(direct3D, 0, DeviceTypeHal, parameters.DeviceWindow,
                    CreateSoftwareVertexProcessing | CreateMultithreaded | CreateFpuPreserve, &parameters, IntPtr.Zero, &device);
            if (result < 0 || device == IntPtr.Zero)
            {
                Release(direct3D);
                return null;
            }
            return new GpuTileRenderer(direct3D, device);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: GPU renderer unavailable, using the CPU. {exception.Message}");
            return null;
        }
    }

    // Draws the frame. `commands` are in element (DIP) units; scale converts them to pixels.
    // Returns false if the GPU path can't be used for this frame.
    public bool Render(ReadOnlySpan<DiskUsageTreemap.TileCommand> commands, int width, int height,
        double scaleX, double scaleY, uint background)
    {
        if (!IsAvailable || !EnsureTargets(width, height)) return false;
        Image.Lock();
        try
        {
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
            setStage(_device, 0, TssColorOp, TopSelectArg2); // vertex color, no texture
            setStage(_device, 0, TssAlphaOp, TopSelectArg2);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(_device, SlotSetFVF))(_device, FvfXyzRhw | FvfDiffuse);

            var draw = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void*, uint, int>)Slot(_device, SlotDrawPrimitiveUP);
            var sx = (float)scaleX;
            var sy = (float)scaleY;
            var count = 0;
            fixed (Vertex* vertices = _vertices)
            {
                foreach (ref readonly var command in commands)
                {
                    // Pre-transformed vertices sample at pixel centers: shift by half a pixel.
                    float x0 = command.X0 * sx - .5f, y0 = command.Y0 * sy - .5f;
                    float x1 = command.X1 * sx - .5f, y1 = command.Y1 * sy - .5f;
                    var color = (uint)command.Alpha << 24 | command.Color;
                    var v = vertices + count * 6;
                    v[0] = new Vertex { X = x0, Y = y0, Rhw = 1, Color = color };
                    v[1] = new Vertex { X = x1, Y = y0, Rhw = 1, Color = color };
                    v[2] = new Vertex { X = x0, Y = y1, Rhw = 1, Color = color };
                    v[3] = new Vertex { X = x1, Y = y0, Rhw = 1, Color = color };
                    v[4] = new Vertex { X = x1, Y = y1, Rhw = 1, Color = color };
                    v[5] = new Vertex { X = x0, Y = y1, Rhw = 1, Color = color };
                    if (++count == RectsPerBatch)
                    {
                        draw(_device, PrimitiveTriangleList, (uint)(count * 2), vertices, (uint)sizeof(Vertex));
                        count = 0;
                    }
                }
                if (count > 0) draw(_device, PrimitiveTriangleList, (uint)(count * 2), vertices, (uint)sizeof(Vertex));
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
            Image.Unlock();
        }
    }

    private bool EnsureTargets(int width, int height)
    {
        if (_shared != IntPtr.Zero && width == _width && height == _height) return true;
        ReleaseTargets();
        var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int, IntPtr*, IntPtr*, int>)
            Slot(_device, SlotCreateRenderTarget);
        IntPtr shared, handle = IntPtr.Zero;
        // A shareable surface is what D3DImage expects from a Direct3D 9Ex device.
        if (create(_device, (uint)width, (uint)height, FormatA8R8G8B8, 0, 0, 0, &shared, &handle) < 0 || shared == IntPtr.Zero)
            return Fail();
        IntPtr multisample;
        if (create(_device, (uint)width, (uint)height, FormatA8R8G8B8, Multisample4, 0, 0, &multisample, null) < 0)
            multisample = IntPtr.Zero; // no MSAA on this adapter: draw straight into the shared surface
        _shared = shared;
        _multisample = multisample;
        _width = width;
        _height = height;
        Image.Lock();
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _shared);
        Image.Unlock();
        return true;
    }

    private void ReleaseTargets()
    {
        if (_shared != IntPtr.Zero && Image.Dispatcher.CheckAccess())
        {
            Image.Lock();
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            Image.Unlock();
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
        ReleaseTargets();
        Release(_device);
        Release(_direct3D);
        _device = _direct3D = IntPtr.Zero;
    }
}
