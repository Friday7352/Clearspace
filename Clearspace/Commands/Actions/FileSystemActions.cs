// Clearspace | File-system command actions.

using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Clearspace.Services;

namespace Clearspace.Commands.Actions;

public sealed class OpenItemAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.OpenItem;
    public string Label => "Open";
    public string Description => "Open the selected item";
    public string Glyph => "\uE8E5";
    public HotKey HotKey => new(Key.Enter);
    public bool IsExecutable => context.HasSelection;

    // CHANGED (zip folders): a ZIP file opens in the list like a folder, as in File Explorer (it used to
    // go to whichever program Windows uses for .zip files; "Open with…" still offers those).
    //  - A ZIP file on a disk: the list goes inside it.
    //  - A folder inside a ZIP: the list goes into it, like any folder.
    //  - A file inside a ZIP: a copy is taken out into Clearspace's temporary folder and that copy is
    //    opened. The copy is read-only, because changes to it would not go back into the ZIP file.
    //    A ZIP inside a ZIP opens in the list the same way, from its copy.
    // An address inside a ZIP is never handed to Windows to start.
    public async Task ExecuteAsync(object? parameter = null)
    {
        foreach (var item in context.SelectedItems)
        {
            if (item.IsFolder)
            {
                context.Navigation.Navigate(item.FullPath);
                return;
            }

            // NEW (zip folders)
            if (item.IsInArchive)
            {
                if (context.ArchiveCopies?.Invoke([item], true) is not [var copy])
                    return; // failed or stopped; the window has said so

                if (ArchiveService.IsArchive(copy))
                {
                    context.Navigation.Navigate(ArchiveLocation.For(copy));
                    return;
                }

                if (Start(copy))
                    context.Notify?.Invoke($"Opened a copy of {item.Name}. Changes to it are not saved into the ZIP file.");
                continue;
            }

            try
            {
                if (context.OpenLockedFile is not null && await context.OpenLockedFile(item.FullPath))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            // NEW (zip folders)
            if (ArchiveService.IsArchive(item.FullPath))
            {
                context.Navigation.Navigate(ArchiveLocation.For(item.FullPath));
                return;
            }

            Start(item.FullPath);
        }

    }

    private static bool Start(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class DeleteAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.Delete;
    public string Label => "Delete";
    public string Description => "Move the selection to the Recycle Bin";
    public string Glyph => "\uE74D";
    public HotKey HotKey => new(Key.Delete);
    public bool IsExecutable => context.HasSelection && !context.SelectionInArchive; // CHANGED (zip folders): the inside of a ZIP is read-only

    public Task ExecuteAsync(object? parameter = null)
    {
        var paths = context.SelectedPaths;
        if (paths.Length == 0)
            return Task.CompletedTask;

        context.ReportFileOperation(FileOperationService.Delete(paths, context.OwnerHandle));
        context.RequestRefresh();
        return Task.CompletedTask;
    }
}

public sealed class DeletePermanentlyAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.DeletePermanently;
    public string Label => "Delete permanently";
    public string Description => "Delete the selection without using the Recycle Bin";
    public string Glyph => "\uE74D";
    public HotKey HotKey => new(Key.Delete, ModifierKeys.Shift);
    public bool IsExecutable => context.HasSelection && !context.SelectionInArchive; // CHANGED (zip folders)

    public Task ExecuteAsync(object? parameter = null)
    {
        var paths = context.SelectedPaths;
        if (paths.Length == 0)
            return Task.CompletedTask;

        context.ReportFileOperation(FileOperationService.Delete(paths, context.OwnerHandle, permanent: true));
        context.RequestRefresh();
        return Task.CompletedTask;
    }
}

public sealed class RenameAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.Rename;
    public string Label => "Rename";
    public string Description => "Rename the selected item, or several at once";
    public string Glyph => "\uE8AC";
    public HotKey HotKey => new(Key.F2);
    // CHANGED (batch rename): also available with several items selected (was a single item only).
    // CHANGED (zip folders): not for things inside a ZIP file.
    public bool IsExecutable => !context.SelectionInArchive &&
                                (context.HasSingleSelection || (context.HasSelection && context.BeginBatchRename is not null));

    public Task ExecuteAsync(object? parameter = null)
    {
        // NEW (batch rename): several items go to the batch rename dialog.
        if (context.SelectedItems.Count > 1)
        {
            context.BeginBatchRename?.Invoke(context.SelectedItems);
            return Task.CompletedTask;
        }

        context.BeginRename?.Invoke(context.SelectedItems.FirstOrDefault());
        return Task.CompletedTask;
    }
}

