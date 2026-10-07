// Clearspace | NEW (preview pane): what the pane beside the file list shows for the selected item.
//  - a picture, a video or a document Windows can draw: the picture, large
//  - a text file (anything that turns out to be text, whatever its ending): its first lines
//  - a .zip: what is inside it
//  - anything else: its icon
// with the name, type, size and dates underneath. Several selected items give a count and a total size.
// The preview is loaded off the UI thread and a moment after the selection settles, so holding an arrow
// key down in the list does not start a load for every row passed. A cloud file that is not on this
// device is never downloaded for a preview, and a locked file is not opened.
// Built in code from the theme's colours (DynamicResource keys), so it follows every theme.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.Controls;

internal sealed class PreviewPane : Grid
{
    private const int PictureSize = 720;   // the longest side asked for, in pixels
    private const int ArchiveRows = 200;

    private readonly Border _stage;
    private readonly TextBlock _name;
    private readonly Grid _details;
    private readonly DispatcherTimer _settle;
    private IReadOnlyList<FileSystemItem> _selection = [];
    private int _request; // goes up with every selection; a load that finishes for an older one is dropped

    internal PreviewPane()
    {
        Margin = new Thickness(14);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stage = new Border { MinHeight = 120, ClipToBounds = true };
        _stage.SetResourceReference(Border.BackgroundProperty, "Well");
        _stage.SetResourceReference(Border.CornerRadiusProperty, "R8");
        Children.Add(_stage);

        _name = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 62,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 14, 2, 0)
        };
        _name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        SetRow(_name, 1);
        Children.Add(_name);

        _details = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetRow(_details, 2);
        Children.Add(_details);

        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
        _settle.Tick += async (_, _) =>
        {
            _settle.Stop();
            await RenderAsync();
        };

        Unloaded += (_, _) => _settle.Stop();
        ShowNothingSelected();
    }

    // The selection changed. An empty list clears the pane.
    internal void Show(IReadOnlyList<FileSystemItem> selection)
    {
        _selection = selection;
        _request++;
        _settle.Stop();
        _settle.Start();
    }

    private async Task RenderAsync()
    {
        var request = _request;
        var selection = _selection;

        try
        {
            if (selection.Count == 0)
            {
                ShowNothingSelected();
                return;
            }

            if (selection.Count > 1)
            {
                ShowSeveral(selection);
                return;
            }

            var item = selection[0];
            var path = item.FullPath;
            var onDisk = !item.IsDriveRoot && Path.IsPathFullyQualified(path);
            ShowDetails(item, onDisk);

            // NEW (zip folders): a file inside a ZIP file is previewed from a copy taken out into Clearspace's
            // temporary folder (the same copy a double-click opens). From here on that copy is the file: a
            // picture shows as a picture, text as text. Large files are not taken out just to be looked at.
            if (item.IsInArchive)
            {
                if (item.IsFolder || item.Size > ArchiveBrowser.MostToPreview)
                {
                    ShowMessage(item.IsFolder ? "\uE8B7" : "\uE7B8",
                        item.IsFolder ? "A folder inside the ZIP file." : "Too large to preview from inside a ZIP file. Open it to see it.");
                    return;
                }

                var location = path;
                var copy = await Task.Run(() => CopyOutOfArchive(location));
                if (request != _request)
                    return;

                if (copy is null || FileSystemItem.FromLocation(copy) is not { } real)
                {
                    ShowMessage("\uE7C3", "No preview for this item.");
                    return;
                }

                item = real;
                path = copy;
                onDisk = true;
            }

            if (onDisk && !item.IsFolder)
            {
                if (LockNames.HasLockedName(path))
                {
                    ShowMessage("", "This file is locked. Unlock it to see what is inside.");
                    return;
                }

                if (ArchiveService.IsArchive(path) && !item.IsOnlineOnly)
                {
                    var listing = await Task.Run(() => ListArchive(path));
                    if (request != _request)
                        return;
                    if (listing is not null)
                    {
                        ShowArchive(listing);
                        return;
                    }
                }
                else if (!MediaTypes.IsThumbnailCandidate(item.Extension) && !MediaTypes.IsAudio(item.Extension)
                         && !item.IsOnlineOnly && item.Size > 0)
                {
                    var sample = await Task.Run(() => TextPreview.Read(path));
                    if (request != _request)
                        return;
                    if (sample is not null)
                    {
                        ShowText(sample);
                        return;
                    }
                }
            }

            var picture = await ThumbnailService.PreviewAsync(item, PictureSize);
            if (request != _request)
                return;

            if (picture is null)
                ShowMessage("", "No preview for this item.");
            else
                ShowPicture(picture);
        }
        catch (Exception)
        {
            // A preview is a convenience: whatever went wrong reading the file, the pane says so and the
            // list carries on.
            if (request == _request)
                ShowMessage("", "This item can't be previewed.");
        }
    }

    // NEW (zip folders): null when the file can't be taken out (the ZIP is gone, damaged or protected).
    private static string? CopyOutOfArchive(string location)
    {
        try
        {
            return ArchiveBrowser.CopyForOpening(location);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static ArchiveListing? ListArchive(string path)
    {
        try
        {
            return ArchiveService.List(path, ArchiveRows);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null; // damaged, or not really a .zip: fall back to its icon
        }
    }

    // ---- what the stage shows

    private void ShowNothingSelected()
    {
        _name.Text = string.Empty;
        ClearDetails();
        ShowMessage("", "Select a file to preview it.");
    }

    private void ShowSeveral(IReadOnlyList<FileSystemItem> selection)
    {
        var folders = selection.Count(item => item.IsFolder);
        var files = selection.Count - folders;
        var size = selection.Where(item => !item.IsFolder && !item.IsDriveRoot).Sum(item => Math.Max(0, item.Size));

        _name.Text = $"{selection.Count:N0} items selected";
        ClearDetails();
        if (files > 0)
            AddDetail(files == 1 ? "File" : "Files", files.ToString("N0"));
        if (folders > 0)
            AddDetail(folders == 1 ? "Folder" : "Folders", folders.ToString("N0"));
        if (files > 0)
            AddDetail("Size of the files", FileSystemItem.FormatSize(size));

        ShowMessage("", "Several items are selected.");
    }

    private void ShowMessage(string glyph, string text)
    {
        var symbol = new TextBlock { Text = glyph, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");

        var line = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18) };
        stack.Children.Add(symbol);
        stack.Children.Add(line);
        _stage.Child = stack;
    }

    private void ShowPicture(ImageSource picture)
    {
        var image = new Image
        {
            Source = picture,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _stage.Child = image;
    }

    private void ShowText(TextSample sample)
    {
        var text = new TextBlock
        {
            Text = sample.Truncated ? sample.Text + "\n…" : sample.Text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        _stage.Child = new ScrollViewer
        {
            Content = text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 10, 12, 10)
        };
    }

    private void ShowArchive(ArchiveListing listing)
    {
        var rows = new StackPanel();

        var summary = new TextBlock
        {
            Text = $"{listing.TotalEntries:N0} {(listing.TotalEntries == 1 ? "item" : "items")}, {FileSystemItem.FormatSize(listing.TotalSize)} unpacked",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        rows.Children.Add(summary);

        foreach (var entry in listing.Entries)
        {
            var size = new TextBlock { Text = entry.IsFolder ? string.Empty : FileSystemItem.FormatSize(entry.Size), Margin = new Thickness(12, 0, 0, 0) };
            size.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
            DockPanel.SetDock(size, Dock.Right);

            var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Name };
            name.SetResourceReference(TextBlock.ForegroundProperty, entry.IsFolder ? "InkMuted" : "Ink");

            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(size);
            row.Children.Add(name);
            rows.Children.Add(row);
        }

        if (listing.TotalEntries > listing.Entries.Count)
        {
            var more = new TextBlock { Text = $"… and {listing.TotalEntries - listing.Entries.Count:N0} more", Margin = new Thickness(0, 6, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            rows.Children.Add(more);
        }

        _stage.Child = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 10, 12, 10)
        };
    }

    // ---- the lines under the stage

    private void ShowDetails(FileSystemItem item, bool onDisk)
    {
        _name.Text = item.Name;
        ClearDetails();

        AddDetail("Type", item.TypeName);
        if (!item.IsFolder || item.IsDriveRoot)
            AddDetail("Size", item.SizeText);
        if (item.HasDimensions)
            AddDetail("Dimensions", item.DimensionsText);
        AddDetail("Modified", item.DateModifiedText);
        AddDetail("Created", item.DateCreatedText);
        if (item.HasTags)
            AddDetail("Tags", item.TagNames);
        if (onDisk && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.FullPath)) is { Length: > 0 } folder)
            AddDetail("In", folder);
    }

    private void ClearDetails()
    {
        _details.Children.Clear();
        _details.RowDefinitions.Clear();
    }

    private void AddDetail(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var row = _details.RowDefinitions.Count;
        _details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = new TextBlock { Text = label, Margin = new Thickness(0, 2, 14, 2) };
        left.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        SetRow(left, row);
        _details.Children.Add(left);

        var right = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), ToolTip = value };
        right.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        SetRow(right, row);
        SetColumn(right, 1);
        _details.Children.Add(right);
    }
}
