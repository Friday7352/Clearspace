// Clearspace | NEW (map network drive): giving a shared network folder a drive letter, and taking it away.
//
// Until now a drive had to be mapped in File Explorer before Clearspace could show it. This is the part
// with no windows in it: checking the folder that was typed, working out which letters are free, and the
// two Windows calls. The dialog is MapNetworkDriveWindow.cs; the menus are in MainWindow.NetworkDrives.cs.
//
// Windows does the signing in. WNetAddConnection3 is called with CONNECT_INTERACTIVE, so when the server
// wants a user name and password Windows shows its own sign-in box (the one with "Remember my
// credentials", which keeps them in Credential Manager). Clearspace never sees, holds or stores a password.
//
// Both calls go out to the server and can take many seconds when it does not answer, so neither is made
// on the UI thread: MapAsync runs on a thread of its own (STA, because Windows may show its sign-in box
// from it), and the window calls Disconnect through Task.Run.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Clearspace.Services;

/// <summary>How mapping or disconnecting ended: Windows' error code (0 = done) and words for the user.</summary>
internal readonly record struct NetworkMappingResult(int Code, string Message)
{
    public bool Succeeded => Code == 0;

    /// <summary>The user closed Windows' sign-in box. Nothing went wrong, so there is nothing to report.</summary>
    public bool Cancelled => Code == NetworkMapping.ErrorCancelled;

    /// <summary>Disconnecting was refused because something still has files on the drive open.</summary>
    public bool InUse => Code is NetworkMapping.ErrorOpenFiles or NetworkMapping.ErrorDeviceInUse;
}

internal static class NetworkMapping
{
    internal const string Example = @"\\server\share";

    internal const int ErrorCancelled = 1223;    // ERROR_CANCELLED
    internal const int ErrorOpenFiles = 2401;    // ERROR_OPEN_FILES
    internal const int ErrorDeviceInUse = 2404;  // ERROR_DEVICE_IN_USE
    private const int ErrorNotConnected = 2250;  // ERROR_NOT_CONNECTED
    private const int ErrorConnectionUnavailable = 1201; // remembered, but not connected right now

    private const int ResourceTypeDisk = 1;
    private const int ConnectUpdateProfile = 0x1; // remember it: Windows reconnects at sign-in
    private const int ConnectTemporary = 0x4;     // this session only
    private const int ConnectInteractive = 0x8;   // Windows may ask for a user name and password
    private const int ConnectPrompt = 0x10;       // ... and asks even when the current account would do

    private const int DriveRemoved = 0x80;        // SHCNE_DRIVEREMOVED
    private const int DriveAdded = 0x100;         // SHCNE_DRIVEADD
    private const uint ByPath = 0x5;              // SHCNF_PATHW

    // Characters no server or shared folder name can hold. A colon also rules out C:\folder, and a
    // question mark the \\?\ spelling, neither of which is a network folder.
    private static readonly char[] Refused = ['<', '>', ':', '"', '|', '?', '*'];

    // ------------------------------------------------------------------ what was typed

    /// <summary>
    /// Turns what was typed into \\server\share (or \\server\share\folder). Forward slashes, quotes around
    /// it, spaces at the ends, a trailing slash and smb:// are all accepted. False, with the reason in
    /// <paramref name="problem"/>, when it is not a network folder.
    /// </summary>
    internal static bool TryNormalize(string? typed, out string unc, out string problem)
    {
        unc = string.Empty;
        problem = string.Empty;

        var text = (typed ?? string.Empty).Trim().Trim('"').Trim();
        if (text.Length == 0)
        {
            problem = $"Type the folder to connect to, like {Example}.";
            return false;
        }

        if (text.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            text = @"\\" + text["smb://".Length..];
        text = text.Replace('/', '\\');

        if (!text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            problem = text.Length >= 2 && text[1] == ':'
                ? $"That is a folder on this PC. A network folder starts with two backslashes, like {Example}."
                : $"A network folder starts with two backslashes, like {Example}.";
            return false;
        }

        var parts = text.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            problem = $"Type the server and the shared folder, like {Example}.";
            return false;
        }

        if (parts[0] == "." || parts.Any(part => part.IndexOfAny(Refused) >= 0))
        {
            problem = "A network folder's name can't contain any of  < > : \" | ? *";
            return false;
        }

        if (parts.Length == 1)
        {
            problem = $@"Add the shared folder's name after the server, like \\{parts[0]}\share.";
            return false;
        }

        unc = @"\\" + string.Join('\\', parts);
        return true;
    }

    /// <summary>The server of \\server\share; null when the text is not a network folder.</summary>
    internal static string? ServerOf(string? unc)
        => unc is not null && unc.StartsWith(@"\\", StringComparison.Ordinal) &&
           unc[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries) is [var server, ..]
            ? server
            : null;

    // ------------------------------------------------------------------ drive letters

    /// <summary>The letters that can still be given out, Z first (where File Explorer starts too).</summary>
    internal static char[] FreeLetters(IEnumerable<char> taken)
    {
        var used = new HashSet<char>(taken.Select(char.ToUpperInvariant));
        var free = new List<char>(26);

        for (var letter = 'Z'; letter >= 'A'; letter--)
            if (!used.Contains(letter))
                free.Add(letter);

        return [.. free];
    }

