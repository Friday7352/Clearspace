// Clearspace | Command registration and lookup.

using Clearspace.Commands.Actions;

namespace Clearspace.Commands;

public sealed class CommandManager
{
    private readonly Dictionary<CommandCode, RichCommand> _commands = [];
    private readonly Dictionary<HotKey, RichCommand> _keyBindings = [];

    public CommandManager(ExplorerContext context)
    {
        Context = context;
        Register(context);
        BuildKeyBindings();
    }

    public ExplorerContext Context { get; }

    public RichCommand this[CommandCode code]
        => _commands.TryGetValue(code, out var command) ? command : _commands[CommandCode.None];

    public IEnumerable<RichCommand> All => _commands.Values;

    private void Register(ExplorerContext context)
    {
        Add(new NoneAction());

        Add(new NavigateBackAction(context));
        Add(new NavigateForwardAction(context));
        Add(new NavigateUpAction(context));
        Add(new NavigateHomeAction(context));
        Add(new RefreshAction(context));
        Add(new FocusAddressBarAction(context));

        Add(new OpenItemAction(context));
        Add(new DeleteAction(context));
        Add(new DeletePermanentlyAction(context));
        Add(new RenameAction(context));
        Add(new CopyItemAction(context));
        Add(new CutItemAction(context));
        Add(new PasteItemAction(context));
        Add(new CopyPathAction(context));
        Add(new NewFolderAction(context));
        Add(new ShowPropertiesAction(context));

        Add(new SelectAllAction(context));
        Add(new ClearSelectionAction(context));
        Add(new InvertSelectionAction(context));

        Add(new OpenTerminalAction(context));

        void Add(IAction action) => _commands[action.Code] = new RichCommand(action, context);
    }

    private void BuildKeyBindings()
    {
        foreach (var command in _commands.Values)
        {
            if (command.HotKey.IsNone)
                continue;

            _keyBindings[command.HotKey] = command;
        }
    }

    public RichCommand? TryGetByHotKey(HotKey hotKey)
        => _keyBindings.TryGetValue(hotKey, out var command) ? command : null;

    public void RefreshState()
    {
        foreach (var command in _commands.Values)
            command.NotifyStateChanged();
    }
}
