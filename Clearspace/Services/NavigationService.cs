// Clearspace | Navigation history and current location.

using System.IO;

namespace Clearspace.Services;

public sealed class NavigationService
{
    private readonly List<string> _history = [];
    private int _index = -1;

    public event EventHandler<string>? Navigated;

    public string? CurrentPath => _index >= 0 && _index < _history.Count ? _history[_index] : null;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _history.Count - 1;

    public bool CanGoUp => CurrentPath is not null &&
                           !CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase) &&
                           Directory.GetParent(CurrentPath) is not null;

    public void Navigate(string path)
    {
        path = Normalize(path);

        if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Navigated?.Invoke(this, path);
            return;
        }

        if (_index < _history.Count - 1)
            _history.RemoveRange(_index + 1, _history.Count - _index - 1);

        _history.Add(path);
        _index = _history.Count - 1;

        Navigated?.Invoke(this, path);
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        _index--;
        Navigated?.Invoke(this, _history[_index]);
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        _index++;
        Navigated?.Invoke(this, _history[_index]);
    }

    public void GoUp()
    {
        if (CurrentPath is null || CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase)) return;
        var parent = Directory.GetParent(CurrentPath);
        if (parent is not null)
            Navigate(parent.FullName);
    }

    private static string Normalize(string path)
    {
        path = path.Trim().Trim('"');

        if (path.Length > 3 && (path.EndsWith('\\') || path.EndsWith('/')))
            path = path.TrimEnd('\\', '/');

        return path;
    }
}
