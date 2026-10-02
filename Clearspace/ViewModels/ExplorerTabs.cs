// Clearspace | NEW (tabs): the tabs of the main window.
// Each tab is a place and the road that led there: its own back / forward list (NavigationHistory), plus
// what the window puts aside while the tab is in the background (search text, scroll position, selection).
// There is still one file list and one MainViewModel. Switching tabs attaches the tab's history to the
// NavigationService, which raises Navigated exactly as a click on a folder does, so the tab's folder is
// loaded through the normal path (instantly, from FolderSnapshotCache, when it was shown recently).
// Nothing in this file touches WPF, so it is covered by ordinary unit tests (Clearspace.Tests/TabTests.cs).

using System.Collections.ObjectModel;
using System.IO;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class ExplorerTab : ObservableObject
{
    private readonly Func<string, string> _titleOf;

    internal ExplorerTab(NavigationHistory history, Func<string, string> titleOf)
    {
        History = history;
        _titleOf = titleOf;
        SetLocation(history.CurrentPath ?? string.Empty);
    }

    // This tab's back / forward list. The NavigationService works on it while the tab is active.
    public NavigationHistory History { get; }

    private string _location = string.Empty;
    public string Location => _location;

    private string _title = "New tab";
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    // A symbol from the icon font for the kind of place the tab shows.
    private string _glyph = FolderGlyph;
    public string Glyph
    {
        get => _glyph;
        private set => SetProperty(ref _glyph, value);
    }

    // The tooltip: the full path, or the name for places that have no path (This PC, hubs).
    public string Hint => IsVirtual(_location) || _location.Length == 0 ? Title : _location;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    // True once the window has shown this tab at the place it is in now. A tab opened in the background
    // has not shown anything yet, so it does not keep a visit-unlocked folder open (see LockAgent).
    internal bool WasShown { get; set; }

    // ---- What the window puts aside while the tab is in the background ----

    public string SearchText { get; set; } = string.Empty;

    public double ScrollOffset { get; set; }

    public IReadOnlyList<string> SelectedPaths { get; set; } = [];

    internal void SetLocation(string path)
    {
        if (string.Equals(_location, path, StringComparison.Ordinal) && path.Length > 0)
            return;

        _location = path;
        OnPropertyChanged(nameof(Location));
        Title = path.Length == 0 ? "New tab" : _titleOf(path);
        Glyph = GlyphFor(path);
        OnPropertyChanged(nameof(Hint));
    }

    // The name on the tab: the last part of the address bar's trail ("Documents", "C:", "This PC").
    internal static string DefaultTitle(string path)
    {
        var crumbs = NavigationBreadcrumbs.BuildBreadcrumbs(path);
        return crumbs.Count > 0 ? crumbs[^1].Name : path;
    }

    private const string FolderGlyph = "\uE8B7";

    private static bool IsVirtual(string path)
        => path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase);

    internal static string GlyphFor(string path)
    {
        if (path.Equals(ExplorerLocations.MyPcPath, StringComparison.OrdinalIgnoreCase)) return "\uE977";      // This PC
        if (path.Equals(ExplorerLocations.NetworkPath, StringComparison.OrdinalIgnoreCase)) return "\uE968";   // Network
        if (path.Equals(ExplorerLocations.YourFilesPath, StringComparison.OrdinalIgnoreCase)) return "\uE80F"; // Home
        if (path.Equals(ExplorerLocations.PinnedPath, StringComparison.OrdinalIgnoreCase)) return "\uE734";    // Favorites
        if (path.Equals(ExplorerLocations.CloudPath, StringComparison.OrdinalIgnoreCase)) return "\uE753";     // Cloud
        if (IsVirtual(path)) return FolderGlyph;

        // "C:" or "C:\" is a drive.
        return path.Length is 2 or 3 && path[1] == ':' ? "\uEDA2" : FolderGlyph;
    }
}

public sealed class ExplorerTabs
{
    // How many closed tabs "Reopen closed tab" can bring back.
    private const int MaxClosed = 25;

    private sealed record ClosedTab(NavigationHistory History, int Index, string SearchText);

    private readonly NavigationService _navigation;
    private readonly Func<string> _newTabLocation;
    private readonly Func<string, string> _titleOf;
    private readonly List<ClosedTab> _closed = [];

