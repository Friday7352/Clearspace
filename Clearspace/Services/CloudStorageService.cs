// Clearspace | Cloud storage discovery and pinning.

using System.IO;
using Microsoft.Win32;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

public sealed record CloudRoot(string Name, string Path, string Provider);

public static class CloudStorageService
{
    private static IReadOnlyList<CloudRoot>? _roots;
    private static readonly object Gate = new();

    public static IReadOnlyList<CloudRoot> Roots
    {
        get
        {
            if (_roots is not null)
                return _roots;

            lock (Gate)
                return _roots ??= Discover();
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
            _roots = null;
    }

    public static bool IsDiscovered
    {
        get
        {
            lock (Gate)
                return _roots is not null;
        }
    }

    public static CloudRoot? RootFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Roots
            .Where(root => IsWithin(path, root.Path))
            .OrderByDescending(root => root.Path.Length)
            .FirstOrDefault();
    }

    public static bool IsCloudPath(string? path) => RootFor(path) is not null;

    private static bool IsWithin(string path, string root)
    {
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == root.Length ||
               path[root.Length] is '\\' or '/' ||
               root.EndsWith('\\');
    }


    public static CloudSyncState Evaluate(uint attributes)
    {
        const uint managed =
            NativeMethods.FILE_ATTRIBUTE_PINNED |
            NativeMethods.FILE_ATTRIBUTE_UNPINNED |
            NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN |
            NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;

        if ((attributes & managed) == 0)
            return CloudSyncState.None;

        if ((attributes & (NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN |
                           NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0)
            return CloudSyncState.OnlineOnly;

        return (attributes & NativeMethods.FILE_ATTRIBUTE_PINNED) != 0
            ? CloudSyncState.AlwaysAvailable
            : CloudSyncState.Available;
    }


    public static void SetPinned(string path, bool pinned, CancellationToken cancellationToken = default)
    {
        Apply(path, pinned);

        if (!Directory.Exists(path))
            return;

        foreach (var item in DirectoryEnumerator.EnumerateTree(path, showHidden: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Apply(item.FullPath, pinned);
        }
    }

    private static void Apply(string path, bool pinned)
    {
        var current = NativeMethods.GetFileAttributesW(path);

        if (current == NativeMethods.INVALID_FILE_ATTRIBUTES)
            return;

        var updated = pinned
            ? (current | NativeMethods.FILE_ATTRIBUTE_PINNED) & ~NativeMethods.FILE_ATTRIBUTE_UNPINNED
            : (current | NativeMethods.FILE_ATTRIBUTE_UNPINNED) & ~NativeMethods.FILE_ATTRIBUTE_PINNED;

        if (updated != current)
            NativeMethods.SetFileAttributesW(path, updated);
    }


    private static IReadOnlyList<CloudRoot> Discover()
    {
        var found = new List<CloudRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name, string? path, string provider)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
                return;

            path = path.TrimEnd('\\');

            if (!Directory.Exists(path) || !seen.Add(path))
                return;

            found.Add(new CloudRoot(name, path, provider));
        }

        foreach (var (name, path) in ReadOneDriveAccounts())
            Add(name, path, "OneDrive");

        foreach (var (provider, name, path) in ReadSyncRootManager())
            Add(name, path, provider);

        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"), "OneDrive");
        Add("OneDrive for Business", Environment.GetEnvironmentVariable("OneDriveCommercial"), "OneDrive");
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"), "OneDrive");

        return found;
    }

    private static List<(string Name, string Path)> ReadOneDriveAccounts()
    {
        var accounts = new List<(string, string)>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");

            if (root is null)
                return accounts;

            foreach (var accountId in root.GetSubKeyNames())
            {
                using var account = root.OpenSubKey(accountId);

                if (account?.GetValue("UserFolder") is not string folder ||
                    string.IsNullOrWhiteSpace(folder))
                    continue;

                var isBusiness = accountId.StartsWith("Business", StringComparison.OrdinalIgnoreCase);
                var label = account.GetValue("DisplayName") as string;

                var name = !string.IsNullOrWhiteSpace(label)
                    ? $"OneDrive - {label}"
                    : isBusiness ? "OneDrive for Business" : "OneDrive";

                accounts.Add((name, folder));
            }
        }
        catch (Exception)
        {
        }

        return accounts;
    }

    private static List<(string Provider, string Name, string Path)> ReadSyncRootManager()
    {
        var roots = new List<(string, string, string)>();

        try
        {
            using var manager = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");

            if (manager is null)
                return roots;

            foreach (var id in manager.GetSubKeyNames())
            {
                using var entry = manager.OpenSubKey(id);
                using var userRoots = entry?.OpenSubKey("UserSyncRoots");

                if (userRoots is null)
                    continue;

                var provider = id.Split('!')[0];

                foreach (var valueName in userRoots.GetValueNames())
                {
                    if (userRoots.GetValue(valueName) is not string path ||
                        string.IsNullOrWhiteSpace(path))
                        continue;

                    var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));

                    if (string.IsNullOrWhiteSpace(name))
                        name = provider;

                    roots.Add((provider, name, path));
                }
            }
        }
        catch (Exception)
        {
        }

        return roots;
    }
}
