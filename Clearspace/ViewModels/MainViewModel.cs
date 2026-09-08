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

public sealed class MainViewModel : ObservableObject
{
    public const string MyPcPath = "clearspace://my-pc";
    public const string NetworkPath = "clearspace://network";
    public const string YourFilesPath = "clearspace://your-files";
    public const string PinnedPath = "clearspace://pinned";
    public const string CloudPath = "clearspace://cloud";
    public const string CategoryPathPrefix = "clearspace://category/";

    private CancellationTokenSource? _loadCancellation;
    private List<SidebarEntry> _driveEntries = [];

    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<FileSystemItem> _localMatches = [];
    private SearchQuery _pendingQuery = SearchQuery.Empty;

    private const int MaxSearchResults = 10_000;

    public MainViewModel()
    {
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
            RefreshTagOptions();
        };

        TagService.Changed += (_, _) => RefreshTagOptions();

        FileIndexService.Changed += OnFileIndexChanged;

        Sidebar = new ObservableCollection<SidebarEntry>();
        RebuildSidebar();
        LoadColumns();
        RefreshTagOptions();

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = RunTreeSearchAsync(_pendingQuery);
        };

        SearchEverywhere = SettingsService.GetSearchEverywhere();
        UseWindowsIndex = SettingsService.GetUseWindowsIndex();
        SearchFileContents = SettingsService.GetSearchFileContents();
        ShowHiddenItems = SettingsService.GetShowHiddenItems();
    }

    public NavigationService Navigation { get; }

    public ExplorerContext Context { get; }

    public CommandManager Commands { get; }

    public ObservableCollection<SidebarEntry> Sidebar { get; }

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
        var profile = FolderProfile == DirectoryViewProfile.Automatic
            ? AutomaticFolderTypeDetector.DetectFromName(CurrentPath) ?? DirectoryViewProfile.General
            : FolderProfile;

        var saved = string.IsNullOrWhiteSpace(CurrentPath)
            ? null
            : SettingsService.GetFolderColumns(CurrentPath);

        _visibleColumns = ColumnCatalog.Sanitise(saved ?? ColumnCatalog.DefaultsFor(profile, IsCloudFolder));

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

        TagService.ToggleForAll(paths, option.Tag.Id);
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

        var tag = TagService.Create(name);
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();

        if (paths.Length > 0)
        {
            foreach (var path in paths)
                TagService.Assign(path, tag.Id);
        }

        RefreshVisibleTags();
        RefreshTagOptions();
        StatusText = paths.Length == 0
            ? $"Created the {tag.Name} tag."
            : $"Tagged {paths.Length:N0} item{(paths.Length == 1 ? string.Empty : "s")} as {tag.Name}.";
    }

    public void ClearTagsOnSelection()
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        TagService.ClearTags(paths);
        RefreshVisibleTags();
        RefreshTagOptions();
        StatusText = $"Cleared tags on {paths.Length:N0} item{(paths.Length == 1 ? string.Empty : "s")}.";
    }

    public void DeleteTag(TagDefinition tag)
    {
        TagService.Delete(tag.Id);
        RefreshVisibleTags();
        RefreshTagOptions();

        if (HasSearch)
            ApplySearchFilter(updateStatus: true);

        StatusText = $"Deleted the {tag.Name} tag.";
    }

    public void SearchByTag(TagDefinition tag)
    {
        SearchEverywhere = true;
        SearchText = $"tag:{tag.Id}";
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
            }
        }
    }

    public bool IsGrid => Layout == LayoutMode.Grid;

    public bool IsDetails => Layout == LayoutMode.Details;

    private DirectoryViewProfile _folderProfile = DirectoryViewProfile.Automatic;
    public DirectoryViewProfile FolderProfile
    {
        get => _folderProfile;
        private set
        {
            if (!SetProperty(ref _folderProfile, value))
                return;

            OnPropertyChanged(nameof(FolderProfileLabel));
            OnPropertyChanged(nameof(IsAutomaticProfile));
            OnPropertyChanged(nameof(IsGeneralProfile));
            OnPropertyChanged(nameof(IsPhotosProfile));
            OnPropertyChanged(nameof(IsMusicProfile));

            LoadColumns();
        }
    }

    public string FolderProfileLabel => FolderProfile switch
    {
        DirectoryViewProfile.Desktop => "Desktop",
        DirectoryViewProfile.Documents => "Documents",
        DirectoryViewProfile.Downloads => "Downloads",
        DirectoryViewProfile.General => "General",
        DirectoryViewProfile.Photos => "Photos",
        DirectoryViewProfile.Music => "Music",
        DirectoryViewProfile.Videos => "Videos",
        _ => AutomaticFolderTypeDetector.DetectFromName(CurrentPath) switch
        {
            DirectoryViewProfile.Photos => "Automatic (Photos)",
            DirectoryViewProfile.Music => "Automatic (Music)",
            _ => "Automatic"
        }
    };

    public bool IsAutomaticProfile => FolderProfile == DirectoryViewProfile.Automatic;
    public bool IsGeneralProfile => FolderProfile == DirectoryViewProfile.General;
    public bool IsPhotosProfile => FolderProfile == DirectoryViewProfile.Photos;
    public bool IsMusicProfile => FolderProfile == DirectoryViewProfile.Music;
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
            TileScale = SettingsService.GetFolderTileScale(path) ?? 1;
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

    public void SetFolderProfile(DirectoryViewProfile profile)
    {
        if (!CanSetFolderProfile)
            return;

        SettingsService.SetFolderViewProfile(CurrentPath, profile.ToString());
        FolderProfile = profile;

        var preferredLayout = profile switch
        {
            DirectoryViewProfile.Photos or DirectoryViewProfile.Videos => LayoutMode.Grid,
            DirectoryViewProfile.Music or DirectoryViewProfile.General or
                DirectoryViewProfile.Desktop or DirectoryViewProfile.Documents or DirectoryViewProfile.Downloads => LayoutMode.Details,
            _ => ResolveLayout(CurrentPath, Items)
        };

        if (preferredLayout == LayoutMode.Grid && Items.Count > GridItemLimit)
            preferredLayout = LayoutMode.Details;

        Layout = preferredLayout;
        if (Layout == LayoutMode.Details)
            _ = EnsureItemIconsAsync();
    }

    public void SetFolderProfilesForSelection(DirectoryViewProfile profile)
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

        SettingsService.SetFolderViewProfiles(folders, profile.ToString());

        foreach (var item in Context.SelectedItems.Where(item => item.IsStandardFolder))
        {
            item.Thumbnail = null;
            item.GridPlaceholder = null;
            item.Icon = IconService.GetIcon(item);
        }

        var folderLabel = folders.Length == 1 ? "folder" : "folders";
        var typeLabel = profile == DirectoryViewProfile.Automatic ? "automatic" : FolderProfileLabelFor(profile);
        StatusText = $"Set {typeLabel} view for {folders.Length:N0} {folderLabel}.";
    }

    private static string FolderProfileLabelFor(DirectoryViewProfile profile) => profile switch
    {
        DirectoryViewProfile.Desktop => "Desktop",
        DirectoryViewProfile.Documents => "Documents",
        DirectoryViewProfile.Downloads => "Downloads",
        DirectoryViewProfile.General => "General",
        DirectoryViewProfile.Photos => "Photos",
        DirectoryViewProfile.Music => "Music",
        DirectoryViewProfile.Videos => "Videos",
        _ => "Automatic"
    };


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

        _ = LoadDrivesAsync();

        FileIndexService.Start();
    }

    private async Task LoadDrivesAsync()
    {
        List<SidebarEntry> drives;

        try
        {
            drives = await Task.Run(() =>
            {
                _ = CloudStorageService.Roots;
                return EnumerateDrives();
            });
        }
        catch (Exception)
        {
            return;
        }

        _driveEntries = drives;
        RebuildSidebar();
    }

    public Task RefreshAsync() => LoadAsync(CurrentPath, force: true);

    public void Sort(SortColumn column)
    {
        SortDescending = column == SortColumn && !SortDescending;
        SortColumn = column;

        var sorted = _directoryItems.ToList();
        sorted.Sort(new ItemComparer(SortColumn, SortDescending));
        SetDirectoryItems(sorted);
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            FolderSnapshotCache.Set(CurrentPath, sorted);
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
        _directoryItems = items;
        ApplySearchFilter(updateStatus: false);
    }

    private static void ApplyTags(IReadOnlyList<FileSystemItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].RefreshTags();
    }

    private void ApplySearchFilter(bool updateStatus)
    {
        CancelTreeSearch();

        var query = SearchQuery.Parse(SearchText);

        if (query.IsEmpty)
        {
            _localMatches = [];
            Items = _directoryItems;
            return;
        }

        var seed = _directoryItems.Where(query.Matches).ToList();

        if (SearchEverywhere && query.HasIndexFilter)
        {
            var known = new HashSet<string>(seed.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);

            foreach (var item in BuildIndexResults(query))
            {
                if (known.Add(item.FullPath))
                    seed.Add(item);
            }
        }

        _localMatches = seed;
        Items = seed.ToArray();

        if (updateStatus)
            UpdateSearchStatus();

        _pendingQuery = query;
        _searchDebounce.Stop();

        _searchDebounce.Interval = FileIndexService.IsLive
            ? TimeSpan.FromMilliseconds(35)
            : TimeSpan.FromMilliseconds(350);

        _searchDebounce.Start();
    }

    private void FinishIndexResultsAsync(
        IReadOnlyList<FileSystemItem> indexed,
        List<FileSystemItem> found,
        CancellationToken token)
    {
        _ = Task.Run(() =>
        {
            try
            {
                IconService.Populate(indexed);
                IconService.PopulateTypeNames(indexed);

                var missing = FileIndexService.PruneMissing(indexed);

                if (missing.Count == 0 || token.IsCancellationRequested)
                    return;

                var dispatcher = Application.Current?.Dispatcher;

                dispatcher?.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    var gone = new HashSet<string>(
                        missing.Select(item => item.FullPath),
                        StringComparer.OrdinalIgnoreCase);

                    found.RemoveAll(item => gone.Contains(item.FullPath));
                    Items = found.ToArray();
                });
            }
            catch (Exception)
            {
            }
        });
    }

    private void CancelTreeSearch()
    {
        _searchDebounce.Stop();

        var previous = _searchCancellation;
        _searchCancellation = null;

        if (previous is null)
            return;

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        previous.Dispose();
        IsSearchingTree = false;
    }

    private bool _isSearchingTree;
    public bool IsSearchingTree
    {
        get => _isSearchingTree;
        private set => SetProperty(ref _isSearchingTree, value);
    }

    private IReadOnlyList<string> ResolveSearchRoots()
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(CurrentPath) && Directory.Exists(CurrentPath))
            roots.Add(CurrentPath);

        if (!SearchEverywhere)
            return roots;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType == DriveType.Network)
                    continue;

                var root = drive.RootDirectory.FullName;

                if (!roots.Any(existing => existing.Equals(root, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return roots;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private async Task RunTreeSearchAsync(SearchQuery query)
    {
        var roots = ResolveSearchRoots();

        if (query.IsEmpty || roots.Count == 0)
            return;

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;

        var showHidden = ShowHiddenItems;
        var found = new List<FileSystemItem>(_localMatches);
        var seen = new HashSet<string>(found.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        var capped = false;
        var pendingPublish = false;

        var rankTerms = query.Terms;

        long? shownMilliseconds = null;

        var indexAnswersEverything = roots.Count > 0 && roots.All(FileIndexService.Covers);

        IsSearchingTree = !indexAnswersEverything;

        var publishTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        void Publish(bool rank)
        {
            if (!pendingPublish || token.IsCancellationRequested)
                return;

            pendingPublish = false;

            if (rank)
                SearchRanker.Rank(found, rankTerms, CurrentPath);

            Items = found.ToArray();
            shownMilliseconds ??= timer.ElapsedMilliseconds;
            StatusText = SearchEverywhere
                ? $"Searching all drives… {found.Count:N0} found"
                : $"Searching subfolders… {found.Count:N0} found";
        }

        publishTimer.Tick += (_, _) => Publish(rank: false);
        publishTimer.Start();

        var progress = new Progress<IReadOnlyList<FileSystemItem>>(batch =>
        {
            if (token.IsCancellationRequested)
                return;

            foreach (var item in batch)
            {
                if (!seen.Add(item.FullPath))
                    continue;

                item.RefreshTags();
                found.Add(item);
                pendingPublish = true;
            }
        });

        var populatedProgress = new InlineProgress<IReadOnlyList<FileSystemItem>>(batch =>
        {
            IconService.Populate(batch);
            IconService.PopulateTypeNames(batch);
            ((IProgress<IReadOnlyList<FileSystemItem>>)progress).Report(batch);
        });

        try
        {
            var indexed = await Task.Run(
                () => FileIndexService.Search(query, roots, showHidden, MaxSearchResults, token),
                token);

            if (indexed.Count > 0 && !token.IsCancellationRequested)
            {
                foreach (var item in indexed)
                {
                    if (!seen.Add(item.FullPath))
                        continue;

                    item.RefreshTags();
                    found.Add(item);
                }

                SearchRanker.Rank(found, rankTerms, CurrentPath);
                Items = found.ToArray();
                pendingPublish = false;
                shownMilliseconds ??= timer.ElapsedMilliseconds;

                FinishIndexResultsAsync(indexed, found, token);
            }
        }
        catch (OperationCanceledException)
        {
            publishTimer.Stop();
            return;
        }
        catch (Exception)
        {
        }

        var needsWindowsIndex = SearchFileContents || !indexAnswersEverything;

        if (needsWindowsIndex && WindowsSearchService.IsAvailable)
        {
            try
            {
                var fromIndex = await Task.Run(() =>
                {
                    var hits = WindowsSearchService.Search(
                        query, roots, MaxSearchResults, SearchFileContents, token);
                    var materialised = new List<FileSystemItem>(hits.Count);

                    foreach (var hit in hits)
                    {
                        token.ThrowIfCancellationRequested();

                        var item = FileSystemItem.FromLocation(hit.Path);

                        if (item is null)
                            continue;

                        if (!query.MatchesStructural(item))
                            continue;

                        materialised.Add(item);
                    }

                    IconService.Populate(materialised);
                    IconService.PopulateTypeNames(materialised);
                    return materialised;
                }, token);

                if (fromIndex.Count > 0)
                    ((IProgress<IReadOnlyList<FileSystemItem>>)progress).Report(fromIndex);
            }
            catch (OperationCanceledException)
            {
                publishTimer.Stop();
                return;
            }
            catch (Exception)
            {
            }
        }

        try
        {
            if (!indexAnswersEverything)
            {
                capped = await Task.Run(
                    () => FileSearchService.Run(roots, showHidden, query.Matches, populatedProgress, MaxSearchResults, token),
                    token);
            }
        }
        catch (OperationCanceledException)
        {
            publishTimer.Stop();
            return;
        }
        catch (Exception)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                IsSearchingTree = false;
                _searchCancellation = null;
                cancellation.Dispose();
            }
        }

        publishTimer.Stop();

        if (token.IsCancellationRequested)
            return;

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null)
            await dispatcher.InvokeAsync(() => Publish(rank: true), DispatcherPriority.Background);
        else
            Publish(rank: true);

        timer.Stop();

        var scope = SearchEverywhere
            ? "across all drives"
            : "in this folder and subfolders";

        var source = indexAnswersEverything ? "  ·  from index" : string.Empty;

        var elapsed = indexAnswersEverything && shownMilliseconds.HasValue
            ? shownMilliseconds.Value
            : timer.ElapsedMilliseconds;

        StatusText = found.Count switch
        {
            0 => $"No matches {scope}",
            1 => $"1 match {scope}  ·  {elapsed} ms{source}",
            _ => capped
                ? $"First {found.Count:N0} matches {scope}  ·  narrow the search to see fewer"
                : $"{found.Count:N0} matches {scope}  ·  {elapsed} ms{source}"
        };
    }

    private static IReadOnlyList<FileSystemItem> BuildIndexResults(SearchQuery query)
    {
        var results = new List<FileSystemItem>();

        foreach (var path in query.IndexCandidates())
        {
            var item = FileSystemItem.FromLocation(path);
            if (item is null)
                continue;

            item.RefreshTags();

            if (!query.Matches(item))
                continue;

            results.Add(item);
        }

        results.Sort(new ItemComparer(SortColumn.Name, descending: false));
        IconService.Populate(results);
        IconService.PopulateTypeNames(results);
        ScalableIconService.PopulateGridPlaceholders(results);
        return results;
    }

    private void UpdateSearchStatus()
    {
        if (!HasSearch)
            return;

        var query = SearchQuery.Parse(SearchText);
        var scope = SearchEverywhere && query.HasIndexFilter
            ? "here and everywhere tagged"
            : "in this folder";
        var description = query.Describe();

        StatusText = Items.Count switch
        {
            0 => description.Length == 0
                ? $"No matches {scope}"
                : $"No matches {scope} for {description}",
            1 => $"1 match {scope}",
            _ => $"{Items.Count:N0} matches {scope}"
        };
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

        // A new location replaces any pending load so stale results cannot reach the view.
        var previous = _loadCancellation;
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previous.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;

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

            var items = await Task.Run(() =>
            {
                var list = new List<FileSystemItem>();
                var firstBatchWatch = Stopwatch.StartNew();
                var firstBatchReported = false;

                foreach (var item in DirectoryEnumerator.Enumerate(path, showHidden, token))
                {
                    list.Add(item);

                    if (showPartial && !firstBatchReported &&
                        (list.Count >= 256 || firstBatchWatch.ElapsedMilliseconds >= 25))
                    {
                        var firstBatch = list.ToList();
                        firstBatch.Sort(new ItemComparer(column, descending));
                        if (!gridFastPath)
                        {
                            IconService.Populate(firstBatch);
                            IconService.PopulateTypeNames(firstBatch);
                        }
                        ScalableIconService.PopulateGridPlaceholders(firstBatch);
                        ApplyTags(firstBatch);
                        partialProgress.Report(firstBatch);
                        firstBatchReported = true;
                    }
                }

                list.Sort(new ItemComparer(column, descending));
                if (!gridFastPath)
                {
                    IconService.Populate(list);
                    IconService.PopulateTypeNames(list);
                }
                ScalableIconService.PopulateGridPlaceholders(list);
                ApplyTags(list);
                return list;
            }, token);

            if (token.IsCancellationRequested)
                return;

            FolderSnapshotCache.Set(path, items);
            SetDirectoryItems(items);
            stopwatch.Stop();
            readyMilliseconds ??= stopwatch.ElapsedMilliseconds;

            Layout = ResolveLayout(path, items);

            var folders = items.Count(item => item.IsFolder);
            var files = items.Count - folders;

            StatusText = items.Count switch
            {
                0 => "This folder is empty",
                _ => $"{folders:N0} folders, {files:N0} files"
            };

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
            SetDirectoryItems([]);
            AccessDeniedPath = path;

            StatusText = ElevationService.IsElevated
                ? "Windows refused this folder even with administrator rights"
                : "You don't have permission to view this folder";
        }
        catch (DirectoryNotFoundException)
        {
            SetDirectoryItems([]);
            StatusText = "That folder no longer exists";
        }
        catch (IOException exception)
        {
            SetDirectoryItems([]);
            StatusText = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                IsLoading = false;
                Commands.RefreshState();
            }
        }
    }

    private async Task LoadVirtualDrivesAsync(string path, Stopwatch stopwatch, CancellationToken token)
    {
        var networkOnly = path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase);
        var drives = await Task.Run(() => EnumerateDriveItems(networkOnly), token);

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

        StatusText = networkOnly
            ? drives.Count == 0 ? "No mapped network locations" : $"{drives.Count:N0} network location{(drives.Count == 1 ? string.Empty : "s")}" 
            : $"{drives.Count:N0} drive{(drives.Count == 1 ? string.Empty : "s")}";
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
        var items = await Task.Run(() => BuildHubItems(isPinnedHub, isCloudHub, categoryId), token);

        if (token.IsCancellationRequested)
            return;

        IconService.Populate(items);
        ScalableIconService.PopulateGridPlaceholders(items);
        FolderSnapshotCache.Set(path, items);
        SetDirectoryItems(items);
        Layout = LayoutMode.Grid;
        stopwatch.Stop();
        StatusText = items.Count == 0
            ? isPinnedHub ? "No pinned directories yet" : "No locations available"
            : $"{items.Count:N0} location{(items.Count == 1 ? string.Empty : "s")}";
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
        var profileName = SettingsService.GetFolderViewProfile(path);
        if (profileName is not null && Enum.TryParse<DirectoryViewProfile>(profileName, out var profile))
        {
            if ((profile is DirectoryViewProfile.Photos or DirectoryViewProfile.Videos) && items.Count <= GridItemLimit)
                return LayoutMode.Grid;
            if (profile is DirectoryViewProfile.General or DirectoryViewProfile.Music or
                DirectoryViewProfile.Desktop or DirectoryViewProfile.Documents or DirectoryViewProfile.Downloads)
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
             AutomaticFolderTypeDetector.DetectFromName(path) == DirectoryViewProfile.Photos))
            return LayoutMode.Grid;

        return LayoutMode.Details;
    }

    private void RestoreFolderProfile(string path)
    {
        var saved = SettingsService.GetFolderViewProfile(path);
        FolderProfile = saved is not null && Enum.TryParse<DirectoryViewProfile>(saved, out var profile)
            ? profile
            : DirectoryViewProfile.Automatic;
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
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        if (path.Equals(MyPcPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("This PC", MyPcPath)];

        if (path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Network", NetworkPath)];

        if (path.Equals(YourFilesPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Your files", YourFilesPath)];

        if (path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Pinned directories", PinnedPath)];

        if (path.Equals(CloudPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Cloud", CloudPath)];

        if (path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var categoryId = path[CategoryPathPrefix.Length..];
            var name = SettingsService.GetSidebarSections()
                .FirstOrDefault(section => section.Id.Equals($"category:{categoryId}", StringComparison.OrdinalIgnoreCase))?.Name ?? "Category";
            return [new Breadcrumb(name, path)];
        }

        var crumbs = new List<Breadcrumb>();
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                name = current.TrimEnd('\\');

            crumbs.Insert(0, new Breadcrumb(name, current));

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;

            current = parent;
        }

        return crumbs;
    }

    private static IEnumerable<SidebarEntry> BuildSidebarEntries(IEnumerable<SidebarEntry> drives)
    {
        foreach (var section in SettingsService.GetSidebarSections())
        {
            switch (section.Id)
            {
                case "files":
                    yield return Section(section, YourFilesPath);
                    if (!section.IsCollapsed)
                        foreach (var location in BuildUserFileEntries()) yield return Child(location);
                    break;

                case "favorites":
                    yield return Section(section, PinnedPath, isFavorites: true);
                    if (!section.IsCollapsed)
                        foreach (var pin in SettingsService.GetPins(categoryId: null))
                            yield return WithCloud(new SidebarEntry(pin.Value, pin.Key, IsPinned: true, IsChild: true));
                    break;

                case "this-pc":
                    yield return Section(section, MyPcPath);
                    if (!section.IsCollapsed)
                        foreach (var drive in drives.Where(drive => !drive.IsNetworkDrive)) yield return Child(drive);
                    break;

                case "network":
                    yield return Section(section, NetworkPath);
                    if (!section.IsCollapsed)
                        foreach (var drive in drives.Where(drive => drive.IsNetworkDrive)) yield return Child(drive);
                    break;

                case "cloud":
                    if (CloudStorageService.Roots.Count == 0)
                        break;

                    yield return Section(section, CloudPath);
                    if (!section.IsCollapsed)
                        foreach (var root in BuildCloudEntries()) yield return Child(root);
                    break;

                case var _ when section.IsCategory:
                    var categoryId = section.Id["category:".Length..];
                    yield return Section(section, CategoryPathPrefix + categoryId, isCategory: true, categoryId: categoryId);
                    if (!section.IsCollapsed)
                        foreach (var pin in SettingsService.GetPins(categoryId))
                            yield return WithCloud(new SidebarEntry(pin.Value, pin.Key, IsPinned: true, CategoryId: categoryId, IsChild: true));
                    break;
            }
        }
    }

    private static SidebarEntry Section(SidebarSectionInfo section, string path, bool isFavorites = false, bool isCategory = false, string? categoryId = null)
        => new(section.Name, path, IsHeader: true, IsPinnedRoot: isFavorites, IsCategory: isCategory,
            CategoryId: categoryId, IsCollapsed: section.IsCollapsed, IsSection: true, SectionId: section.Id);

    private static SidebarEntry Child(SidebarEntry entry) => WithCloud(entry with { IsChild = true });

    private static SidebarEntry WithCloud(SidebarEntry entry)
        => entry.IsHeader || entry.CloudProvider is not null || !CloudStorageService.IsDiscovered
            ? entry
            : entry with { CloudProvider = CloudStorageService.RootFor(entry.Path)?.Name };

    private static SidebarEntry Entry(string name, string defaultPath)
    {
        var path = SettingsService.GetSidebarOverride(name) ?? defaultPath;

        return new SidebarEntry(
            name,
            path,
            IsKnownFolder: true,
            CloudProvider: CloudStorageService.IsDiscovered
                ? CloudStorageService.RootFor(path)?.Name
                : null);
    }

    public void SetSidebarLocation(string name, string path)
    {
        SettingsService.SetSidebarOverride(name, path);
        RebuildSidebar();
    }

    public void ResetSidebarLocation(string name)
    {
        SettingsService.ClearSidebarOverride(name);
        RebuildSidebar();
    }

    public void PinDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        SettingsService.PinDirectory(path, name);
        RebuildSidebar();
    }

    public void UnpinDirectory(string path)
    {
        SettingsService.UnpinDirectory(path);
        RebuildSidebar();
    }

    public void CreatePinnedCategory(string name)
    {
        SettingsService.CreatePinnedCategory(name);
        RebuildSidebar();
    }

    public void RenamePinnedCategory(string id, string name)
    {
        SettingsService.RenamePinnedCategory(id, name);
        RebuildSidebar();
    }

    public void DeletePinnedCategory(string id)
    {
        SettingsService.DeletePinnedCategory(id);
        RebuildSidebar();
    }

    public void TogglePinnedCategory(string id)
    {
        SettingsService.TogglePinnedCategory(id);
        RebuildSidebar();
    }

    public void ToggleSidebarSection(string id)
    {
        SettingsService.ToggleSidebarSection(id);
        RebuildSidebar();
    }

    public void RenameSidebarSection(string id, string name)
    {
        SettingsService.RenameSidebarSection(id, name);
        RebuildSidebar();
    }

    public void MoveSidebarSection(string sourceId, string targetId, bool placeAfter)
    {
        SettingsService.MoveSidebarSection(sourceId, targetId, placeAfter);
        RebuildSidebar();
    }

    public void MovePinnedDirectory(string path, string? categoryId, string? targetPath, bool placeAfter)
    {
        SettingsService.MovePinnedDirectory(path, categoryId, targetPath, placeAfter);
        RebuildSidebar();
    }

    public void MovePinnedCategory(string sourceId, string beforeId)
    {
        SettingsService.MovePinnedCategory(sourceId, beforeId);
        RebuildSidebar();
    }

    private void RebuildSidebar()
    {
        Sidebar.Clear();

        foreach (var entry in BuildSidebarEntries(_driveEntries))
            Sidebar.Add(entry);
    }

    private static List<SidebarEntry> EnumerateDrives()
    {
        var entries = new List<SidebarEntry>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                entries.Add(new SidebarEntry(
                    $"{label} ({drive.Name.TrimEnd('\\')})",
                    drive.RootDirectory.FullName,
                    IsNetworkDrive: drive.DriveType == DriveType.Network));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return entries;
    }

    private static List<FileSystemItem> EnumerateDriveItems(bool networkOnly)
    {
        var entries = new List<FileSystemItem>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady ||
                    (networkOnly && drive.DriveType != DriveType.Network) ||
                    (!networkOnly && drive.DriveType == DriveType.Network))
                    continue;

                entries.Add(FileSystemItem.FromDrive(drive));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return entries;
    }

    private static List<FileSystemItem> BuildHubItems(bool pinnedOnly, bool cloudOnly, string? categoryId)
    {
        IEnumerable<SidebarEntry> locations = categoryId is not null
            ? SettingsService.GetPins(categoryId).Select(pin => new SidebarEntry(pin.Value, pin.Key, IsPinned: true, CategoryId: categoryId))
            : cloudOnly
            ? BuildCloudEntries()
            : pinnedOnly
            ? SettingsService.GetPinnedDirectories()
                .OrderBy(pin => pin.Value, StringComparer.OrdinalIgnoreCase)
                .Select(pin => new SidebarEntry(pin.Value, pin.Key, IsPinned: true))
            : BuildUserFileEntries();

        return locations
            .Select(location => FileSystemItem.FromLocation(location.Path, location.Name))
            .Where(item => item is not null)
            .Cast<FileSystemItem>()
            .ToList();
    }

    private static IEnumerable<SidebarEntry> BuildCloudEntries()
        => CloudStorageService.Roots.Select(root => new SidebarEntry(root.Name, root.Path, IsKnownFolder: true));

    private static IEnumerable<SidebarEntry> BuildUserFileEntries()
    {
        yield return Entry("Desktop", KnownFolders.Desktop);
        yield return Entry("Documents", KnownFolders.Documents);
        yield return Entry("Downloads", KnownFolders.Downloads);
        yield return Entry("Pictures", KnownFolders.Pictures);
        yield return Entry("Music", KnownFolders.Music);
        yield return Entry("Videos", KnownFolders.Videos);
    }
}

public sealed record Breadcrumb(string Name, string Path);

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

public sealed record SidebarEntry(
    string Name,
    string Path,
    bool IsHeader = false,
    bool IsPinned = false,
    bool IsKnownFolder = false,
    bool IsNetworkDrive = false,
    bool IsPinnedRoot = false,
    bool IsCategory = false,
    string? CategoryId = null,
    bool IsCollapsed = false,
    bool IsSection = false,
    string? SectionId = null,
    bool IsChild = false,
    string? CloudProvider = null)
{
    public string DisplayName => Name;
    public string CollapseGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public bool HasHub => IsSection && !string.IsNullOrWhiteSpace(Path);
    public bool IsNestedPin => IsPinned;

    public bool IsCloudBacked => !string.IsNullOrWhiteSpace(CloudProvider);

    public string CloudHint => IsCloudBacked
        ? $"Backed up by {CloudProvider}"
        : string.Empty;
}