// NEW (preview pane): shows or hides the pane beside the file list that previews the selected file.
public sealed class TogglePreviewPaneAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.TogglePreviewPane;
    public string Label => "Preview pane";
    public string Description => "Show or hide the preview of the selected file";
    public string Glyph => "\uE8A1";
    public HotKey HotKey => new(Key.P, ModifierKeys.Alt);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.TogglePreviewPane?.Invoke();
        return Task.CompletedTask;
    }
}

// NEW (inspector): shows or hides the Inspector, the details of the selected file (Controls/InspectorPane.cs).
public sealed class ToggleInspectorAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.ToggleInspector;
    public string Label => "Inspector";
    public string Description => "Show or hide the details of the selected file";
    public string Glyph => "\uE946";
    public HotKey HotKey => new(Key.I, ModifierKeys.Alt);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.ToggleInspector?.Invoke();
        return Task.CompletedTask;
    }
}

// NEW (shelf): shows or hides the Shelf, where files from anywhere are collected (Controls/ShelfPane.cs).
public sealed class ToggleShelfAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.ToggleShelf;
    public string Label => "Shelf";
    public string Description => "Show or hide the shelf, where you collect files from anywhere";
    public string Glyph => "\uE8F1";
    public HotKey HotKey => new(Key.S, ModifierKeys.Alt);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.ToggleShelf?.Invoke();
        return Task.CompletedTask;
    }
}

// NEW (shelf): puts the selection on the shelf (and shows the shelf).
public sealed class AddToShelfAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.AddToShelf;
    public string Label => "Add to shelf";
    public string Description => "Put the selected files on the shelf, to move, copy or zip them together later";
    public string Glyph => "\uE8F1";

    public bool IsExecutable => context.SelectedItems.Any(item => !item.IsDriveRoot && !item.IsInArchive);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.AddToShelf?.Invoke(context.SelectedItems);
        return Task.CompletedTask;
    }
}

public sealed class CopyItemAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.CopyItem;
    public string Label => "Copy";
    public string Description => "Copy the selection to the clipboard";
    public string Glyph => "\uE8C8";
    public HotKey HotKey => new(Key.C, ModifierKeys.Control);
    public bool IsExecutable => context.HasSelection;

    public Task ExecuteAsync(object? parameter = null)
    {
        // NEW (zip folders): things inside a ZIP file are taken out into Clearspace's temporary folder
        // first, and those copies go on the clipboard, so Paste works here and in every other program.
        if (context.SelectionInArchive)
        {
            var inside = context.SelectedItems.Where(item => item.IsInArchive).ToList();
            if (context.ArchiveCopies?.Invoke(inside, false) is { Count: > 0 } copies)
            {
                List<string> paths = [.. context.SelectedItems.Where(item => !item.IsInArchive).Select(item => item.FullPath), .. copies];
                ClipboardHelper.SetFiles(paths, cut: false);
                context.Notify?.Invoke(paths.Count == 1 ? $"Copied {inside[0].Name}." : $"Copied {paths.Count:N0} items.");
            }

            return Task.CompletedTask;
        }

        ClipboardHelper.SetFiles(context.SelectedPaths, cut: false);
        return Task.CompletedTask;
    }
}

public sealed class CutItemAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.CutItem;
    public string Label => "Cut";
    public string Description => "Move the selection to the clipboard";
    public string Glyph => "\uE8C6";
    public HotKey HotKey => new(Key.X, ModifierKeys.Control);
    public bool IsExecutable => context.HasSelection && !context.SelectionInArchive; // CHANGED (zip folders): nothing can be taken away from a ZIP

    public Task ExecuteAsync(object? parameter = null)
    {
        ClipboardHelper.SetFiles(context.SelectedPaths, cut: true);
        return Task.CompletedTask;
    }
}

public sealed class PasteItemAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.PasteItem;
    public string Label => "Paste";
    public string Description => "Paste clipboard contents into this folder";
    public string Glyph => "\uE77F";
    public HotKey HotKey => new(Key.V, ModifierKeys.Control);
    public bool IsExecutable => Directory.Exists(context.CurrentPath);

    public Task ExecuteAsync(object? parameter = null)
    {
        var (paths, cut) = ClipboardHelper.GetFiles();
        if (paths.Length == 0 || !Directory.Exists(context.CurrentPath))
            return Task.CompletedTask;

        // FIXED (pre-release review): pasting into the folder the items are already in.
        //  - Copied (Ctrl+C, Ctrl+V in one folder, the usual way to duplicate a file): Windows refuses to copy
        //    a file onto itself and showed "the source and destination file names are the same". The copies
        //    now get names of their own ("name - Copy"), as in File Explorer.
        //  - Cut: the items are where they are being moved to, so there is nothing to do (it was the same error).
        var here = Path.TrimEndingDirectorySeparator(context.CurrentPath);
        var alreadyHere = paths.All(path => string.Equals(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)), here, StringComparison.OrdinalIgnoreCase));
        if (cut && alreadyHere)
            return Task.CompletedTask;

        var result = cut
            ? FileOperationService.Move(paths, context.CurrentPath, context.OwnerHandle)
            : FileOperationService.Copy(paths, context.CurrentPath, context.OwnerHandle, keepBoth: alreadyHere);
        context.ReportFileOperation(result);

        context.RequestRefresh();
        return Task.CompletedTask;
    }
}