    // newTabLocation: where a tab made with Ctrl+T or the + button starts.
    // titleOf: the name shown on a tab for a location (MainViewModel passes one that knows category names).
    public ExplorerTabs(NavigationService navigation, Func<string>? newTabLocation = null, Func<string, string>? titleOf = null)
    {
        _navigation = navigation;
        _newTabLocation = newTabLocation ?? (() => ExplorerLocations.MyPcPath);
        _titleOf = titleOf ?? ExplorerTab.DefaultTitle;

        // The window opens with one tab. It takes over the history the service already has.
        Active = new ExplorerTab(navigation.History, _titleOf) { IsActive = true, WasShown = true };
        Items.Add(Active);

        // Every move (a folder click, Back, Up, a tab switch) lands in the active tab.
        navigation.Navigated += (_, path) =>
        {
            Active.SetLocation(path);
            Active.WasShown = true;
        };
    }

    public ObservableCollection<ExplorerTab> Items { get; } = [];

    public ExplorerTab Active { get; private set; }

    public int Count => Items.Count;

    // Raised just before the window leaves a tab, while that tab's folder is still on screen: the moment
    // to put its search text, scroll position and selection aside.
    public event EventHandler<ExplorerTab>? Deactivating;

    // Raised once another tab has taken over and its folder has started loading.
    public event EventHandler<ExplorerTab>? Activated;

    // The only tab was closed. As in Explorer, that closes the window.
    public event EventHandler? LastTabClosed;

    // Tabs were opened, closed, moved or switched (the commands' enabled state may have changed).
    public event EventHandler? Changed;

    // ---- Opening ----

    // A new tab at the end of the strip. path = null starts where new tabs start. With activate = false the
    // tab opens in the background ("Open in new tab", a middle click). Returns null when the tab could not
    // be shown (its folder is locked and the password prompt was cancelled).
    public ExplorerTab? Open(string? path = null, bool activate = true)
        => Insert(NavigationService.CreateHistory(string.IsNullOrWhiteSpace(path) ? _newTabLocation() : path), Items.Count, activate);

    // A second tab on the same folder with the same back / forward list, right beside the first.
    public ExplorerTab? Duplicate(ExplorerTab tab)
    {
        var index = Items.IndexOf(tab);
        return index < 0 ? null : Insert(tab.History.Clone(), index + 1, activate: true);
    }

