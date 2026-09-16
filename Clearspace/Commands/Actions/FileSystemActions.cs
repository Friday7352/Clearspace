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

    public Task ExecuteAsync(object? parameter = null)
    {
        foreach (var item in context.SelectedItems)
        {
            if (item.IsFolder)
            {
                context.Navigation.Navigate(item.FullPath);
                return Task.CompletedTask;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.FullPath,
                    UseShellExecute = true
                });
            }
            catch (Exception)
            {
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class DeleteAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.Delete;
    public string Label => "Delete";
    public string Description => "Move the selection to the Recycle Bin";
    public string Glyph => "\uE74D";
    public HotKey HotKey => new(Key.Delete);
    public bool IsExecutable => context.HasSelection;

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
    public bool IsExecutable => context.HasSelection;

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
    public string Description => "Rename the selected item";
    public string Glyph => "\uE8AC";
    public HotKey HotKey => new(Key.F2);
    public bool IsExecutable => context.HasSingleSelection;

    public Task ExecuteAsync(object? parameter = null)
    {
        context.BeginRename?.Invoke(context.SelectedItems.FirstOrDefault());
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
    public bool IsExecutable => context.HasSelection;

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

        var result = cut
            ? FileOperationService.Move(paths, context.CurrentPath, context.OwnerHandle)
            : FileOperationService.Copy(paths, context.CurrentPath, context.OwnerHandle);
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
        var text = string.Join(Environment.NewLine, context.SelectedPaths);

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
    public bool IsExecutable => Directory.Exists(context.CurrentPath);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (!Directory.Exists(context.CurrentPath))
            return Task.CompletedTask;

        var target = Path.Combine(context.CurrentPath, "New folder");
        var suffix = 2;

        while (Directory.Exists(target) || File.Exists(target))
            target = Path.Combine(context.CurrentPath, $"New folder ({suffix++})");

        try
        {
            Directory.CreateDirectory(target);
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
    public bool IsExecutable => context.HasSelection || Directory.Exists(context.CurrentPath);

    public Task ExecuteAsync(object? parameter = null)
    {
        var path = context.SelectedItems.FirstOrDefault()?.FullPath ?? context.CurrentPath;

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
