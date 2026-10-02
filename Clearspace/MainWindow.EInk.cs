// Clearspace | How the main window refreshes in the E-reader theme.
// NEW (e-ink): Services/EInkScreen.cs makes the window look like an e-ink panel. This file makes it behave
// like one, the way a real e-reader does:
// Going to another folder, or opening or closing the photo viewer, Disk usage or Indexing, is a page turn:
// the old page does not vanish, it is pushed into the new one over about a third of a second in a few
// visible steps, and then the page is clean.
// REMOVED (e-ink, round 3): the full refresh (negative, black, white) that ran every sixth page turn and on
// every change of view. It read as a glitch rather than as e-ink. The effect can still do it (Invert,
// FlashLevel, FlashAmount in EInkScreen.cs); nothing asks for it any more.
// REMOVED (e-ink, round 2): the faint ghost of the old page left behind after a turn.
// Nothing here blocks input; it only changes one number on the screen effect over time.
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    // Puts the panel effect on the window (E-reader theme) or takes it off (every other theme).
    private void ApplyScreenEffect()
    {
        var screen = ThemeService.EInk ? EInkScreen.Main : null;
        if (screen is not null)
            ClearGhost(screen);
        ScreenFrame.Effect = screen;
    }

    // The effect, when it is on this window right now; otherwise null.
    private EInkEffect? ActiveScreen =>
        ThemeService.EInk && EInkScreen.Main is { } screen && ReferenceEquals(ScreenFrame.Effect, screen) ? screen : null;

    // A page turn: remember what is on screen, then let the new page come through it.
    private void TurnPage()
    {
        if (ActiveScreen is not { } screen)
            return;

        if (!CaptureGhost(screen))
            return;

        screen.GhostAmount = 0;   // where it rests once the steps below have run: no trace of the old page
        Step(screen, EInkEffect.GhostAmountProperty, 320,
            (1.0, 0), (0.82, 45), (0.62, 90), (0.44, 135), (0.28, 180), (0.16, 225), (0.08, 270));
    }

    // Changes one value of the effect in sudden steps (a panel has no smooth fades), then lets it fall back
    // to its resting value.
    private static void Step(EInkEffect screen, DependencyProperty property, int milliseconds, params (double Value, int At)[] steps)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            FillBehavior = FillBehavior.Stop
        };
        foreach (var (value, at) in steps)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
        screen.BeginAnimation(property, animation);
    }

    private static void ClearGhost(EInkEffect screen)
    {
        screen.BeginAnimation(EInkEffect.GhostAmountProperty, null);
        screen.GhostAmount = 0;
    }

    // Takes a picture of the window's contents as they are now and hands it to the effect as "the previous
    // page". False if there is nothing to photograph yet.
    private bool CaptureGhost(EInkEffect screen)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(ScreenContent);
            var width = (int)Math.Ceiling(ScreenContent.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(ScreenContent.ActualHeight * dpi.DpiScaleY);
            if (width < 1 || height < 1)
                return false;

            var picture = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            picture.Render(ScreenContent);
            picture.Freeze();
            screen.Ghost = new ImageBrush(picture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
