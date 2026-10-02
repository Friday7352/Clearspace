// Clearspace | Navigation history and current location.
// CHANGED (tabs): the back / forward list is now its own object (NavigationHistory) and every tab owns
// one. NavigationService works on whichever history is attached, so Back, Forward and Up always act on
// the tab you are looking at. Nothing else about it changed: one service, one Navigated event.

using System.IO;

namespace Clearspace.Services;

// NEW (tabs): one tab's back / forward list and its place in it.
public sealed class NavigationHistory
{
    internal List<string> Entries { get; } = [];

    internal int Index { get; set; } = -1;

    public string? CurrentPath => Index >= 0 && Index < Entries.Count ? Entries[Index] : null;

    public int Count => Entries.Count;

    // Moves to a new location: whatever was "forward" of here is dropped, as in a browser.
    internal void Push(string path)
    {
        if (Index < Entries.Count - 1)
            Entries.RemoveRange(Index + 1, Entries.Count - Index - 1);

        Entries.Add(path);
        Index = Entries.Count - 1;
    }

    // A separate copy, for "Duplicate tab" and for remembering a closed tab.
    public NavigationHistory Clone()
    {
        var copy = new NavigationHistory();
        copy.Entries.AddRange(Entries);
        copy.Index = Index;
        return copy;
    }
}

public sealed class NavigationService
{
    // CHANGED (tabs): was a List<string> and an index held here; now the attached tab's history.
    private NavigationHistory _history;

    // NEW (tab to new window): a service can start on a history that already exists. That is how a tab
    // dragged out of one window carries its back / forward list into the window made for it.
    public NavigationService(NavigationHistory? history = null) => _history = history ?? new NavigationHistory();

    public event EventHandler<string>? Navigated;

    // NEW (locked folders): asked before moving to a different location; returning false cancels the
    // move (e.g. the password prompt for a locked folder was cancelled). Refreshing the current location
    // is never gated.
    public Func<string, bool>? CanEnter { get; set; }

    // NEW (tabs): the history Back and Forward are working on right now (the active tab's).
    public NavigationHistory History => _history;

    public string? CurrentPath => _history.CurrentPath;

    public bool CanGoBack => _history.Index > 0;

    public bool CanGoForward => _history.Index >= 0 && _history.Index < _history.Entries.Count - 1;

    public bool CanGoUp => CurrentPath is not null &&
                           !CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase) &&
                           Directory.GetParent(CurrentPath) is not null;

    // NEW (tabs): a history that starts at one location (a new tab).
    public static NavigationHistory CreateHistory(string path)
    {
        var history = new NavigationHistory();
        history.Push(Normalize(path));
        return history;
    }

    // NEW (tabs): whether the window may show this location now. The same gate Navigate uses, for a move
    // that does not go through Navigate (switching to a tab that sits in a locked folder).
    public bool MayEnter(string? path)
        => path is null ||
           string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase) ||
           CanEnter is null ||
           CanEnter(path);

    // NEW (tabs): switches to another tab's history and shows where that tab is. Not gated; callers ask
    // MayEnter first.
    public void Attach(NavigationHistory history)
    {
        _history = history;

        if (history.CurrentPath is { } path)
            Navigated?.Invoke(this, path);
    }

    public void Navigate(string path)
    {
        path = Normalize(path);

        if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Navigated?.Invoke(this, path);
            return;
        }

        if (CanEnter is not null && !CanEnter(path)) return; // NEW (locked folders)

        _history.Push(path); // CHANGED (tabs): the truncate-then-add moved into NavigationHistory.Push

        Navigated?.Invoke(this, path);
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        if (CanEnter is not null && !CanEnter(_history.Entries[_history.Index - 1])) return; // NEW (locked folders)
        _history.Index--;
        Navigated?.Invoke(this, _history.Entries[_history.Index]);
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        if (CanEnter is not null && !CanEnter(_history.Entries[_history.Index + 1])) return; // NEW (locked folders)
        _history.Index++;
        Navigated?.Invoke(this, _history.Entries[_history.Index]);
    }

    public void GoUp()
    {
        if (CurrentPath is null || CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase)) return;
        var parent = Directory.GetParent(CurrentPath);
        if (parent is not null)
            Navigate(parent.FullName);
    }

    // CHANGED (tabs): internal (was private) so a tab's starting location is tidied the same way.
    internal static string Normalize(string path)
    {
        path = path.Trim().Trim('"');

        if (path.Length > 3 && (path.EndsWith('\\') || path.EndsWith('/')))
            path = path.TrimEnd('\\', '/');

        return path;
    }
}
