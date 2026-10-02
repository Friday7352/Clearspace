// Clearspace | Main file-browser state and operations.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Clearspace.Commands;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const string MyPcPath = ExplorerLocations.MyPcPath;
    public const string NetworkPath = ExplorerLocations.NetworkPath;
    public const string YourFilesPath = ExplorerLocations.YourFilesPath;
    public const string PinnedPath = ExplorerLocations.PinnedPath;
    public const string CloudPath = ExplorerLocations.CloudPath;
    public const string CategoryPathPrefix = ExplorerLocations.CategoryPathPrefix;

    private readonly NavigationCoordinator _navigationLoads = new();
    private readonly SearchCoordinator _search;

    public MainViewModel()
    {
        _search = new SearchCoordinator(new SearchSources(), update =>
        {
            Items = update.Items;
            IsSearchingTree = update.IsSearching;
            if (update.Status is not null) StatusText = update.Status;
        });
        Navigation = new NavigationService();
        Context = new ExplorerContext { Navigation = Navigation };
        Commands = new CommandManager(Context);

        Navigation.Navigated += async (_, path) => await LoadAsync(path);
        Context.RefreshRequested += async (_, _) => await RefreshAsync();
        Context.SelectionChanged += (_, _) =>
        {
            Commands.RefreshState();
            UpdateStatus();
            OnPropertyChanged(nameof(HasSelectedFolders));
            OnPropertyChanged(nameof(HasCloudSelection));
            OnPropertyChanged(nameof(CanPinToProject)); // NEW (folder types, step 2)
            RefreshTagOptions();
        };

        TagService.Changed += OnTagsChanged;

        FileIndexService.Changed += OnFileIndexChanged;
        LoadColumns();
        RefreshTagOptions();

        SearchEverywhere = SettingsService.GetSearchEverywhere();
        UseWindowsIndex = SettingsService.GetUseWindowsIndex();
        SearchFileContents = SettingsService.GetSearchFileContents();
        ShowHiddenItems = SettingsService.GetShowHiddenItems();
    }

    public NavigationService Navigation { get; }

    public ExplorerContext Context { get; }

    public CommandManager Commands { get; }

    public SidebarViewModel SidebarState { get; } = new();
    public ObservableCollection<SidebarEntry> Sidebar => SidebarState.Sidebar;

    public AudioPlayerViewModel Player { get; } = new();

    public PhotoViewerViewModel Viewer { get; } = new();


    public ObservableCollection<ColumnOption> ColumnOptions { get; } = [];

    public event EventHandler? ColumnsChanged;

    private List<string> _visibleColumns = [];

    public IReadOnlyList<string> VisibleColumns => _visibleColumns;

    public double? GetColumnWidth(string columnId)
        => string.IsNullOrWhiteSpace(CurrentPath)
            ? null
            : SettingsService.GetFolderColumnWidth(CurrentPath, columnId);

    public void SaveColumnWidth(string columnId, double width)
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            SettingsService.SetFolderColumnWidth(CurrentPath, columnId, width);
    }

    public void SaveColumnOrder(IEnumerable<string> columnIds)
    {
        var visible = new HashSet<string>(_visibleColumns, StringComparer.OrdinalIgnoreCase);
        var ordered = ColumnCatalog.Sanitise(columnIds)
            .Where(visible.Contains)
            .ToList();

        if (ordered.Count != _visibleColumns.Count)
            return;

        _visibleColumns = ordered;
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            SettingsService.SetFolderColumns(CurrentPath, _visibleColumns);
    }

    private void LoadColumns()
    {
        // CHANGED (folder types): the type in effect here (own, inherited, or detected) supplies the defaults.
        var type = string.IsNullOrWhiteSpace(CurrentPath) ? FolderTypes.General : FolderTypes.Resolved(CurrentPath);

        var saved = string.IsNullOrWhiteSpace(CurrentPath)
            ? null
            : SettingsService.GetFolderColumns(CurrentPath);

        _visibleColumns = ColumnCatalog.Sanitise(saved ?? FolderTypes.DefaultColumns(type, IsCloudFolder));

        ColumnOptions.Clear();

        foreach (var info in ColumnCatalog.All)
        {
            ColumnOptions.Add(new ColumnOption(
                info,
                _visibleColumns.Contains(info.Id, StringComparer.OrdinalIgnoreCase),
                OnColumnToggled));
        }

        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnToggled(ColumnOption option)
    {
        if (option.IsVisible)
        {
            if (!_visibleColumns.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
            {
                var target = ColumnCatalog.All
                    .TakeWhile(info => !info.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(info => _visibleColumns.FindIndex(id => id.Equals(info.Id, StringComparison.OrdinalIgnoreCase)))
                    .Where(index => index >= 0)
                    .DefaultIfEmpty(-1)
                    .Max();

                _visibleColumns.Insert(target + 1, option.Id);
            }
        }
        else
        {
            _visibleColumns.RemoveAll(id => id.Equals(option.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(CurrentPath))
            SettingsService.SetFolderColumns(CurrentPath, _visibleColumns);

        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetColumns()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            SettingsService.ClearFolderColumns(CurrentPath);

        LoadColumns();
    }

    public void PlayTrack(FileSystemItem item)
    {
        if (item.IsAudio)
            Player.Play(Items, item);
    }

    public void ViewPhoto(FileSystemItem item)
    {
        if (item.IsImageFile)
            Viewer.Open(Items, item);
    }

    public RichCommand BackCommand => Commands[CommandCode.NavigateBack];
    public RichCommand ForwardCommand => Commands[CommandCode.NavigateForward];
    public RichCommand UpCommand => Commands[CommandCode.NavigateUp];
    public RichCommand HomeCommand => Commands[CommandCode.NavigateHome];
    public RichCommand RefreshCommand => Commands[CommandCode.Refresh];
    public RichCommand OpenCommand => Commands[CommandCode.OpenItem];
    public RichCommand DeleteCommand => Commands[CommandCode.Delete];
    public RichCommand RenameCommand => Commands[CommandCode.Rename];
    public RichCommand CopyCommand => Commands[CommandCode.CopyItem];
    public RichCommand CutCommand => Commands[CommandCode.CutItem];
    public RichCommand PasteCommand => Commands[CommandCode.PasteItem];
    public RichCommand CopyPathCommand => Commands[CommandCode.CopyPath];
    public RichCommand NewFolderCommand => Commands[CommandCode.NewFolder];
    public RichCommand PropertiesCommand => Commands[CommandCode.ShowProperties];
    public RichCommand TerminalCommand => Commands[CommandCode.OpenTerminal];

    private IReadOnlyList<FileSystemItem> _items = [];
    public IReadOnlyList<FileSystemItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    private IReadOnlyList<FileSystemItem> _directoryItems = [];

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;

            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(ListGrouping));   // NEW (folder types, step 2)
            OnPropertyChanged(nameof(ShowsProjectStrip)); // NEW (folder types, step 2)
            ApplySearchFilter(updateStatus: true);
        }
    }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    private bool _searchEverywhere;
    public bool SearchEverywhere
    {
        get => _searchEverywhere;
        set
        {
            if (!SetProperty(ref _searchEverywhere, value))
                return;

            SettingsService.SetSearchEverywhere(value);
            ApplySearchFilter(updateStatus: true);
        }
    }


    public ObservableCollection<TagOption> TagOptions { get; } = [];

    private void RefreshTagOptions()
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();

        TagOptions.Clear();

        foreach (var tag in TagService.All)
        {
            var applied = paths.Length > 0 && paths.All(path => TagService.HasTag(path, tag.Id));
            TagOptions.Add(new TagOption(tag, applied, OnTagToggled));
        }
    }

    private void OnTagToggled(TagOption option)
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        if (!TryUpdateTags(() => TagService.ToggleForAll(paths, option.Tag.Id))) return;
        RefreshVisibleTags();

        var count = paths.Length == 1 ? "1 item" : $"{paths.Length:N0} items";
        StatusText = option.IsApplied
            ? $"Tagged {count} as {option.Tag.Name}."
            : $"Removed {option.Tag.Name} from {count}.";
    }

    public void CreateTagForSelection(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        TagDefinition? tag = null;
        if (!TryUpdateTags(() => tag = TagService.CreateForPaths(name, paths))) return;

        RefreshVisibleTags();
        RefreshTagOptions();
        StatusText = paths.Length == 0
            ? $"Created the {tag!.Name} tag."
            : $"Tagged {paths.Length:N0} item{(paths.Length == 1 ? string.Empty : "s")} as {tag!.Name}.";
    }

    public void ClearTagsOnSelection()
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        if (!TryUpdateTags(() => TagService.ClearTags(paths))) return;
        RefreshVisibleTags();
        RefreshTagOptions();
        StatusText = $"Cleared tags on {paths.Length:N0} item{(paths.Length == 1 ? string.Empty : "s")}.";
    }

    public void DeleteTag(TagDefinition tag)
    {
        if (!TryUpdateTags(() => TagService.Delete(tag.Id))) return;
        RefreshVisibleTags();
        RefreshTagOptions();

        if (HasSearch)
            ApplySearchFilter(updateStatus: true);

        StatusText = $"Deleted the {tag.Name} tag.";
    }

    private bool TryUpdateTags(Action update)
    {
        try { update(); return true; }
        catch (InvalidOperationException exception)
        {
            // Restore a toggled checkbox from committed data after a failed write.
            try { RefreshTagOptions(); }
            catch (InvalidOperationException) { TagOptions.Clear(); }
            StatusText = exception.Message;
            return false;
        }
    }

    public void SearchByTag(TagDefinition tag)
    {
        SearchEverywhere = true;
        SearchText = $"tag:{tag.Id}";
    }

    // NEW (lock icons): re-reads the lock badge of every listed row after a lock operation.
    internal void RefreshLockBadges()
    {
        foreach (var item in _directoryItems)
            item.RefreshLock();

        if (!ReferenceEquals(Items, _directoryItems))
        {
            foreach (var item in Items)
                item.RefreshLock();
        }
    }

    private void RefreshVisibleTags()
    {
        foreach (var item in _directoryItems)
            item.RefreshTags();

        if (!ReferenceEquals(Items, _directoryItems))
        {
            foreach (var item in Items)
                item.RefreshTags();
        }
    }

    private string _currentPath = string.Empty;
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                Context.CurrentPath = value;
                OnPropertyChanged(nameof(Breadcrumbs));

                OnPropertyChanged(nameof(FolderProfileLabel));
            }
        }
    }

    private string _addressText = string.Empty;
    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    private LayoutMode _layout = LayoutMode.Details;
    public LayoutMode Layout
    {
        get => _layout;
        private set
        {
            if (SetProperty(ref _layout, value))
            {
                OnPropertyChanged(nameof(IsGrid));
                OnPropertyChanged(nameof(IsDetails));
                OnPropertyChanged(nameof(ListGrouping)); // NEW (folder types, step 2)
            }
        }
    }

    public bool IsGrid => Layout == LayoutMode.Grid;

    public bool IsDetails => Layout == LayoutMode.Details;

    // CHANGED (folder types): the folder's type is a FolderType definition (built-in or custom), possibly
    // inherited from a parent folder whose type applies to subfolders. FolderProfile is its built-in family.
    private FolderType _folderType = FolderTypes.Automatic;
    private string? _folderTypeInheritedFrom;

    public FolderType FolderType => _folderType;
    public DirectoryViewProfile FolderProfile => _folderType.Base;
    public string? FolderTypeInheritedFrom => _folderTypeInheritedFrom;

    public bool FolderTypeAppliesToSubfolders
        => CanSetFolderProfile && _folderTypeInheritedFrom is null && !_folderType.IsAutomatic &&
           SettingsService.GetFolderTypeAppliesToSubfolders(CurrentPath);

    // NEW (folder types, step 2): the type whose behaviors apply here - the effective type, or for
    // Automatic, the type detected from the folder's name and contents (Screenshots, Code, ...).
    private FolderType _resolvedType = FolderTypes.General;
    public FolderType ResolvedFolderType => _resolvedType;

    private void ApplyFolderType(FolderType type, string? inheritedFrom)
    {
        _folderType = type;
        _folderTypeInheritedFrom = inheritedFrom;
        _resolvedType = !CanSetFolderProfile
            ? FolderTypes.General
            : type.IsAutomatic ? FolderTypes.Detected(CurrentPath, lookAtContents: true) : type;

        OnPropertyChanged(nameof(FolderType));
        OnPropertyChanged(nameof(FolderProfile));
        OnPropertyChanged(nameof(FolderTypeInheritedFrom));
        OnPropertyChanged(nameof(FolderTypeAppliesToSubfolders));
        OnPropertyChanged(nameof(FolderProfileLabel));
        OnPropertyChanged(nameof(FolderTypeTooltip));
        OnPropertyChanged(nameof(IsAutomaticProfile));
        OnPropertyChanged(nameof(IsGeneralProfile));
        OnPropertyChanged(nameof(IsPhotosProfile));
        OnPropertyChanged(nameof(IsMusicProfile));
        OnPropertyChanged(nameof(ResolvedFolderType));   // NEW (step 2)
        OnPropertyChanged(nameof(ListGrouping));      // NEW (step 2)
        OnPropertyChanged(nameof(IsProjectFolder));      // NEW (step 2)
        OnPropertyChanged(nameof(ShowsProjectStrip));    // NEW (step 2)
        OnPropertyChanged(nameof(ProjectTitle));         // NEW (step 2)

        LoadColumns();
    }

    public string FolderProfileLabel => FolderTypes.Label(_folderType, CurrentPath);

    // CHANGED (folder types, step 3): says what Automatic picked and why it matters.
    public string FolderTypeTooltip => _folderTypeInheritedFrom is { } from
        ? $"Folder type: {_folderType.Name}, applied to this folder by {from}"
        : _folderType.IsAutomatic && _resolvedType.Id != FolderTypes.General.Id
            ? $"Automatic picked {_resolvedType.Name}: {_resolvedType.Description}. Choose a type to keep one."
            : "Choose a folder type for this folder";

    public bool IsAutomaticProfile => _folderType.IsAutomatic;
    public bool IsGeneralProfile => FolderProfile == DirectoryViewProfile.General;
    // CHANGED (folder types, step 3): the detected type counts too (an Automatic folder full of songs gets
    // the player), and Screenshots and Design & 3D get the photo viewer.
    public bool IsPhotosProfile => _resolvedType.PhotoViewer;
    public bool IsMusicProfile => _resolvedType.Base == DirectoryViewProfile.Music;

    // NEW (folder types, step 3): whether visible columns need file properties (title, pages, length...).
    public bool NeedsFileProperties => ColumnCatalog.NeedsFileProperties(_visibleColumns);
    public bool CanSetFolderProfile => !string.IsNullOrWhiteSpace(CurrentPath) &&
                                       !CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase);
    public bool HasSelectedFolders => Context.SelectedItems.Any(item => item.IsStandardFolder);

    private double _tileScale = 1;
    private bool _restoringTileScale;
    public double TileScale
    {
        get => _tileScale;
        private set
        {
            var clamped = Math.Clamp(value, 0.70, 2.20);
            if (!SetProperty(ref _tileScale, clamped)) return;
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileHeight));
            OnPropertyChanged(nameof(TilePreviewSize));
            OnPropertyChanged(nameof(TilePreviewAreaHeight));
            OnPropertyChanged(nameof(TileZoomText));

            if (!_restoringTileScale && !string.IsNullOrWhiteSpace(CurrentPath))
                SettingsService.SetFolderTileScale(CurrentPath, clamped);
        }
    }

    public double TileWidth => Math.Ceiling(132 * TileScale);
    public double TilePreviewSize => Math.Ceiling(104 * TileScale);
    public double TilePreviewAreaHeight => Math.Max(118, TilePreviewSize + 14);
    public double TileHeight => Math.Ceiling(TilePreviewAreaHeight + 104);
    public string TileZoomText => $"{TileScale * 100:N0}%";

    public void AdjustTileScale(double delta) => TileScale += delta;

    private void RestoreTileScale(string path)
    {
        _restoringTileScale = true;
        try
        {
            // CHANGED (folder types, step 2): the type's starting size when the folder has none of its own.
            TileScale = SettingsService.GetFolderTileScale(path) ?? _resolvedType.TileScale ?? 1;
        }
        finally
        {
            _restoringTileScale = false;
        }
    }

    private const int GridItemLimit = 3000;

    public void SetLayout(LayoutMode layout)
    {
        if (layout == LayoutMode.Grid && Items.Count > GridItemLimit)
        {
            StatusText = $"Too many items for tiles ({Items.Count:N0}). Staying in details.";
            return;
        }

        if (Layout == layout)
            return;

        Layout = layout;

        if (layout == LayoutMode.Details)
            _ = EnsureItemIconsAsync();

        if (!string.IsNullOrEmpty(CurrentPath))
            SettingsService.SetFolderLayout(CurrentPath, layout.ToString());
    }

    public void ToggleLayout()
        => SetLayout(Layout == LayoutMode.Details ? LayoutMode.Grid : LayoutMode.Details);

    public void SetFolderProfile(DirectoryViewProfile profile) => SetFolderType(FolderTypes.ForProfile(profile));

    // CHANGED (folder types): any type, built-in or custom. Its layout comes from the definition.
    public void SetFolderType(FolderType type)
    {
        if (!CanSetFolderProfile)
            return;

        SettingsService.SetFolderViewProfile(CurrentPath, type.Id);
        if (!type.IsAutomatic)
            SettingsService.SetFolderTypeAppliesToSubfolders([CurrentPath], type.SubfoldersByDefault);

        // Automatic may now pick up a parent's type, so resolve rather than assume.
        RestoreFolderProfile(CurrentPath);

        var preferredLayout = !_folderType.IsAutomatic && _folderType.Layout is { } typed
            ? typed
            : ResolveLayout(CurrentPath, Items);

        if (preferredLayout == LayoutMode.Grid && Items.Count > GridItemLimit)
            preferredLayout = LayoutMode.Details;

        Layout = preferredLayout;
        if (Layout == LayoutMode.Details)
            _ = EnsureItemIconsAsync();

        // NEW (folder types, step 2): the new type's sort, tile size, dimming, Git and project strip.
        RestoreTileScale(CurrentPath);
        if (ApplyTypeSort())
            ResortDirectoryItems();
        MarkGenerated(_directoryItems);
        _ = RefreshFolderExtrasAsync(CurrentPath);
    }

    // NEW (folder types): whether this folder's type also applies to the folders below it.
    public void SetFolderTypeAppliesToSubfolders(bool applies)
    {
        if (!CanSetFolderProfile || FolderTypes.AssignedTo(CurrentPath).IsAutomatic)
            return;

        SettingsService.SetFolderTypeAppliesToSubfolders([CurrentPath], applies);
        OnPropertyChanged(nameof(FolderTypeAppliesToSubfolders));
        StatusText = applies
            ? $"{_folderType.Name} now applies to every folder inside this one (unless a folder has its own type)."
            : $"{_folderType.Name} now applies to this folder only.";
    }

    // NEW (folder types): save the current layout and columns as a custom type, and apply it here.
    public void SaveViewAsFolderType(string name)
    {
        name = name.Trim();
        if (!CanSetFolderProfile || name.Length == 0)
            return;

        var existing = FolderTypes.Find(name);
        if (existing is { IsCustom: false })
        {
            StatusText = $"\"{name}\" is a built-in folder type. Choose another name.";
            return;
        }

        var basis = FolderTypes.Resolved(CurrentPath);
        var data = new CustomFolderTypeData
        {
            Id = existing?.Id ?? FolderTypes.NewCustomId(name),
            Name = name,
            Base = (basis.Base == DirectoryViewProfile.Automatic ? DirectoryViewProfile.General : basis.Base).ToString(),
            Layout = Layout.ToString(),
            Columns = [.. _visibleColumns],
            Subfolders = true,
            // NEW (folder types, step 2): the sort and tile size are part of the saved view too.
            Sort = SortColumn.ToString(),
            SortDescending = SortDescending,
            TileScale = Math.Abs(TileScale - 1) < 0.01 ? null : TileScale
        };

        SettingsService.SaveCustomFolderType(data);
        SetFolderType(FolderTypes.FromData(data));
        StatusText = existing is null
            ? $"Saved \"{name}\" as a folder type and applied it here and to subfolders."
            : $"Updated the \"{name}\" folder type.";
    }

    // NEW (folder types): remove a custom type; folders using it go back to Automatic.
    public void DeleteFolderType(FolderType type)
    {
        if (!type.IsCustom)
            return;

        SettingsService.DeleteCustomFolderType(type.Id);
        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            RestoreFolderProfile(CurrentPath);
            Layout = ResolveLayout(CurrentPath, Items);
        }

        StatusText = $"Deleted the \"{type.Name}\" folder type.";
    }

    public void SetFolderProfilesForSelection(DirectoryViewProfile profile) => SetFolderTypeForSelection(FolderTypes.ForProfile(profile));

    public void SetFolderTypeForSelection(FolderType type)
    {
        var folders = Context.SelectedItems
            .Where(item => item.IsStandardFolder)
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (folders.Length == 0)
        {
            StatusText = "Select one or more folders to set their folder type.";
            return;
        }

        SettingsService.SetFolderViewProfiles(folders, type.Id);
        if (!type.IsAutomatic)
            SettingsService.SetFolderTypeAppliesToSubfolders(folders, type.SubfoldersByDefault); // NEW

        foreach (var item in Context.SelectedItems.Where(item => item.IsStandardFolder))
        {
            item.Thumbnail = null;
            item.GridPlaceholder = null;
            item.Icon = IconService.GetIcon(item);
        }

        var folderLabel = folders.Length == 1 ? "folder" : "folders";
        var typeLabel = type.IsAutomatic ? "automatic" : type.Name;
        StatusText = $"Set {typeLabel} view for {folders.Length:N0} {folderLabel}.";
    }


    private DateTime _lastIndexReport = DateTime.MinValue;

    private void OnFileIndexChanged(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        if (FileIndexService.IsBuilding && now - _lastIndexReport < TimeSpan.FromMilliseconds(400))
            return;

        _lastIndexReport = now;

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            OnPropertyChanged(nameof(IndexStatusText));
            OnPropertyChanged(nameof(IndexTooltip));
        });
    }

    public string IndexStatusText
    {
        get
        {
            var status = FileIndexService.Status;
            return status.Length > 0 ? status : "Index starting…";
        }
    }

    public string IndexTooltip
    {
        get
        {
            var lines = new List<string>
            {
                $"Clearspace {BuildVersion}  ·  built {BuildStamp}",
                string.Empty,
                $"{FileIndexService.Count:N0} items  ·  about {FileSystemItem.FormatSize(FileIndexService.EstimatedBytes)} in memory"
            };

            foreach (var skipped in FileIndexService.SkippedRoots)
                lines.Add($"{skipped} too large to index — searched by crawling");

            lines.Add(string.Empty);
            lines.Add(FileIndexStore.FilePath);

            return string.Join(Environment.NewLine, lines);
        }
    }

    public static string BuildStamp
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;

                return string.IsNullOrEmpty(path) || !File.Exists(path)
                    ? "unknown"
                    : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }

    public static string BuildVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

    private string _statusText = "Ready";
    internal void ReportFileLock(string message) => StatusText = message;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _timingText = string.Empty;
    public string TimingText
    {
        get => _timingText;
        private set
        {
            if (SetProperty(ref _timingText, value))
                OnPropertyChanged(nameof(HasTiming));
        }
    }

    public bool HasTiming => !string.IsNullOrEmpty(TimingText);

    private string _hubTitle = string.Empty;
    public string HubTitle
    {
        get => _hubTitle;
        private set => SetProperty(ref _hubTitle, value);
    }

    private string _hubSummary = string.Empty;
    public string HubSummary
    {
        get => _hubSummary;
        private set => SetProperty(ref _hubSummary, value);
    }

    private string _hubDescription = string.Empty;
    public string HubDescription
    {
        get => _hubDescription;
        private set => SetProperty(ref _hubDescription, value);
    }

    public bool IsHub => !string.IsNullOrEmpty(HubTitle);

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private bool _useWindowsIndex;
    public bool UseWindowsIndex
    {
        get => _useWindowsIndex;
        set
        {
            if (!SetProperty(ref _useWindowsIndex, value))
                return;

            SettingsService.SetUseWindowsIndex(value);

            if (HasSearch)
                ApplySearchFilter(updateStatus: true);
        }
    }

    public bool IsWindowsIndexAvailable => WindowsSearchService.IsAvailable;

    public void OpenIndexingOptions()
    {
        if (!WindowsSearchService.OpenIndexingOptions())
            StatusText = "Could not open Windows indexing options.";
    }

    private bool _searchFileContents;

    public bool SearchFileContents
    {
        get => _searchFileContents;
        set
        {
            if (!SetProperty(ref _searchFileContents, value))
                return;

            SettingsService.SetSearchFileContents(value);

            if (HasSearch)
                ApplySearchFilter(updateStatus: true);
        }
    }

    private bool _showHiddenItems;
    public bool ShowHiddenItems
    {
        get => _showHiddenItems;
        set
        {
            if (!SetProperty(ref _showHiddenItems, value))
                return;

            SettingsService.SetShowHiddenItems(value);
            _ = RefreshAsync();
        }
    }

    private SortColumn _sortColumn = SortColumn.Name;
    public SortColumn SortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    private bool _sortDescending;
    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    public IReadOnlyList<Breadcrumb> Breadcrumbs => BuildBreadcrumbs(CurrentPath);


    private bool _isCloudFolder;

    public bool IsCloudFolder
    {
        get => _isCloudFolder;
        private set => SetProperty(ref _isCloudFolder, value);
    }

    private string _cloudRootName = string.Empty;

    public string CloudRootName
    {
        get => _cloudRootName;
        private set => SetProperty(ref _cloudRootName, value);
    }

    private string? _accessDeniedPath;

    public string? AccessDeniedPath
    {
        get => _accessDeniedPath;
        private set
        {
            if (SetProperty(ref _accessDeniedPath, value))
            {
                OnPropertyChanged(nameof(IsAccessDenied));
                OnPropertyChanged(nameof(CanRetryElevated));
            }
        }
    }

    public bool IsAccessDenied => _accessDeniedPath is not null;

    public bool CanRetryElevated => IsAccessDenied && !ElevationService.IsElevated;

    public string WindowTitle => ElevationService.IsElevated
        ? "Clearspace \u00b7 Administrator"
        : "Clearspace";

    public void OpenCurrentElevated()
    {
        var target = AccessDeniedPath ?? CurrentPath;

        if (string.IsNullOrWhiteSpace(target))
            return;

        if (!ElevationService.TryRelaunchAt(target, out var message) && message is not null)
            StatusText = message;
    }


    public bool HasCloudSelection => Context.SelectedItems.Any(item => item.IsCloudItem);

    public async Task SetCloudPinStateAsync(bool pinned)
    {
        var targets = Context.SelectedItems
            .Where(item => item.IsCloudItem)
            .Select(item => item.FullPath)
            .ToArray();

        if (targets.Length == 0)
            return;

        StatusText = pinned ? "Keeping on this device\u2026" : "Freeing up space\u2026";

        try
        {
            await Task.Run(() =>
            {
                foreach (var target in targets)
                    CloudStorageService.SetPinned(target, pinned);
            });
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            return;
        }

        await RefreshAsync();

        var noun = targets.Length == 1 ? "item" : "items";
        StatusText = pinned
            ? $"{targets.Length:N0} {noun} will be kept on this device"
            : $"{targets.Length:N0} {noun} released to the cloud";
    }

    public void Start(string? initialPath = null)
    {
        var start = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
            ? initialPath
            : KnownFolders.Profile;

        Navigation.Navigate(start);

        _ = SidebarState.LoadDrivesAsync();

        FileIndexService.Start();
        DiskUsageSnapshotCache.Start(); // NEW (round 19): keep disk-map sizes warm so the view opens instantly
    }

    public Task RefreshAsync() => LoadAsync(CurrentPath, force: true);

    public void Sort(SortColumn column)
    {
        SortDescending = column == SortColumn && !SortDescending;
        SortColumn = column;

        // NEW (folder types, step 2): a sort you choose becomes your usual sort, unless this folder's
        // type sets its own (then it lasts for this visit).
        if (!_typeSortActive)
        {
            _userSortColumn = SortColumn;
            _userSortDescending = SortDescending;
        }

        ResortDirectoryItems();
    }

    private void ResortDirectoryItems()
    {
        var sorted = _directoryItems.ToList();
        sorted.Sort(new ItemComparer(SortColumn, SortDescending));
        SetDirectoryItems(sorted);
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            FolderSnapshotCache.Set(CurrentPath, sorted);
        OnPropertyChanged(nameof(ListGrouping));
    }

    // ------------------------------------------------------------------ NEW (folder types, step 2)

    private SortColumn _userSortColumn = SortColumn.Name;
    private bool _userSortDescending;
    private bool _typeSortActive;

    // Applies the resolved type's sort, or goes back to your usual sort. True when the sort changed.
    private bool ApplyTypeSort()
    {
        var (column, descending) = (SortColumn, SortDescending);

        if (_resolvedType.Sort is { } typed)
        {
            SortColumn = typed;
            SortDescending = _resolvedType.SortDescending;
            _typeSortActive = true;
        }
        else if (_typeSortActive)
        {
            SortColumn = _userSortColumn;
            SortDescending = _userSortDescending;
            _typeSortActive = false;
        }

        OnPropertyChanged(nameof(ListGrouping));
        return column != SortColumn || descending != SortDescending;
    }

    // NEW (folder types, step 3): re-resolve an Automatic folder after its contents were seen.
    private void ReapplyDetectedType(string path)
    {
        // Windows' folders, names and repositories were already known; only reapply when the answer moved.
        if (FolderTypes.Detected(path, lookAtContents: true).Id == _resolvedType.Id)
            return;

        RestoreFolderProfile(path);

        RestoreTileScale(path);
        if (ApplyTypeSort())
            ResortDirectoryItems();
        else
            SetDirectoryItems(_directoryItems);

        Layout = ResolveLayout(path, _directoryItems);
        if (Layout == LayoutMode.Details)
            _ = EnsureItemIconsAsync();
    }

    // Screenshots: details view grouped under Today / Yesterday / ... while sorted by date.
    // CHANGED (folder types, step 3): the item property the details view groups by, or null.
    // Date groups (Screenshots, Downloads, Archives) only while sorted by date; Kind groups (Desktop) always.
    public string? ListGrouping
    {
        get
        {
            if (!IsDetails || HasSearch)
                return null;

            return _resolvedType.Grouping switch
            {
                FolderGrouping.Date when SortColumn == SortColumn.DateModified => nameof(FileSystemItem.DateGroup),
                FolderGrouping.Kind => nameof(FileSystemItem.KindGroup),
                _ => null
            };
        }
    }

    // Code: build output and dependency folders are dimmed.
    private void MarkGenerated(IReadOnlyList<FileSystemItem> items)
    {
        var dim = _resolvedType.DimGenerated;

        for (var i = 0; i < items.Count; i++)
            items[i].IsGenerated = dim && items[i].IsFolder && GeneratedFolders.IsGenerated(items[i].Name);
    }

    // Code: "git: main · 3 changed" beside the folder type button.
    private string _folderContextText = string.Empty;
    public string FolderContextText
    {
        get => _folderContextText;
        private set
        {
            if (SetProperty(ref _folderContextText, value))
                OnPropertyChanged(nameof(HasFolderContext));
        }
    }

    public bool HasFolderContext => _folderContextText.Length > 0;

    // Projects: the folder the Projects type is assigned to (this one, or the one it is inherited from).
    public bool IsProjectFolder => _resolvedType.ShowProjectStrip && CanSetFolderProfile;
    public bool ShowsProjectStrip => IsProjectFolder && !HasSearch;
    public string? ProjectRoot => IsProjectFolder ? _folderTypeInheritedFrom ?? CurrentPath : null;
    public string ProjectTitle => ProjectRoot is { } root ? Path.GetFileName(root.TrimEnd('\\')) : string.Empty;

    public ObservableCollection<ProjectStripItem> ProjectStrip { get; } = [];

    private string _projectStripHint = string.Empty;
    public string ProjectStripHint
    {
        get => _projectStripHint;
        private set => SetProperty(ref _projectStripHint, value);
    }

    private CancellationTokenSource? _extrasCancel;

    // Git status (Code) and the project strip (Projects), read in the background after a folder opens.
    private async Task RefreshFolderExtrasAsync(string path)
    {
        _extrasCancel?.Cancel();
        _extrasCancel = new CancellationTokenSource();
        var token = _extrasCancel.Token;

        var wantsGit = _resolvedType.Base == DirectoryViewProfile.Code && CanSetFolderProfile;
        var projectRoot = ProjectRoot;

        if (!wantsGit)
        {
            FolderContextText = string.Empty;
            GitService.Apply(null, _directoryItems);
        }

        if (projectRoot is null)
            ProjectStrip.Clear();

        try
        {
            if (projectRoot is not null)
            {
                var pins = SettingsService.GetProjectPins(projectRoot);
                var strip = await Task.Run(() => ProjectFiles.Build(projectRoot, pins, 8, token), token);

                if (token.IsCancellationRequested || !path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
                    return;

                ProjectStrip.Clear();
                foreach (var item in strip)
                    ProjectStrip.Add(item);

                ProjectStripHint = strip.Count == 0
                    ? "Nothing here yet. Right-click a file and choose Pin to project."
                    : string.Empty;
            }

            if (wantsGit)
            {
                var snapshot = await GitService.ReadAsync(path, token);

                if (token.IsCancellationRequested || !path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
                    return;

                GitService.Apply(snapshot, _directoryItems);
                FolderContextText = snapshot is null ? string.Empty : GitService.Describe(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public bool CanPinToProject => IsProjectFolder && Context.SelectedItems.Count > 0;

    public void PinSelectionToProject(bool pin)
    {
        if (ProjectRoot is not { } root)
            return;

        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        SettingsService.SetProjectPins(root, paths, pin);
        _ = RefreshFolderExtrasAsync(CurrentPath);

        var count = paths.Length == 1 ? "1 item" : $"{paths.Length:N0} items";
        StatusText = pin ? $"Pinned {count} to {ProjectTitle}." : $"Unpinned {count} from {ProjectTitle}.";
    }

    public void UnpinFromProject(ProjectStripItem item)
    {
        if (ProjectRoot is not { } root)
            return;

        SettingsService.SetProjectPins(root, [item.FullPath], pinned: false);
        _ = RefreshFolderExtrasAsync(CurrentPath);
    }

    public void OpenProjectItem(ProjectStripItem item)
    {
        if (item.IsFolder)
        {
            Navigation.Navigate(item.FullPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusText = $"Could not open {item.Name}: {exception.Message}";
        }
    }

    public void ShowProjectItemInFolder(ProjectStripItem item)
    {
        var folder = Path.GetDirectoryName(item.FullPath);
        if (!string.IsNullOrEmpty(folder))
            Navigation.Navigate(folder);
    }

    public void ApplyRename(FileSystemItem item, string newFullPath)
    {
        var index = -1;
        for (var i = 0; i < _directoryItems.Count; i++)
        {
            if (ReferenceEquals(_directoryItems[i], item))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            _ = RefreshAsync();
            return;
        }

        TagService.MovePath(item.FullPath, newFullPath);
        item.ApplyRename(newFullPath);

        var reordered = _directoryItems.ToList();
        reordered.RemoveAt(index);

        var comparer = new ItemComparer(SortColumn, SortDescending);
        var insertAt = reordered.FindIndex(existing => comparer.Compare(item, existing) < 0);
        if (insertAt < 0)
            insertAt = reordered.Count;

        reordered.Insert(insertAt, item);

        SetDirectoryItems(reordered);
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            FolderSnapshotCache.Set(CurrentPath, reordered);

        UpdateStatus();
    }

    private void SetDirectoryItems(IReadOnlyList<FileSystemItem> items)
    {
        MarkGenerated(items); // NEW (folder types, step 2)

        // NEW (folder types, step 3): Desktop groups folders, shortcuts and files; keep each group together
        // (stable, so the sort inside each group is unchanged).
        if (_resolvedType.Grouping == FolderGrouping.Kind && items.Count > 1)
            items = [.. items.OrderBy(item => item.KindRank)];

        _directoryItems = items;
        ApplySearchFilter(updateStatus: false);
    }

    private void ApplySearchFilter(bool updateStatus)
        => _ = _search.SearchAsync(new SearchRequest(SearchText, CurrentPath, SearchEverywhere,
            ShowHiddenItems, UseWindowsIndex, SearchFileContents), _directoryItems, updateStatus);

    private void CancelTreeSearch()
    {
        _search.Cancel();
        IsSearchingTree = false;
    }

    private bool _isSearchingTree;
    public bool IsSearchingTree
    {
        get => _isSearchingTree;
        private set => SetProperty(ref _isSearchingTree, value);
    }

    private void UpdateSearchStatus()
    {
        if (HasSearch)
            StatusText = SearchCoordinator.DescribeLocal(SearchQuery.Parse(SearchText), SearchEverywhere, Items.Count);
    }

    private async Task LoadAsync(string path, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var navigationTimer = Stopwatch.StartNew();
        var keepCurrentItems = force &&
                               path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase) &&
                               _directoryItems.Count > 0;
        IReadOnlyList<FileSystemItem> snapshot = [];
        var hasSnapshot = !force && FolderSnapshotCache.TryGet(path, out snapshot);
        long? readyMilliseconds = null;

        using var load = _navigationLoads.BeginLoad();
        var token = load.Token;

        ThumbnailService.CancelPending();
        MediaPropertyService.CancelPending();

        CancelTreeSearch();

        if (!path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            Viewer.Close();
            if (HasSearch)
                SearchText = string.Empty;
        }

        CurrentPath = path;
        RestoreFolderProfile(path);
        RestoreTileScale(path);
        ApplyTypeSort();                       // NEW (folder types, step 2): before the load captures the sort
        FolderContextText = string.Empty;      // NEW (folder types, step 2)

        var cloudRoot = CloudStorageService.RootFor(path);
        IsCloudFolder = cloudRoot is not null;
        CloudRootName = cloudRoot?.Name ?? string.Empty;
        AccessDeniedPath = null;

        LoadColumns();
        AddressText = path;
        IsLoading = true;
        StatusText = hasSnapshot ? "Refreshing cached view…" : "Opening…";
        TimingText = string.Empty;
        ClearHubInfo();

        if (hasSnapshot)
        {
            SetDirectoryItems(snapshot);
            Layout = ResolveLayout(path, snapshot);
            readyMilliseconds = navigationTimer.ElapsedMilliseconds;
            TimingText = $"{readyMilliseconds} ms open · refreshing";
        }
        else if (!keepCurrentItems)
        {
            SetDirectoryItems([]);
            Layout = ResolveLayout(path, []);
        }

        var stopwatch = navigationTimer;

        try
        {
            if (path.Equals(MyPcPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase))
            {
                await LoadVirtualDrivesAsync(path, stopwatch, token);
                return;
            }

            if (path.Equals(YourFilesPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(CloudPath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await LoadHubAsync(path, stopwatch, token);
                return;
            }

            var showHidden = ShowHiddenItems;
            var column = SortColumn;
            var descending = SortDescending;
            var gridFastPath = ResolveLayout(path, []) == LayoutMode.Grid;
            var showPartial = !hasSnapshot && !keepCurrentItems;
            IProgress<IReadOnlyList<FileSystemItem>> partialProgress = new Progress<IReadOnlyList<FileSystemItem>>(batch =>
            {
                if (!showPartial ||
                    !load.IsCurrent ||
                    token.IsCancellationRequested ||
                    !path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SetDirectoryItems(batch);
                Layout = ResolveLayout(path, batch);
                readyMilliseconds ??= navigationTimer.ElapsedMilliseconds;
                StatusText = $"Opening… {batch.Count:N0} items ready";
                TimingText = $"{readyMilliseconds} ms first view";

            });

            var items = await _navigationLoads.LoadDirectoryAsync(load, path,
                new FolderLoadOptions(showHidden, column, descending, gridFastPath, showPartial), partialProgress);

            if (token.IsCancellationRequested)
                return;

            FolderSnapshotCache.Set(path, items);
            SetDirectoryItems(items);

            // NEW (folder types, step 3): Automatic looks at what the folder contains. When that changes
            // the answer (first visit to a folder of photos, say), apply the detected type's view now;
            // next time it is known before the folder opens.
            if (AutomaticFolderTypeDetector.LearnContents(path, items) && _folderType.IsAutomatic)
                ReapplyDetectedType(path);

            _ = RefreshFolderExtrasAsync(path); // NEW (folder types, step 2): Git status, project strip
            stopwatch.Stop();
            readyMilliseconds ??= stopwatch.ElapsedMilliseconds;

            Layout = ResolveLayout(path, items);

            StatusText = SearchCoordinator.DescribeBrowsing(path, items);

            UpdateSearchStatus();

            TimingText = readyMilliseconds < stopwatch.ElapsedMilliseconds
                ? $"{readyMilliseconds} ms open · {stopwatch.ElapsedMilliseconds} ms complete"
                : $"{stopwatch.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            if (!load.IsCurrent) return;
            SetDirectoryItems([]);
            AccessDeniedPath = path;

            StatusText = ElevationService.IsElevated
                ? "Windows refused this folder even with administrator rights"
                : "You don't have permission to view this folder";
        }
        catch (DirectoryNotFoundException)
        {
            if (!load.IsCurrent) return;
            SetDirectoryItems([]);
            StatusText = "That folder no longer exists";
        }
        catch (IOException exception)
        {
            if (!load.IsCurrent) return;
            SetDirectoryItems([]);
            StatusText = exception.Message;
        }
        finally
        {
            if (load.IsCurrent)
            {
                IsLoading = false;
                Commands.RefreshState();
            }
        }
    }

    private async Task LoadVirtualDrivesAsync(string path, Stopwatch stopwatch, CancellationToken token)
    {
        var networkOnly = path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase);
        var drives = await Task.Run(() => LocationCatalog.EnumerateDriveItems(networkOnly), token);

        if (token.IsCancellationRequested)
            return;

        IconService.Populate(drives);
        ScalableIconService.PopulateGridPlaceholders(drives);
        FolderSnapshotCache.Set(path, drives);
        SetDirectoryItems(drives);
        Layout = LayoutMode.Grid;
        stopwatch.Stop();

        var total = drives.Sum(drive => drive.DriveTotalSpace);
        var available = drives.Sum(drive => drive.DriveAvailableSpace);

        StatusText = SearchCoordinator.DescribeBrowsing(networkOnly ? NetworkPath : MyPcPath, drives);
        UpdateSearchStatus();
        SetHubInfo(
            networkOnly ? "Network" : "This PC",
            drives.Count == 0
                ? networkOnly ? "No mapped locations" : "No local drives found"
                : $"{FileSystemItem.FormatSize(available)} available of {FileSystemItem.FormatSize(total)} total",
            networkOnly
                ? "Mapped network locations available to this computer. Select one to browse it."
                : $"{drives.Count:N0} local drive{(drives.Count == 1 ? string.Empty : "s")} · storage bars show the used space for each drive.");
        TimingText = $"{stopwatch.ElapsedMilliseconds} ms";
    }

    private async Task LoadHubAsync(string path, Stopwatch stopwatch, CancellationToken token)
    {
        var isPinnedHub = path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase);
        var isCloudHub = path.Equals(CloudPath, StringComparison.OrdinalIgnoreCase);
        var categoryId = path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[CategoryPathPrefix.Length..]
            : null;
        var items = await Task.Run(() => LocationCatalog.BuildHubItems(isPinnedHub, isCloudHub, categoryId), token);

        if (token.IsCancellationRequested)
            return;

        IconService.Populate(items);
        ScalableIconService.PopulateGridPlaceholders(items);
        FolderSnapshotCache.Set(path, items);
        SetDirectoryItems(items);
        Layout = LayoutMode.Grid;
        stopwatch.Stop();
        StatusText = SearchCoordinator.DescribeBrowsing(path, items);
        UpdateSearchStatus();

        if (isCloudHub)
        {
            SetHubInfo(
                "Cloud",
                items.Count == 0
                    ? "No sync folders"
                    : $"{items.Count:N0} sync folder{(items.Count == 1 ? string.Empty : "s")}",
                "Folders a provider keeps in step with the cloud. Inside them the Status column shows what is actually stored on this device.");
            TimingText = $"{stopwatch.ElapsedMilliseconds} ms";
            return;
        }

        var categoryName = categoryId is null ? null : SettingsService.GetSidebarSections()
            .FirstOrDefault(section => section.Id.Equals($"category:{categoryId}", StringComparison.OrdinalIgnoreCase))?.Name;
        SetHubInfo(
            categoryName ?? (isPinnedHub ? "Favorites" : "Your files"),
            categoryName is not null
                ? items.Count == 0 ? "No items in this category" : $"{items.Count:N0} saved location{(items.Count == 1 ? string.Empty : "s")}" 
                : isPinnedHub
                ? items.Count == 0 ? "Nothing pinned yet" : $"{items.Count:N0} saved location{(items.Count == 1 ? string.Empty : "s")}" 
                : "Six primary folders",
            categoryName is not null
                ? "Drag Favorites into or out of this category to keep your sidebar organized."
                : isPinnedHub
                ? "Pin any file or folder from its right-click menu to keep it within reach."
                : "Desktop, Documents, Downloads, Pictures, Music, and Videos—your everyday starting points.");
        TimingText = $"{stopwatch.ElapsedMilliseconds} ms";
    }

    private void SetHubInfo(string title, string summary, string description)
    {
        HubTitle = title;
        HubSummary = summary;
        HubDescription = description;
        OnPropertyChanged(nameof(IsHub));
    }

    private void ClearHubInfo()
    {
        if (!IsHub)
            return;

        HubTitle = string.Empty;
        HubSummary = string.Empty;
        HubDescription = string.Empty;
        OnPropertyChanged(nameof(IsHub));
    }

    private static LayoutMode ResolveLayout(string path, IReadOnlyList<FileSystemItem> items)
    {
        // CHANGED (folder types): the type in effect (own or inherited) decides, when it has a layout.
        if (FolderTypes.Effective(path) is { IsAutomatic: false, Layout: { } typed })
        {
            if (typed == LayoutMode.Grid && items.Count <= GridItemLimit)
                return LayoutMode.Grid;
            if (typed == LayoutMode.Details)
                return LayoutMode.Details;
        }

        var saved = SettingsService.GetFolderLayout(path);

        if (saved is not null && Enum.TryParse<LayoutMode>(saved, out var chosen))
        {
            if (chosen == LayoutMode.Grid && items.Count > GridItemLimit)
                return LayoutMode.Details;

            return chosen;
        }

        if (items.Count <= GridItemLimit &&
            (KnownFolders.IsWithinPictures(path) ||
             MediaTypes.LooksVisual(items) ||
             FolderTypes.Detected(path, lookAtContents: true).Layout == LayoutMode.Grid)) // CHANGED (step 3): detected type incl. contents
            return LayoutMode.Grid;

        return LayoutMode.Details;
    }

    private void RestoreFolderProfile(string path)
    {
        // CHANGED (folder types): own type, else inherited from a parent, else Automatic.
        var type = FolderTypes.Effective(path, out var inheritedFrom);
        ApplyFolderType(type, inheritedFrom);
        OnPropertyChanged(nameof(CanSetFolderProfile));
    }

    private void UpdateStatus()
    {
        var selected = Context.SelectedItems;

        if (selected.Count == 0)
            return;

        if (selected.Count == 1)
        {
            var item = selected[0];
            StatusText = item.IsFolder
                ? $"{item.Name}  ·  folder"
                : $"{item.Name}  ·  {item.SizeText}";
            return;
        }

        var total = selected.Where(item => !item.IsFolder).Sum(item => item.Size);
        StatusText = $"{selected.Count:N0} selected  ·  {FileSystemItem.FormatSize(total)}";
    }

    private async Task EnsureItemIconsAsync()
    {
        var missing = Items.Where(item => item.Icon is null).ToArray();
        if (missing.Length == 0)
            return;

        var resolved = await Task.Run(() => missing
            .Select(item => (Icon: IconService.GetIcon(item), TypeName: IconService.GetTypeName(item)))
            .ToArray());
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            for (var index = 0; index < missing.Length; index++)
            {
                missing[index].Icon = resolved[index].Icon;
                missing[index].TypeName = resolved[index].TypeName;
            }
        });
    }

    private static IReadOnlyList<Breadcrumb> BuildBreadcrumbs(string path)
        => NavigationBreadcrumbs.BuildBreadcrumbs(path, id => SettingsService.GetSidebarSections()
            .FirstOrDefault(section => section.Id.Equals($"category:{id}", StringComparison.OrdinalIgnoreCase))?.Name);

    public void Dispose()
    {
        _search.Dispose();
        _navigationLoads.Dispose();
        _extrasCancel?.Cancel(); // NEW (folder types, step 2)
        FileIndexService.Changed -= OnFileIndexChanged;
        TagService.Changed -= OnTagsChanged;
    }

    private void OnTagsChanged(object? sender, EventArgs e)
    {
        RefreshTagOptions();
        if (HasSearch) ApplySearchFilter(updateStatus: false);
    }

    public void SetSidebarLocation(string name, string path) => SidebarState.SetSidebarLocation(name, path);
    public void ResetSidebarLocation(string name) => SidebarState.ResetSidebarLocation(name);
    public void PinDirectory(string path) => SidebarState.PinDirectory(path);
    public void UnpinDirectory(string path) => SidebarState.UnpinDirectory(path);
    public void CreatePinnedCategory(string name) => SidebarState.CreatePinnedCategory(name);
    public void RenamePinnedCategory(string id, string name) => SidebarState.RenamePinnedCategory(id, name);
    public void DeletePinnedCategory(string id) => SidebarState.DeletePinnedCategory(id);
    public void TogglePinnedCategory(string id) => SidebarState.TogglePinnedCategory(id);
    public void ToggleSidebarSection(string id) => SidebarState.ToggleSidebarSection(id);
    public void RenameSidebarSection(string id, string name) => SidebarState.RenameSidebarSection(id, name);
    public void MoveSidebarSection(string sourceId, string targetId, bool placeAfter) => SidebarState.MoveSidebarSection(sourceId, targetId, placeAfter);
    public void MovePinnedDirectory(string path, string? categoryId, string? targetPath, bool placeAfter) => SidebarState.MovePinnedDirectory(path, categoryId, targetPath, placeAfter);
    public void MovePinnedCategory(string sourceId, string beforeId) => SidebarState.MovePinnedCategory(sourceId, beforeId);
}
