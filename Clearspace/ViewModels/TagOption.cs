using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class TagOption : ObservableObject
{
    private readonly Action<TagOption> _onToggled;
    private bool _isApplied;

    public TagOption(TagDefinition tag, bool isApplied, Action<TagOption> onToggled)
    {
        Tag = tag;
        _isApplied = isApplied;
        _onToggled = onToggled;
    }

    public TagDefinition Tag { get; }

    public string Name => Tag.Name;

    public bool IsApplied
    {
        get => _isApplied;
        set
        {
            if (SetProperty(ref _isApplied, value))
                _onToggled(this);
        }
    }
}
