// Clearspace | NEW (batch rename): the dialog for renaming several selected items at once.
// Rename (F2) with more than one item selected opens it. You type a pattern for the new names, optionally
// a piece of text to find and what to put in its place, and see every old name beside its new one before
// anything is renamed. Names that can't be used (a clash, a character Windows refuses) are marked and
// keep the Rename button off. The rules themselves are in Services/BatchRename.cs.
// Built in code from the shared styles in App.xaml and the theme's colours, like MessageDialog.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Services;

namespace Clearspace;

internal sealed class BatchRenameWindow : Window
{
    private const int PreviewRows = 300; // more than this many items: the rest are counted, not listed

    private readonly IReadOnlyList<RenameSource> _sources;
    private readonly HashSet<string> _onDisk = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _pattern;
    private readonly TextBox _find;
    private readonly TextBox _replace;
    private readonly TextBox _start;
    private readonly CheckBox _matchCase;
    private readonly Button _capitals;
    private readonly Button _rename;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary;
    private RenameCase _case = RenameCase.Unchanged;
    private IReadOnlyList<RenameStep> _plan = [];
    private bool _ready;

    // What was renamed; null until Rename succeeded.
    internal RenameOutcome? Outcome { get; private set; }

    internal BatchRenameWindow(IReadOnlyList<RenameSource> sources)
    {
        _sources = sources;
        ReadFolders();

        Title = "Rename";
        Width = 660;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // Header: round icon badge + title (the same pattern as MessageDialog).
        var glyph = new TextBlock
        {
            Text = "", // Rename
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = glyph
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        DockPanel.SetDock(badge, Dock.Left);

        var heading = new TextBlock
        {
            Text = $"Rename {sources.Count:N0} items",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(heading);

        // The fields. Each change works the names out again.
        _pattern = Input("{name}", out var patternFrame);
        _find = Input(string.Empty, out var findFrame);
        _replace = Input(string.Empty, out var replaceFrame);
        _start = Input("1", out var startFrame);
        startFrame.Width = 70;
        startFrame.HorizontalAlignment = HorizontalAlignment.Left;

        var hint = Muted("{name} is the current name, {n} a number ({n:3} gives 001), {date} the date modified. The ending (.jpg) is kept.");
        hint.Margin = new Thickness(0, 6, 0, 0);
        hint.FontSize = 11.5;

        _matchCase = new CheckBox { Content = "Match capitals", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        _matchCase.SetResourceReference(StyleProperty, "ViewerCheck");
        _matchCase.Checked += (_, _) => Refresh();
        _matchCase.Unchecked += (_, _) => Refresh();

        _capitals = new Button { Content = CapitalsLabel(_case), MinWidth = 150, Margin = new Thickness(14, 0, 0, 0), ToolTip = "Click to change" };
        _capitals.SetResourceReference(StyleProperty, "ViewerTextButton");
        _capitals.Click += (_, _) =>
        {
            _case = _case switch
            {
                RenameCase.Unchanged => RenameCase.Lower,
                RenameCase.Lower => RenameCase.Upper,
                RenameCase.Upper => RenameCase.Title,
                _ => RenameCase.Unchanged
            };
            _capitals.Content = CapitalsLabel(_case);
            Refresh();
        };

        var findRow = new Grid();
        findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var joiner = Muted("replace with");
        joiner.VerticalAlignment = VerticalAlignment.Center;
        joiner.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(joiner, 1);
        Grid.SetColumn(replaceFrame, 2);
        Grid.SetColumn(_matchCase, 3);
        findRow.Children.Add(findFrame);
        findRow.Children.Add(joiner);
        findRow.Children.Add(replaceFrame);
        findRow.Children.Add(_matchCase);

        var startRow = new StackPanel { Orientation = Orientation.Horizontal };
        startRow.Children.Add(startFrame);
        startRow.Children.Add(_capitals);

        var patternCell = new StackPanel();
        patternCell.Children.Add(patternFrame);
        patternCell.Children.Add(hint);

        var fields = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(fields, "New name", patternCell);
        AddField(fields, "Find", findRow);
        AddField(fields, "Numbers start at", startRow);

        // The preview: old name, arrow, new name.
        var scroll = new ScrollViewer
        {
            Content = _rows,
            Height = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 8)
        };
        var previewCard = new Border { Margin = new Thickness(0, 18, 0, 0), Child = scroll };
        previewCard.SetResourceReference(Border.BackgroundProperty, "Tint");
        previewCard.SetResourceReference(Border.CornerRadiusProperty, "R8");

        // Bottom row: what will happen on the left, the buttons on the right.
        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var cancel = TextButton("Cancel", primary: false);
        cancel.IsCancel = true; // Esc
        cancel.Click += (_, _) => Close();
        _rename = TextButton("Rename", primary: true);
        _rename.IsDefault = true; // Enter
        _rename.Click += (_, _) => RenameNow();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_rename);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(fields);
        stack.Children.Add(previewCard);
        stack.Children.Add(bottom);

        var card = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Content = card;
        EInkScreen.Frame(this); // the E-reader theme's panel look reaches this dialog too

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            _pattern.Focus();
            _pattern.SelectAll();
        };

        _ready = true;
        Refresh();
    }

