// Clearspace | NEW (Explorer re-lock): which folders the open File Explorer windows (and tabs) are showing,
// read through the Shell.Application automation object Windows provides for this. Used to lock unlocked
// files and folders again once no window is showing them. Must be called on an STA thread (the UI thread).
using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;

internal static class ExplorerWindows
{
    internal static IReadOnlyList<string> Folders()
    {
        var result = new List<string>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return result;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            dynamic windows = ((dynamic)shell!).Windows();
            int count = windows.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic? window = windows.Item(i);
                    if (window is null) continue;
                    string? exe = window.FullName;
                    if (exe is null || !exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue; // skip Internet Explorer windows
                    string? path = window.Document?.Folder?.Self?.Path;
                    if (!string.IsNullOrEmpty(path) && System.IO.Path.IsPathFullyQualified(path))
                        result.Add(path);
                }
                catch (Exception) { } // a window closing while we look at it
            }
        }
        catch (Exception) { }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        return result;
    }
}
