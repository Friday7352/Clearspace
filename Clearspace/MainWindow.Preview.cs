// Clearspace | NEW (preview pane): the pane on the right of the file list that previews the selected item.
// Alt+P or the toolbar button shows and hides it (TogglePreviewPaneAction); the splitter between it and
// the list changes its width. Whether it is shown, and how wide, is remembered (settings.json). What the
// pane draws is in Controls/PreviewPane.cs. The pane is only made the first time it is shown, so a
// window that never uses it pays nothing for it.
using System.Windows;
using Clearspace.Controls;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private const double PreviewMinWidth = 220;
    private const double PreviewMaxWidth = 900;
    private const double ListMinWidthBesidePreview = 280;

    private PreviewPane? _previewPane;
    private bool _previewShown;

    /// <summary>NEW (arrange mode): whether the preview pane is open, and opening or closing it (remembered), for
    /// the theme editor's Arrange panel and for tests.</summary>
    internal bool PreviewShown => _previewShown;

    internal void ShowPreviewPane(bool shown) => SetPreviewShown(shown, remember: true);

    private void InitializePreview()
    {
        _viewModel.Context.TogglePreviewPane = () => SetPreviewShown(!_previewShown, remember: true);
        _viewModel.Context.SelectionChanged += (_, _) =>
        {
            if (_previewShown)
                _previewPane?.Show(_viewModel.Context.SelectedItems);
        };
        PreviewSplitter.DragCompleted += (_, _) =>
        {
            if (_previewShown)
                SettingsService.SetPreviewPaneWidth(PreviewColumn.ActualWidth);
        };

        if (SettingsService.GetShowPreviewPane())
            SetPreviewShown(true, remember: false);
    }

    private void SetPreviewShown(bool shown, bool remember)
    {
        _previewShown = shown;
        PreviewToggle.IsChecked = shown; // CHANGED (lit when open): the toolbar button shows whether the pane is open

        if (shown)
        {
            if (_previewPane is null)
            {
                _previewPane = new PreviewPane();
                PreviewCard.Child = _previewPane;
            }

            ContentColumn.MinWidth = ListMinWidthBesidePreview;
            PreviewSplitterColumn.Width = GridLength.Auto;
            PreviewColumn.MinWidth = PreviewMinWidth;
            PreviewColumn.MaxWidth = PreviewMaxWidth;
            PreviewColumn.Width = new GridLength(SettingsService.GetPreviewPaneWidth());
            PreviewSplitter.Visibility = Visibility.Visible;
            PreviewCard.Visibility = Visibility.Visible;

            _previewPane.Show(_viewModel.Context.SelectedItems);
        }
        else
        {
            PreviewSplitter.Visibility = Visibility.Collapsed;
            PreviewCard.Visibility = Visibility.Collapsed;
            PreviewColumn.MinWidth = 0;
            PreviewColumn.Width = new GridLength(0);
            PreviewSplitterColumn.Width = new GridLength(0);
            ContentColumn.MinWidth = DockShown ? ListMinWidthBesidePreview : 0;   // CHANGED (shelf and inspector): they need the room too

            _previewPane?.Show([]); // drops the picture it was holding
        }

        // CHANGED (complex themes): the gaps round the file list and the pane are set by the theme layout now,
        // which knows whether the pane is on the left or the right (it was ContentCard.Margin = 0,0,0,14 when
        // shown and 0,0,14,14 when hidden).
        ThemeExplorerLayout.Apply(this);

        if (remember)
            SettingsService.SetShowPreviewPane(shown);
    }
}
