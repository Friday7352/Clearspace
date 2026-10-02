// Clearspace | Makes the window look and refresh like an e-ink panel (the E-reader theme).
// NEW (e-ink): an e-ink screen is not just "grey colours". Every pixel is one of 16 greys between a
// dark-grey "black" and a pale-grey "white"; a page turn pushes the old page into the new one; and every
// few turns the panel does a full refresh - negative, black, white, clean page. None of that can be done with colours in a theme file, so it is done to the
// finished picture of the window by a small pixel shader:
//   * turn each pixel to grey, snap it to 16 levels, and map it onto the panel's ink-to-paper range
//     (this also takes the colour out of real icons, photos and anything else in the window);
//   * mix in the previous page ("Ghost") by GhostAmount;
//   * show the negative (Invert) or a flat black / white (FlashLevel, FlashAmount) during a full refresh.
// MainWindow.EInk.cs drives those values. The shader is compiled by Windows' own compiler the first time
// the theme is used, the same way the disk usage map compiles its shader; if that fails the theme still
// works, only without the panel look and the refresh.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Clearspace.Services;

internal sealed class EInkEffect : ShaderEffect
{
    // The picture of the element the effect is on, and the picture of the previous page.
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(EInkEffect), 0);
    public static readonly DependencyProperty GhostProperty =
        RegisterPixelShaderSamplerProperty("Ghost", typeof(EInkEffect), 1);

    // 0 = normal, 1 = negative.
    public static readonly DependencyProperty InvertProperty =
        DependencyProperty.Register(nameof(Invert), typeof(double), typeof(EInkEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));
    // How much of the screen is replaced by one flat grey (0 = none, 1 = all), and which grey (0 = black, 1 = white).
    public static readonly DependencyProperty FlashAmountProperty =
        DependencyProperty.Register(nameof(FlashAmount), typeof(double), typeof(EInkEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty FlashLevelProperty =
        DependencyProperty.Register(nameof(FlashLevel), typeof(double), typeof(EInkEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));
    // How much of the previous page shows through (0 = none, 1 = only the previous page).
    public static readonly DependencyProperty GhostAmountProperty =
        DependencyProperty.Register(nameof(GhostAmount), typeof(double), typeof(EInkEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(3)));

    internal EInkEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(GhostProperty);
        UpdateShaderValue(InvertProperty);
        UpdateShaderValue(FlashAmountProperty);
        UpdateShaderValue(FlashLevelProperty);
        UpdateShaderValue(GhostAmountProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Ghost { get => (Brush)GetValue(GhostProperty); set => SetValue(GhostProperty, value); }
    public double Invert { get => (double)GetValue(InvertProperty); set => SetValue(InvertProperty, value); }
    public double FlashAmount { get => (double)GetValue(FlashAmountProperty); set => SetValue(FlashAmountProperty, value); }
    public double FlashLevel { get => (double)GetValue(FlashLevelProperty); set => SetValue(FlashLevelProperty, value); }
    public double GhostAmount { get => (double)GetValue(GhostAmountProperty); set => SetValue(GhostAmountProperty, value); }
}

internal static unsafe class EInkScreen
{
    // The resource key menus, tooltips and dialogs look up for their (plain, never animated) copy of the effect.
    internal const string PopupEffectKey = "PopupScreenEffect";

    // The colours arrive premultiplied by alpha, and leave the same way, so rounded corners stay see-through.
    private const string Shader = @"
sampler2D Input : register(s0);
sampler2D Ghost : register(s1);
float Invert : register(c0);
float FlashAmount : register(c1);
float FlashLevel : register(c2);
float GhostAmount : register(c3);

float4 main(float2 uv : TEXCOORD0) : COLOR
{
    float4 c = tex2D(Input, uv);
    float a = c.a;
    float3 rgb = c.rgb / max(a, 0.001);
    float g = dot(rgb, float3(0.299, 0.587, 0.114));

    float4 old = tex2D(Ghost, uv);
    float og = saturate(dot(old.rgb, float3(0.299, 0.587, 0.114)) + (1 - old.a));
    g = lerp(g, og, GhostAmount);
    g = lerp(g, 1 - g, Invert);
    g = lerp(g, FlashLevel, FlashAmount);
    g = floor(saturate(g) * 15 + 0.5) / 15;

    float3 ink = float3(0.141, 0.141, 0.137);
    float3 paper = float3(0.898, 0.890, 0.859);
    float3 o = lerp(ink, paper, g);
    return float4(o * a, a);
}";

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte* source, nuint sourceSize, string? sourceName, IntPtr defines, IntPtr include,
        string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    private static byte[]? _compiled;
    private static bool _compileTried;

    // True while the E-reader theme is the active one.
    internal static bool Active { get; private set; }

    // The main window's effect: the one whose ghost, negative and flash values are animated. Null if the
    // shader could not be compiled.
    internal static EInkEffect? Main { get; private set; }

    // Turns the panel look on or off. Called by ThemeService on every theme switch (UI thread).
    internal static void SetActive(bool active)
    {
        Active = active;
        var app = Application.Current;
        if (app is null)
            return;

        if (!active)
        {
            if (app.Resources.Contains(PopupEffectKey))
                app.Resources.Remove(PopupEffectKey);
            return;
        }

        Main ??= Create();
        if (!app.Resources.Contains(PopupEffectKey) && Create() is { } popup)
            app.Resources[PopupEffectKey] = popup;
    }

    // Puts a window's content inside a frame that carries the plain effect, so a dialog (the lock window,
    // a confirmation) gets the same panel look as the main window. Harmless in every other theme: the frame
    // is then just the window background.
    internal static void Frame(Window window)
    {
        if (window.Content is not UIElement content)
            return;

        window.Content = null;
        var frame = new Border { Child = content };
        frame.SetResourceReference(Border.BackgroundProperty, "Base");
        frame.SetResourceReference(UIElement.EffectProperty, PopupEffectKey);
        window.Content = frame;
    }

    private static EInkEffect? Create()
    {
        if (!_compileTried)
        {
            _compileTried = true;
            _compiled = Compile();
        }
        if (_compiled is null)
            return null;

        try
        {
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(_compiled));
            return new EInkEffect(shader);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: e-ink effect unavailable. {exception.Message}");
            return null;
        }
    }

    private static byte[]? Compile()
    {
        IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
        try
        {
            var source = Encoding.ASCII.GetBytes(Shader);
            int result;
            fixed (byte* text = source)
                result = D3DCompile(text, (nuint)source.Length, "EInk", IntPtr.Zero, IntPtr.Zero, "main", "ps_2_0",
                    1u << 15 /* D3DCOMPILE_OPTIMIZATION_LEVEL3 */, 0, out code, out errors);
            if (result < 0 || code == IntPtr.Zero)
            {
                if (errors != IntPtr.Zero)
                    Trace.WriteLine("Clearspace: e-ink shader did not compile. " +
                        Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(errors, 3))(errors)));
                return null;
            }

            var bytes = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(code, 3))(code);          // ID3DBlob::GetBufferPointer
            var length = (int)((delegate* unmanaged[Stdcall]<IntPtr, nuint>)Slot(code, 4))(code);     // ID3DBlob::GetBufferSize
            var compiled = new byte[length];
            Marshal.Copy(bytes, compiled, 0, length);
            return compiled;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: e-ink shader unavailable. {exception.Message}");
            return null;
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    // Entry number "index" of a COM object's function table.
    private static IntPtr Slot(IntPtr com, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size);

    private static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);   // IUnknown::Release
    }
}
