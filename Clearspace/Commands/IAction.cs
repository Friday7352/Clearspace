// Clearspace | Command contracts and keyboard shortcuts.

using System.Windows.Input;

namespace Clearspace.Commands;


// CS499: Enhancement 1 can add lock and unlock actions without changing the command UI.
public interface IAction
{
    CommandCode Code { get; }

    string Label { get; }

    string Description { get; }

    string Glyph => string.Empty;

    HotKey HotKey => HotKey.None;

    bool IsExecutable => true;

    Task ExecuteAsync(object? parameter = null);
}


public enum CommandCode
{
    None = 0,

    NavigateBack,
    NavigateForward,
    NavigateUp,
    NavigateHome,
    Refresh,
    FocusAddressBar,

    OpenItem,
    Delete,
    DeletePermanently,
    Rename,
    CopyItem,
    CutItem,
    PasteItem,
    CopyPath,
    NewFolder,
    ShowProperties,

    SelectAll,
    ClearSelection,
    InvertSelection,

    ToggleHiddenItems,
    OpenTerminal
}


public readonly record struct HotKey(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public static HotKey None => new(Key.None);

    public bool IsNone => Key == Key.None;

    public string Label
    {
        get
        {
            if (IsNone) return string.Empty;

            var parts = new List<string>(4);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            parts.Add(Key.ToString());

            return string.Join('+', parts);
        }
    }
}
