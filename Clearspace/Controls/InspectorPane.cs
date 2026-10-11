// Clearspace | NEW (inspector): everything about the selected file or folder, in one place, beside the list.
// The preview pane shows what a file looks like; the Inspector shows what it is:
//   - its name (selectable, so it can be copied), its type and its size, to the byte;
//   - a folder's real size: everything inside it, counted in the background (the count shows as it goes);
//   - a picture's size in pixels, a song's or a video's length;
//   - when it was made, changed and last opened; the program that opens it; its tags; its Git state;
//     whether it is read-only, hidden, compressed or encrypted; where it is;
//   - its SHA-256 fingerprint, worked out only when asked for (to check a download, or that two files match).
// Several selected items show how many, their total size (folders counted too), what kinds they are, and the
// oldest and newest change.
// Nothing is read from a cloud file that is not on this device, and nothing inside a ZIP file is opened.
// Each selection cancels whatever the one before was still working out.
// Built in code from the theme's colours (DynamicResource keys), so it follows every theme.
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clearspace.Models;

namespace Clearspace.Controls;

internal sealed class InspectorPane : Grid
{
    private readonly StackPanel _body = new();
    private readonly DispatcherTimer _settle;
    private IReadOnlyList<FileSystemItem> _selection = [];
    private CancellationTokenSource? _work;
    private Grid? _rows;