public sealed class CopyPathAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.CopyPath;
    public string Label => "Copy path";
    public string Description => "Copy the full path of the selection";
    public string Glyph => "\uE71B";
    public HotKey HotKey => new(Key.C, ModifierKeys.Control | ModifierKeys.Shift);
    public bool IsExecutable => context.HasSelection;

    public Task ExecuteAsync(object? parameter = null)
    {
        // CHANGED (zip folders): something inside a ZIP file gives the path as the address bar shows it
        // (C:\Downloads\Photos.zip\Trip\a.jpg). Everything else is unchanged by Display.
        var text = string.Join(Environment.NewLine, context.SelectedPaths.Select(ArchiveLocation.Display));

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }
}

public sealed class NewFolderAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NewFolder;
    public string Label => "New folder";
    public string Description => "Create a folder here";
    public string Glyph => "\uE8F4";
    public HotKey HotKey => new(Key.N, ModifierKeys.Control | ModifierKeys.Shift);
    // CHANGED (your files): also available on the Your files page, where it makes a folder of your own.
    public bool IsExecutable => Directory.Exists(context.CurrentPath) || IsYourFiles;

    private bool IsYourFiles => context.NewLibraryFolder is not null &&
                                context.CurrentPath.Equals(ExplorerLocations.YourFilesPath, StringComparison.OrdinalIgnoreCase);

    public Task ExecuteAsync(object? parameter = null)
    {
        // NEW (your files)
        if (IsYourFiles)
        {
            context.NewLibraryFolder!.Invoke();
            return Task.CompletedTask;
        }

        if (!Directory.Exists(context.CurrentPath))
            return Task.CompletedTask;

        var target = Path.Combine(context.CurrentPath, "New folder");
        var suffix = 2;

        while (Directory.Exists(target) || File.Exists(target))
            target = Path.Combine(context.CurrentPath, $"New folder ({suffix++})");

        try
        {
            Directory.CreateDirectory(target);
            UndoHistory.Record(new CreatedStep(target)); // NEW (undo)
            context.RequestRefresh();
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }
}

public sealed class ShowPropertiesAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.ShowProperties;
    public string Label => "Properties";
    public string Description => "Open the Windows properties dialog";
    public string Glyph => "\uE946";
    public HotKey HotKey => new(Key.Enter, ModifierKeys.Alt);
    // CHANGED (zip folders): Windows' properties window is about a file or folder on a disk, so it is not
    // offered for something inside a ZIP file. With nothing selected inside a ZIP, it is the ZIP file's.
    public bool IsExecutable => context.HasSelection
        ? !context.SelectionInArchive
        : Directory.Exists(context.CurrentPath) || ArchiveLocation.Is(context.CurrentPath);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (context.SelectionInArchive)
            return Task.CompletedTask;

        var path = context.SelectedItems.FirstOrDefault()?.FullPath ?? context.CurrentPath;
        if (ArchiveLocation.TrySplit(path, out var archive, out _))
            path = archive;

        if (!string.IsNullOrEmpty(path))
            FileOperationService.ShowProperties(path, context.OwnerHandle);

        return Task.CompletedTask;
    }
}

internal static class ClipboardHelper
{
    private const string PreferredDropEffect = "Preferred DropEffect";

    public static void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
            return;

        try
        {
            var files = new StringCollection();
            foreach (var path in paths)
                files.Add(path);

            var data = new DataObject();
            data.SetFileDropList(files);

            var effect = new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5));
            data.SetData(PreferredDropEffect, effect);

            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception)
        {
        }
    }

    public static (string[] Paths, bool Cut) GetFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
                return ([], false);

            var list = Clipboard.GetFileDropList();
            var paths = new string[list.Count];
            list.CopyTo(paths, 0);

            var cut = false;
            if (Clipboard.GetDataObject()?.GetData(PreferredDropEffect) is MemoryStream stream)
            {
                var buffer = new byte[4];
                if (stream.Read(buffer, 0, 4) == 4)
                    cut = (BitConverter.ToInt32(buffer, 0) & 2) == 2;
            }

            return (paths, cut);
        }
        catch (Exception)
        {
            return ([], false);
        }
    }
}