    // Everything in the folders the items are in, read once: a new name is checked against this instead of
    // asking the disk again on every key press. A folder that can't be read is left out, and Windows then
    // refuses the rename itself if a name is taken.
    private void ReadFolders()
    {
        var folders = _sources
            .Select(source => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source.FullPath)))
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder!))
                    _onDisk.Add(entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private RenameOptions Options() => new()
    {
        Pattern = _pattern.Text,
        Find = _find.Text,
        Replace = _replace.Text,
        MatchCase = _matchCase.IsChecked == true,
        Start = int.TryParse(_start.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var start) ? start : 1,
        Case = _case
    };

    private void Refresh()
    {
        if (!_ready)
            return;

        _plan = BatchRename.Plan(_sources, Options(), _onDisk.Contains);

        _rows.Children.Clear();
        foreach (var step in _plan.Take(PreviewRows))
            _rows.Children.Add(Row(step));
        if (_plan.Count > PreviewRows)
            _rows.Children.Add(Muted($"… and {_plan.Count - PreviewRows:N0} more"));

        var problems = _plan.Count(step => step.Problem is not null);
        var changing = _plan.Count(step => step.Problem is null && step.Changes);
        if (problems > 0)
        {
            _summary.Text = problems == 1 ? "1 name can't be used." : $"{problems:N0} names can't be used.";
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        else
        {
            _summary.Text = changing == 0
                ? "No names change yet."
                : changing == 1 ? "1 item will be renamed." : $"{changing:N0} items will be renamed.";
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        }

        _rename.IsEnabled = BatchRename.CanApply(_plan);
    }

    private static Grid Row(RenameStep step)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var before = new TextBlock { Text = step.OldName, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = step.OldName };
        before.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");

        var arrow = new TextBlock { Text = "→", Margin = new Thickness(10, 0, 10, 0) };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        Grid.SetColumn(arrow, 1);

        var after = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        if (step.Problem is not null)
        {
            after.Text = step.NewName.Length == 0 ? step.Problem : $"{step.NewName}  ({step.Problem})";
            after.ToolTip = step.Problem;
            after.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        else
        {
            after.Text = step.NewName;
            after.ToolTip = step.NewName;
            after.SetResourceReference(TextBlock.ForegroundProperty, step.Changes ? "Ink" : "InkFaint");
        }
        Grid.SetColumn(after, 2);

        row.Children.Add(before);
        row.Children.Add(arrow);
        row.Children.Add(after);
        return row;
    }

    private void RenameNow()
    {
        if (!BatchRename.CanApply(_plan))
            return;

        var outcome = BatchRename.Apply(_plan);
        if (outcome.Error is not null)
        {
            // Nothing was renamed (or it was put back). Say why and stay open; the disk may have changed.
            _onDisk.Clear();
            ReadFolders();
            Refresh();
            _summary.Text = outcome.Error;
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            return;
        }

        Outcome = outcome;
        DialogResult = true;
    }

    private static string CapitalsLabel(RenameCase value) => value switch
    {
        RenameCase.Lower => "Capitals: all small",
        RenameCase.Upper => "Capitals: ALL CAPITALS",
        RenameCase.Title => "Capitals: Each Word",
        _ => "Capitals: as they are"
    };

    // ---- small builders

    private TextBox Input(string text, out Border frame)
    {
        var box = new TextBox { Text = text };
        box.SetResourceReference(StyleProperty, "PlainTextBox");
        box.TextChanged += (_, _) => Refresh();

        frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Child = box };
        frame.SetResourceReference(Border.BackgroundProperty, "Base");
        frame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
        frame.SetResourceReference(Border.CornerRadiusProperty, "R8");
        return box;
    }

    private static void AddField(Grid table, string label, UIElement field)
    {
        var row = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = Muted(label);
        left.Margin = new Thickness(0, row == 0 ? 8 : 20, 18, 0);
        left.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(left, row);
        table.Children.Add(left);

        var cell = new Border { Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0), Child = field };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 1);
        table.Children.Add(cell);
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 76 };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }
}