    /// <param name="close">Hides the Inspector (the × in its corner).</param>
    internal InspectorPane(Action close)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(DockUi.Heading("\uE946", "Inspector", "Hide the Inspector  ·  Alt+I", close));

        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        SetRow(scroll, 1);
        Children.Add(scroll);

        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => { _settle.Stop(); Render(); };
        Unloaded += (_, _) => { _settle.Stop(); Cancel(); };
        // CHANGED (review): opening or closing the Shelf moves the Inspector to a new stack, which can unload it and
        // so stop a folder count half-way ("Counting…" for ever); back on screen, it works the details out again.
        Loaded += (_, _) => Show(_selection);
        Render();
    }

    /// <summary>The selection changed (a moment later, the details follow).</summary>
    internal void Show(IReadOnlyList<FileSystemItem> selection)
    {
        _selection = selection;
        Cancel();
        _settle.Stop();
        _settle.Start();
    }

    private void Cancel()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
    }

    // ---------------------------------------------------------------- what it shows

    private void Render()
    {
        _body.Children.Clear();
        _rows = null;
        Cancel();
        var selection = _selection;
        if (selection.Count == 0)
        {
            _body.Children.Add(DockUi.Empty("\uE946", "Select a file or folder to see everything about it."));
            return;
        }
        _work = new CancellationTokenSource();
        if (selection.Count == 1) ShowOne(selection[0], _work.Token);
        else ShowSeveral(selection, _work.Token);
    }

    private void ShowOne(FileSystemItem item, CancellationToken token)
    {
        var path = item.FullPath;
        var onDisk = !item.IsInArchive && !item.IsDriveRoot && Path.IsPathFullyQualified(path);
        var local = onDisk && !item.IsOnlineOnly;

        var name = new TextBox
        {
            Text = item.Name, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 2)
        };
        name.SetResourceReference(Control.ForegroundProperty, "Ink");
        name.SetResourceReference(TextBox.SelectionBrushProperty, "Accent");
        _body.Children.Add(name);
        var kind = item.IsDriveRoot ? item.DriveKind ?? "Drive" : item.TypeName;
        _body.Children.Add(DockUi.Text(item.IsFolder || item.IsDriveRoot ? kind : $"{kind}  ·  {FileSystemItem.FormatSize(item.Size)}"));

        _rows = NewRows();
        _body.Children.Add(_rows);

        if (item.IsDriveRoot)
        {
            Add("Free", FileSystemItem.FormatSize(item.DriveAvailableSpace));
            Add("Size", FileSystemItem.FormatSize(item.DriveTotalSpace));
        }
        else if (item.IsFolder)
        {
            var size = Add("Size", local ? "Counting…" : "—");
            if (local) CountFolder(path, size, token);
        }
        else Add("Size", $"{FileSystemItem.FormatSize(item.Size)} ({item.Size:N0} bytes)");

        if (item.IsImageFile)
        {
            var pixels = Add("Pixels", item.HasDimensions ? item.DimensionsText : local ? "…" : "");
            if (local) ReadPixels(path, pixels, token);
        }
        if (item.HasDuration) Add("Length", item.DurationText);
        if (item.PageCount > 0) Add("Pages", item.PageCountText);
        if (!string.IsNullOrWhiteSpace(item.Artist)) Add("Artist", item.Artist);
        if (!string.IsNullOrWhiteSpace(item.Authors)) Add("Authors", item.Authors);

        Add("Changed", When(item.DateModified));
        Add("Made", When(item.DateCreated));
        if (onDisk) Add("Opened", Safe(() => When(item.IsFolder ? Directory.GetLastAccessTime(path) : File.GetLastAccessTime(path))));
        if (!item.IsFolder && onDisk && item.Extension.Length > 0) Add("Opens with", OpensWith(item.Extension));
        if (item.HasTags) Add("Tags", item.TagNames);
        if (!string.IsNullOrWhiteSpace(item.GitStatusText)) Add("Git", item.GitStatusText);
        if (item.IsCloudItem) Add("Cloud", item.CloudStatusText);
        if (!item.IsDriveRoot) Add("Marked", Marks(item.Attributes));
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } folder && onDisk) Add("Where", folder);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(DockUi.TextButton("\uE8C8", "Copy path", "Copy the full path", () => Copy(path)));
        if (local && !item.IsFolder)
            actions.Children.Add(DockUi.TextButton("\uE72E", "Fingerprint", "Work out the file's SHA-256 fingerprint, to check it is the file you expect", () => Fingerprint(path, actions)));
        _body.Children.Add(actions);
    }

    private void ShowSeveral(IReadOnlyList<FileSystemItem> selection, CancellationToken token)
    {
        var title = DockUi.Text($"{selection.Count:N0} items", "Ink", 15);
        title.FontWeight = FontWeights.SemiBold;
        _body.Children.Add(title);
        var folders = selection.Count(item => item.IsFolder && !item.IsDriveRoot);
        var files = selection.Count(item => !item.IsFolder && !item.IsDriveRoot);
        _body.Children.Add(DockUi.Text(string.Join("  ·  ", new[] { files > 0 ? Count(files, "file") : "", folders > 0 ? Count(folders, "folder") : "" }.Where(part => part.Length > 0))));

        _rows = NewRows();
        _body.Children.Add(_rows);
        long fileBytes = selection.Where(item => !item.IsFolder && !item.IsDriveRoot).Sum(item => Math.Max(0, item.Size));
        var folderPaths = selection.Where(item => item.IsFolder && !item.IsDriveRoot && !item.IsInArchive && !item.IsOnlineOnly && Path.IsPathFullyQualified(item.FullPath))
            .Select(item => item.FullPath).ToList();
        var size = Add("Size", folderPaths.Count == 0 ? $"{FileSystemItem.FormatSize(fileBytes)} ({fileBytes:N0} bytes)" : "Counting…");
        if (folderPaths.Count > 0) CountFolders(folderPaths, fileBytes, size, token);

        var kinds = selection.Where(item => !item.IsFolder && !item.IsDriveRoot).GroupBy(item => item.TypeName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ToList();
        if (kinds.Count > 0)
            Add("Kinds", string.Join(", ", kinds.Take(3).Select(group => $"{group.Count():N0} {group.Key}")) + (kinds.Count > 3 ? $", {kinds.Count - 3:N0} more" : ""));
        var dated = selection.Where(item => !item.IsDriveRoot && item.DateModified > DateTime.MinValue).Select(item => item.DateModified).ToList();
        if (dated.Count > 0)
        {
            Add("Newest", When(dated.Max()));
            Add("Oldest", When(dated.Min()));
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var paths = selection.Where(item => !item.IsDriveRoot).Select(item => item.FullPath).ToList();
        actions.Children.Add(DockUi.TextButton("\uE8C8", "Copy paths", "Copy every path, one per line", () => Copy(string.Join(Environment.NewLine, paths))));
        _body.Children.Add(actions);
    }

    // ---------------------------------------------------------------- the rows

    private static Grid NewRows()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    // A label and its value; the value's TextBlock is handed back so work done later can fill it in.
    private TextBlock Add(string label, string? value)
    {
        var right = new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        right.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        if (_rows is not { } rows || string.IsNullOrWhiteSpace(value)) return right;   // nothing to say: no row
        var row = rows.RowDefinitions.Count;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var left = new TextBlock { Text = label, Margin = new Thickness(0, 3, 14, 3) };
        left.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        SetRow(left, row);
        SetRow(right, row); SetColumn(right, 1);
        right.ToolTip = value;
        rows.Children.Add(left);
        rows.Children.Add(right);
        return right;
    }

    private static string Count(int count, string word) => $"{count:N0} {word}{(count == 1 ? "" : "s")}";

    private static string When(DateTime time) => time <= DateTime.MinValue.AddDays(1) || time.Year < 1981 ? "" : time.ToString("ddd d MMM yyyy, HH:mm");

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    private static string Marks(FileAttributes attributes)
    {
        var marks = new List<string>();
        if (attributes.HasFlag(FileAttributes.ReadOnly)) marks.Add("Read-only");
        if (attributes.HasFlag(FileAttributes.Hidden)) marks.Add("Hidden");
        if (attributes.HasFlag(FileAttributes.System)) marks.Add("System");
        if (attributes.HasFlag(FileAttributes.Compressed)) marks.Add("Compressed");
        if (attributes.HasFlag(FileAttributes.Encrypted)) marks.Add("Encrypted");
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) marks.Add("Link");
        return string.Join(", ", marks);
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (COMException) { }   // another program has the clipboard open; nothing is lost
    }

    // ---------------------------------------------------------------- work done in the background

    // A folder's size: every file inside it, however deep, skipping what cannot be read and links to elsewhere.
    private void CountFolder(string path, TextBlock into, CancellationToken token) => CountFolders([path], 0, into, token);

    private void CountFolders(IReadOnlyList<string> paths, long extraBytes, TextBlock into, CancellationToken token)
    {
        var progress = new Progress<(long Bytes, long Files, long Folders, bool Done)>(now =>
        {
            if (token.IsCancellationRequested) return;
            var bytes = now.Bytes + extraBytes;
            var size = $"{FileSystemItem.FormatSize(bytes)}  ·  {Count((int)Math.Min(int.MaxValue, now.Files), "file")}, {Count((int)Math.Min(int.MaxValue, now.Folders), "folder")}";
            into.Text = now.Done ? size : size + "  (counting…)";
            into.ToolTip = $"{bytes:N0} bytes";
        });
        IProgress<(long, long, long, bool)> report = progress;
        _ = Task.Run(() =>
        {
            long bytes = 0, files = 0, folders = 0;
            var last = Environment.TickCount64;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
            try
            {
                foreach (var root in paths)
                    foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry is FileInfo file) { files++; bytes += file.Length; }
                        else folders++;
                        if (Environment.TickCount64 - last > 250) { last = Environment.TickCount64; report.Report((bytes, files, folders, false)); }
                    }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            report.Report((bytes, files, folders, true));
        }, token);
    }

    // A picture's size in pixels, from its header only.
    private void ReadPixels(string path, TextBlock into, CancellationToken token)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                var (width, height) = (frame.PixelWidth, frame.PixelHeight);
                var megapixels = width * (double)height / 1_000_000;
                return $"{width:N0} × {height:N0}" + (megapixels >= 0.1 ? $"  ({megapixels:0.#} megapixels)" : "");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or COMException)
            {
                return "";
            }
        }, token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && !token.IsCancellationRequested) into.Text = task.Result is { Length: > 0 } text ? text : "Unknown";
        }, token, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // The SHA-256 fingerprint, worked out when the button is pressed; click it to copy it.
    private void Fingerprint(string path, WrapPanel actions)
    {
        var token = _work?.Token ?? CancellationToken.None;
        var line = DockUi.Text("Working out the fingerprint…");
        line.Margin = new Thickness(0, 8, 0, 0);
        line.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _body.Children.Add(line);
        foreach (var button in actions.Children.OfType<Button>()) if (button.ToolTip is string tip && tip.Contains("SHA-256")) button.IsEnabled = false;
        _ = Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        }, token).ContinueWith(task =>
        {
            if (token.IsCancellationRequested) return;
            if (task.IsCompletedSuccessfully)
            {
                line.Text = "SHA-256  " + task.Result;
                line.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                line.ToolTip = "Click to copy";
                line.Cursor = System.Windows.Input.Cursors.Hand;
                line.MouseLeftButtonUp += (_, _) => Copy(task.Result);
            }
            else line.Text = "The fingerprint could not be worked out: " + (task.Exception?.InnerException?.Message ?? "the file could not be read.");
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------------------------------------------------------- the program that opens a file type

    private static readonly Dictionary<string, string> s_opensWith = new(StringComparer.OrdinalIgnoreCase);

    private static string OpensWith(string extension)
    {
        if (s_opensWith.TryGetValue(extension, out var known)) return known;
        var name = "";
        try
        {
            var buffer = new StringBuilder(512);
            uint length = (uint)buffer.Capacity;
            if (AssocQueryString(AssocNoTruncate | AssocIgnoreUnknown, AssocFriendlyAppName, extension, null, buffer, ref length) == 0)
                name = buffer.ToString();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or COMException) { }
        return s_opensWith[extension] = name;
    }

    private const uint AssocNoTruncate = 0x20, AssocIgnoreUnknown = 0x400, AssocFriendlyAppName = 4;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
}
