// Clearspace | NEW (archives): a small "working…" dialog for a job that takes a while and can be stopped.
// It starts the job when it opens, shows how far along it is, and closes by itself when the job ends.
// Cancel (or Esc, or the title bar's X) asks the job to stop; the dialog then waits for it, so the window
// is never gone while files are still being written. Built in code like MessageDialog, so it follows the theme.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

internal sealed class ProgressWindow : Window
{
    private readonly Func<IProgress<ArchiveProgress>, CancellationToken, Task<string>> _work;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly Button _stop;
    private bool _running;

    // What the job returned (the folder or file it made); null when it failed or was stopped.
    internal string? Result { get; private set; }

    // Why it failed, in words; null when it finished or was stopped.
    internal string? Error { get; private set; }

    internal bool Stopped { get; private set; }

    internal ProgressWindow(string title, string glyphText, Func<IProgress<ArchiveProgress>, CancellationToken, Task<string>> work)
    {
        _work = work;

        Title = "Clearspace";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        var glyph = new TextBlock
        {
            Text = glyphText,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = glyph
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        DockPanel.SetDock(badge, Dock.Left);

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(heading);

        _bar = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 1,
            IsIndeterminate = true, // until the first report says how much there is to do
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 20, 0, 0)
        };
        _bar.SetResourceReference(ForegroundProperty, "Accent");
        _bar.SetResourceReference(BackgroundProperty, "Line");

        _status = new TextBlock { Text = "Starting…", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");

        _stop = new Button { Content = "Cancel", MinWidth = 76, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        _stop.SetResourceReference(StyleProperty, "ViewerTextButton");
        _stop.IsCancel = true; // Esc
        _stop.Click += (_, _) => RequestStop();

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_bar);
        stack.Children.Add(_status);
        stack.Children.Add(_stop);

        var card = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Content = card;
        EInkScreen.Frame(this); // the E-reader theme's panel look reaches this dialog too

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) =>
        {
            // Closing while the job runs asks it to stop and waits; RunAsync closes the window when it has.
            if (!_running)
                return;

            e.Cancel = true;
            RequestStop();
        };
        Closed += (_, _) => _cancel.Dispose();
    }

    private void RequestStop()
    {
        if (!_running)
        {
            Close();
            return;
        }

        _stop.IsEnabled = false;
        _status.Text = "Stopping…";
        _cancel.Cancel();
    }

    private async Task RunAsync()
    {
        _running = true;
        var progress = new Progress<ArchiveProgress>(report =>
        {
            if (!_running || _cancel.IsCancellationRequested)
                return;

            if (report.BytesTotal > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = Math.Clamp((double)report.BytesDone / report.BytesTotal, 0, 1);
                _status.Text = $"{FileSystemItem.FormatSize(report.BytesDone)} of {FileSystemItem.FormatSize(report.BytesTotal)}   {report.CurrentName}";
            }
            else if (report.CurrentName.Length > 0)
            {
                _status.Text = report.CurrentName;
            }
        });

        try
        {
            Result = await _work(progress, _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Stopped = true;
        }
        catch (Exception error)
        {
            // FIXED (pre-release review): every failure is reported the same way. Only four kinds of error
            // were caught here; anything else (a name inside an archive that Windows can't turn into a
            // path, a full disk reported another way) escaped this handler and came up as the app-wide
            // "Clearspace hit an error" box with a stack trace. The job has cleaned up after itself
            // either way; the caller shows Error in the ordinary "Couldn't unpack" dialog.
            Error = error.Message;
        }
        finally
        {
            _running = false;
            Close();
        }
    }

    // Shows the dialog over `owner`, runs the job, and returns once it has ended.
    internal static ProgressWindow Run(Window owner, string title, string glyph,
        Func<IProgress<ArchiveProgress>, CancellationToken, Task<string>> work)
    {
        var dialog = new ProgressWindow(title, glyph, work) { Owner = owner };
        dialog.ShowDialog();
        return dialog;
    }
}