    /// <summary>
    /// The letters free on this PC right now. A letter Windows remembers a network folder for counts as
    /// taken even while that folder is out of reach: Windows refuses to give it out a second time.
    /// Reads the list of drives and the registry, never a server, so it is safe on the UI thread.
    /// </summary>
    internal static char[] FreeLetters()
    {
        var taken = new List<char>(26);

        try
        {
            foreach (var root in Environment.GetLogicalDrives())
                if (root.Length > 0)
                    taken.Add(root[0]);
        }
        catch (Exception)
        {
        }

        try
        {
            using var remembered = Registry.CurrentUser.OpenSubKey("Network");
            if (remembered is not null)
                foreach (var name in remembered.GetSubKeyNames())
                    if (name.Length == 1)
                        taken.Add(name[0]);
        }
        catch (Exception)
        {
        }

        return FreeLetters(taken);
    }

    // ------------------------------------------------------------------ connecting

    /// <summary>
    /// Maps <paramref name="unc"/> to <paramref name="letter"/> without holding up the caller's thread.
    /// <paramref name="owner"/> is the window Windows puts its sign-in box in front of, if it needs one.
    /// </summary>
    internal static Task<NetworkMappingResult> MapAsync(char letter, string unc, bool reconnectAtSignIn, bool askForCredentials, IntPtr owner)
    {
        var done = new TaskCompletionSource<NetworkMappingResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Map(letter, unc, reconnectAtSignIn, askForCredentials, owner));
            }
            catch (Exception error)
            {
                done.SetResult(new NetworkMappingResult(-1, error.Message));
            }
        })
        {
            IsBackground = true,
            Name = "Clearspace map network drive"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return done.Task;
    }

    /// <summary>Maps the folder to the letter. Waits for the server, so never call it on the UI thread.</summary>
    internal static NetworkMappingResult Map(char letter, string unc, bool reconnectAtSignIn, bool askForCredentials, IntPtr owner)
    {
        letter = char.ToUpperInvariant(letter);

        var resource = new NETRESOURCE
        {
            dwType = ResourceTypeDisk,
            lpLocalName = $"{letter}:",
            lpRemoteName = unc
        };

        var flags = ConnectInteractive
                    | (reconnectAtSignIn ? ConnectUpdateProfile : ConnectTemporary)
                    | (askForCredentials ? ConnectPrompt : 0);

        // No user name and no password: Windows tries the account that is signed in, then asks.
        var code = WNetAddConnection3W(owner, ref resource, null, null, flags);
        if (code == 0)
            DriveChanged(DriveAdded, letter);

        return new NetworkMappingResult(code, Describe(code, letter, unc));
    }

    /// <summary>
    /// Disconnects a mapped drive ("Z:\") and makes Windows forget it, so it does not come back at
    /// sign-in. Without <paramref name="force"/> Windows refuses while files on it are open
    /// (<see cref="NetworkMappingResult.InUse"/>). Waits for the server, so never call it on the UI thread.
    /// </summary>
    internal static NetworkMappingResult Disconnect(string root, bool force)
    {
        if (string.IsNullOrEmpty(root) || !char.IsAsciiLetter(root[0]))
            return new NetworkMappingResult(ErrorNotConnected, "That is not a network drive.");

        var letter = char.ToUpperInvariant(root[0]);
        var code = WNetCancelConnection2W($"{letter}:", ConnectUpdateProfile, force);
        if (code == 0)
            DriveChanged(DriveRemoved, letter);

        return new NetworkMappingResult(code, Describe(code, letter, null));
    }

    /// <summary>The \\server\share behind a mapped drive ("Z:\"), from Windows' own table - the server is
    /// not asked. Null when the drive is not a mapped one.</summary>
    internal static string? RemoteName(string root)
    {
        if (string.IsNullOrEmpty(root) || root.Length < 2)
            return null;

        try
        {
            var name = new StringBuilder(512);
            var length = name.Capacity;
            return WNetGetConnectionW(root[..2], name, ref length) is 0 or ErrorConnectionUnavailable && name.Length > 0
                ? name.ToString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Tells the rest of Windows (File Explorer's own windows, open and save dialogs) that a drive came or
    // went, and drops what the disk usage map remembered about the letter: it may be another server next.
    private static void DriveChanged(int what, char letter)
    {
        var root = $@"{letter}:\";
        DriveHardwareProbe.Forget(root);

        try { SHChangeNotify(what, ByPath, root, IntPtr.Zero); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ what went wrong, in words

    /// <summary>Windows' answer as a sentence. Empty for success and for a sign-in box that was closed.</summary>
    internal static string Describe(int code, char letter, string? unc)
    {
        var server = ServerOf(unc) ?? "the server";

        return code switch
        {
            0 or ErrorCancelled => string.Empty,

            // bad network path, not listed, no network knows the path, unreachable, timed out
            51 or 53 or 59 or 64 or 121 or 1203 or 1231 or 1232 or 1256
                => $"Windows couldn't find {server}. Check the name, and that this PC is on the same network or VPN as it.",

            67 => $"{server} answered, but it has no shared folder with that name.",

            // letter already assigned, or remembered for another folder
            85 or 1202 => $"{letter}: is already in use. Choose another letter.",

            // wrong password, sign-in refused, account restricted / expired / disabled / locked out, bad user name
            86 or 1326 or 1327 or 1330 or 1331 or 1909 or 2202
                => $"{server} did not accept that user name and password.",

            5 => $"This account isn't allowed to open that folder on {server}.",

            1219 => $"Windows is already signed in to {server} with a different user name, and it allows one at a time. " +
                    $"Disconnect the other drives from {server} first, or sign in with the same name.",

            1222 => "This PC is not connected to a network.",

            ErrorNotConnected => $"{letter}: is not a network drive any more.",

            ErrorOpenFiles or ErrorDeviceInUse => $"Files on {letter}: are still open.",

            _ => $"{new Win32Exception(code).Message} (Windows error {code})"
        };
    }

    // ------------------------------------------------------------------ Windows

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection3W(IntPtr owner, [In] ref NETRESOURCE resource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);
}