    private ExplorerTab? Insert(NavigationHistory history, int index, bool activate, string searchText = "")
    {
        var tab = new ExplorerTab(history, _titleOf) { SearchText = searchText };
        Items.Insert(Math.Clamp(index, 0, Items.Count), tab);

        if (activate && !Activate(tab))
        {
            Items.Remove(tab);
            return null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    // ---- Switching ----

    public bool Activate(ExplorerTab tab)
    {
        if (ReferenceEquals(tab, Active))
            return true;

        if (!Items.Contains(tab))
            return false;

        // A tab that sits in a locked folder asks for the password before it is shown again. Cancelling
        // the prompt leaves you on the tab you were on.
        if (!_navigation.MayEnter(tab.History.CurrentPath))
            return false;

        var previous = Active;
        Deactivating?.Invoke(this, previous);
        previous.IsActive = false;

        Active = tab;
        tab.IsActive = true;
        _navigation.Attach(tab.History); // raises Navigated: the tab's folder loads like any other move

        Activated?.Invoke(this, tab);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Ctrl+Tab (direction 1) and Ctrl+Shift+Tab (direction -1), wrapping around at the ends.
    public bool Cycle(int direction)
    {
        if (Items.Count < 2)
            return false;

        var index = Items.IndexOf(Active) + Math.Sign(direction);
        return Activate(Items[(index + Items.Count) % Items.Count]);
    }

    // Ctrl+1 ... Ctrl+8.
    public bool ActivateAt(int index)
        => index >= 0 && index < Items.Count && Activate(Items[index]);

    // Ctrl+9.
    public bool ActivateLast() => Activate(Items[^1]);

    // ---- Closing ----

    public bool Close(ExplorerTab tab)
    {
        var index = Items.IndexOf(tab);
        if (index < 0)
            return false;

        if (Items.Count == 1)
        {
            LastTabClosed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return Remove(tab, index, remember: true);
    }

    // NEW (tab to new window): takes a tab out of this window so that another window can have it (its
    // History, SearchText, ScrollOffset and SelectedPaths go with the tab object). Unlike Close, the tab
    // is not kept for "Reopen closed tab" - it is still open, elsewhere - and the only tab can't be taken:
    // moving it would just be moving the window. False when the tab stayed.
    public bool Detach(ExplorerTab tab)
    {
        var index = Items.IndexOf(tab);
        return index >= 0 && Items.Count > 1 && Remove(tab, index, remember: false);
    }

    private bool Remove(ExplorerTab tab, int index, bool remember)
    {
        // Taking away the tab you are on: the one to its right takes over, or the one to its left when it
        // was the last. If that one can't be shown (a locked folder, prompt cancelled) the others are
        // tried, nearest first; if none can, the tab stays.
        if (ReferenceEquals(tab, Active) && !Neighbours(index).Any(Activate))
            return false;

        if (remember)
            Remember(tab, index);

        tab.IsActive = false;
        Items.Remove(tab);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CloseOthers(ExplorerTab keep)
    {
        if (!Items.Contains(keep) || !Activate(keep))
            return;

        foreach (var tab in Items.Where(tab => !ReferenceEquals(tab, keep)).ToArray())
        {
            Remember(tab, Items.IndexOf(tab));
            Items.Remove(tab);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CloseToRight(ExplorerTab tab)
    {
        var index = Items.IndexOf(tab);
        if (index < 0 || index == Items.Count - 1)
            return;

        // The active tab is one of those being closed: move to this one first.
        if (Items.IndexOf(Active) > index && !Activate(tab))
            return;

        while (Items.Count > index + 1)
        {
            Remember(Items[index + 1], index + 1);
            Items.RemoveAt(index + 1);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool HasTabsToRight(ExplorerTab tab)
    {
        var index = Items.IndexOf(tab);
        return index >= 0 && index < Items.Count - 1;
    }

    private IEnumerable<ExplorerTab> Neighbours(int index)
    {
        for (var distance = 1; distance < Items.Count; distance++)
        {
            if (index + distance < Items.Count) yield return Items[index + distance];
            if (index - distance >= 0) yield return Items[index - distance];
        }
    }

    // ---- Reopening ----

    public bool CanReopen => _closed.Count > 0;

    // Ctrl+Shift+T: the tab closed last comes back where it was, with its back / forward list.
    public ExplorerTab? ReopenClosed()
    {
        if (_closed.Count == 0)
            return null;

        var closed = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);

        var tab = Insert(closed.History, closed.Index, activate: true, closed.SearchText);
        if (tab is null)
            _closed.Add(closed); // its folder is locked and the prompt was cancelled: keep it for another try

        Changed?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    private void Remember(ExplorerTab tab, int index)
    {
        if (tab.History.CurrentPath is null)
            return;

        _closed.Add(new ClosedTab(tab.History, index, tab.SearchText));
        if (_closed.Count > MaxClosed)
            _closed.RemoveAt(0);
    }

    // ---- Reordering ----

    public void Move(ExplorerTab tab, int index)
    {
        var from = Items.IndexOf(tab);
        index = Math.Clamp(index, 0, Items.Count - 1);

        if (from < 0 || from == index)
            return;

        Items.Move(from, index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- For locked folders ----

    // Where the window is showing folders: the active tab, and background tabs that have been shown at
    // the place they are in. Something unlocked for a visit stays unlocked while any of these is inside it.
    public IEnumerable<string> ShownLocations
        => Items.Where(tab => tab.WasShown && tab.Location.Length > 0).Select(tab => tab.Location);

    // Moves every tab that is somewhere `inside` says is off limits to `destination` (a folder is being
    // locked while tabs are still in it). The active tab goes there the normal way; background tabs get
    // it added to their history and load it when they are next shown.
    public void MoveOut(Func<string, bool> inside, string destination)
    {
        destination = NavigationService.Normalize(destination);

        foreach (var tab in Items.ToArray())
        {
            if (tab.History.CurrentPath is not { } current || !inside(current))
                continue;

            if (ReferenceEquals(tab, Active))
            {
                _navigation.Navigate(destination);
                continue;
            }

            tab.History.Push(destination);
            tab.SetLocation(destination);
            tab.WasShown = false;
            tab.ScrollOffset = 0;
            tab.SelectedPaths = [];
            tab.SearchText = string.Empty;
        }
    }

    // The folder above a path, for MoveOut; This PC above a drive.
    public static string ParentOf(string folder)
        => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } parent
            ? parent
            : ExplorerLocations.MyPcPath;
}
