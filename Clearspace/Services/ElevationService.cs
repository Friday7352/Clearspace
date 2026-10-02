// Clearspace | Relaunching a folder with elevated access.

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Clearspace.Native;

namespace Clearspace.Services;

public static class ElevationService
{
    private static bool? _isElevated;

    public static bool IsElevated => _isElevated ??= Evaluate();

    private static bool Evaluate()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryRelaunchAt(string path, out string? message)
    {
        message = null;

        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            message = "Clearspace could not locate its own executable.";
            return false;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(info);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
        {
            return false;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return false;
        }
    }
}
