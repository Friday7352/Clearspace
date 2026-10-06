// Clearspace | NEW (downloads): the review list for the setup files in a Downloads folder.
// Lists every installer found there (Services/InstallerShelf.cs) with what is known about it:
//   - "Looks installed: <program> <version>": the program is on this PC. Ticked to start with.
//   - "Newer than the installed ...": probably an update that has not been run yet. Not ticked.
//   - "Not found among the installed programs". Not ticked.
// You change the ticks as you like; "Move to Recycle Bin" closes the dialog and hands the ticked files
// back to the window, which removes them. Nothing is removed in here, and nothing is ever removed without
// this list having been shown.
// Built in code from the shared styles in App.xaml and the theme's colours, like MessageDialog.
using System.Windows;
using System.Windows.Controls;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

internal sealed class InstallersWindow : Window
{
    private readonly IReadOnlyList<InstallerFile> _files;
    private readonly List<CheckBox> _boxes = [];
    private readonly TextBlock _summary;
    private readonly Button _remove;

    // The files to remove; empty unless "Move to Recycle Bin" was pressed.
    internal IReadOnlyList<string> Chosen { get; private set; } = [];

    internal InstallersWindow(string folderName, IReadOnlyList<InstallerFile> files)
    {
        _files = files;

        Title = "Installers";
        Width = 700;
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
            Text = "\uE7B8", // Package
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
            Text = $"Installers in {folderName}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var installed = files.Count(file => file.State == InstallerState.Installed);
        var lead = Muted(installed == 0
            ? "None of these could be matched to a program on this PC, so none are ticked. Tick the ones you no longer need."
            : "The ticked ones are for programs that are already on this PC, so the setup file is no longer needed. " +
              "The match is by name; untick anything you want to keep.");
        lead.Margin = new Thickness(0, 6, 0, 0);

        var titles = new StackPanel();
        titles.Children.Add(heading);
        titles.Children.Add(lead);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        // The list.
        var rows = new StackPanel();
        foreach (var file in files)
            rows.Children.Add(Row(file));

        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 8)
        };
        var listCard = new Border { Margin = new Thickness(0, 18, 0, 0), Child = scroll };
        listCard.SetResourceReference(Border.BackgroundProperty, "Tint");
        listCard.SetResourceReference(Border.CornerRadiusProperty, "R8");

        // Bottom row: quick ticks and the total on the left, the buttons on the right.
        var all = TextButton("Tick all", primary: false);
        all.Margin = new Thickness(0);
        all.Click += (_, _) => TickWhere(_ => true);
        var known = TextButton("Only installed ones", primary: false);
        known.ToolTip = "Ticks the installers whose program is already on this PC";
        known.IsEnabled = installed > 0;
        known.Click += (_, _) => TickWhere(file => file.State == InstallerState.Installed);
        var none = TextButton("Untick all", primary: false);
        none.Click += (_, _) => TickWhere(_ => false);

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        quick.Children.Add(all);
        quick.Children.Add(known);
        quick.Children.Add(none);

        _summary = Muted(string.Empty);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(0, 0, 14, 0);

        var cancel = TextButton("Cancel", primary: false);
        cancel.IsCancel = true; // Esc
        cancel.Click += (_, _) => Close();
        _remove = TextButton("Move to Recycle Bin", primary: true);
        _remove.Click += (_, _) =>
        {
            Chosen = _boxes.Where(box => box.IsChecked == true).Select(box => ((InstallerFile)box.Tag).FullPath).ToList();
            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_remove);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(listCard);
        stack.Children.Add(quick);
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
        UpdateSummary();
    }

    private Grid Row(InstallerFile file)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new CheckBox { IsChecked = file.State == InstallerState.Installed, VerticalAlignment = VerticalAlignment.Center, Tag = file };
        box.SetResourceReference(StyleProperty, "ViewerCheck");
        box.Click += (_, _) => UpdateSummary();
        _boxes.Add(box);

        var name = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var note = new TextBlock { Text = file.Note, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        note.SetResourceReference(TextBlock.ForegroundProperty, file.State switch
        {
            InstallerState.Installed => "Good",
            InstallerState.NewerThanInstalled => "Warn",
            _ => "InkMuted"
        });
        var names = new StackPanel { Margin = new Thickness(12, 0, 12, 0), ToolTip = file.FullPath };
        names.Children.Add(name);
        names.Children.Add(note);
        Grid.SetColumn(names, 1);

        var facts = Muted($"{FileSystemItem.FormatSize(file.Size)}   {file.Modified:d}");
        facts.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(facts, 2);

        row.Children.Add(box);
        row.Children.Add(names);
        row.Children.Add(facts);
        return row;
    }

    private void TickWhere(Func<InstallerFile, bool> wanted)
    {
        foreach (var box in _boxes)
            box.IsChecked = wanted((InstallerFile)box.Tag);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var ticked = _boxes.Where(box => box.IsChecked == true).Select(box => (InstallerFile)box.Tag).ToList();
        _summary.Text = ticked.Count == 0
            ? $"{_files.Count:N0} {(_files.Count == 1 ? "installer" : "installers")}, {FileSystemItem.FormatSize(_files.Sum(file => file.Size))}"
            : $"{ticked.Count:N0} ticked, {FileSystemItem.FormatSize(ticked.Sum(file => file.Size))}";
        _remove.IsEnabled = ticked.Count > 0;
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
