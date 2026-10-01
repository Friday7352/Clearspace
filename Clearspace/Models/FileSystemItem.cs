// Clearspace | File and folder list item model.

using System.IO;
using System.Windows.Media;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace.Models;

public sealed class FileSystemItem : ObservableObject
{
    private string _name = string.Empty;

    public required string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _fullPath = string.Empty;
    public required string FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    public FileAttributes Attributes { get; init; }
    internal uint ReparseTag { get; init; }

    public long Size { get; init; }

    public DateTime DateModified { get; init; }

    public DateTime DateCreated { get; init; }

    public bool IsDriveRoot { get; init; }

    public string? DriveKind { get; init; }

    public long DriveTotalSpace { get; init; }

    public long DriveAvailableSpace { get; init; }

    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    public bool IsStandardFolder => IsFolder && !IsDriveRoot;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    public bool IsShortcut => !IsFolder &&
        Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    public bool IsAudio => !IsFolder && MediaTypes.IsAudio(Extension);

    public bool IsImageFile => !IsFolder && MediaTypes.IsImage(Extension);

    public bool IsVideoFile => !IsFolder && MediaTypes.IsVideo(Extension); // NEW (folder types, step 3)


    public bool IsInCloudRoot { get; init; }

    public CloudSyncState CloudState
    {
        get
        {
            var state = CloudStorageService.Evaluate((uint)Attributes);

            return state == CloudSyncState.None && IsInCloudRoot
                ? CloudSyncState.Available
                : state;
        }
    }

    public bool IsCloudItem => CloudState != CloudSyncState.None;

    public bool IsOnlineOnly => CloudState == CloudSyncState.OnlineOnly;

    public string CloudStatusGlyph => CloudState switch
    {
        CloudSyncState.OnlineOnly => "\uE753",      // Cloud
        CloudSyncState.Available => "\uE73E",       // CheckMark
        CloudSyncState.AlwaysAvailable => "\uE930", // Completed
        _ => string.Empty
    };

    public string CloudStatusText => CloudState switch
    {
        CloudSyncState.OnlineOnly => "Online-only",
        CloudSyncState.Available => "On this device",
        CloudSyncState.AlwaysAvailable => "Always kept",
        _ => string.Empty
    };

    // CHANGED (themes): status colours come from the active theme (were fixed greys/greens/ambers that
    // only suited the dark background). Read each time, so they follow a theme switch.
    private static Brush CloudRemoteBrush => ThemeService.Quiet;
    private static Brush CloudLocalBrush => ThemeService.Good;
    private static Brush CloudPinnedBrush => ThemeService.Warn;

    public Brush CloudStatusBrush => CloudState switch
    {
        CloudSyncState.OnlineOnly => CloudRemoteBrush,
        CloudSyncState.AlwaysAvailable => CloudPinnedBrush,
        _ => CloudLocalBrush
    };


    private IReadOnlyList<TagDefinition> _tags = [];

    public IReadOnlyList<TagDefinition> Tags
    {
        get => _tags;
        internal set
        {
            if (SetProperty(ref _tags, value))
            {
                OnPropertyChanged(nameof(HasTags));
                OnPropertyChanged(nameof(TagNames));
            }
        }
    }

    public bool HasTags => _tags.Count > 0;

    public string TagNames => string.Join(", ", _tags.Select(tag => tag.Name));

    // CHANGED (lock icons): every place that refreshes a row's tags (folder loads, search results, renames)
    // now also refreshes its lock badge.
    internal void RefreshTags()
    {
        Tags = TagService.TagsFor(FullPath);
        RefreshLock();
    }

    // NEW (lock icons): None, Locked (password needed) or Open (a locked folder unlocked for this visit).
    private LockState _lockState;
    internal LockState LockState
    {
        get => _lockState;
        private set
        {
            if (SetProperty(ref _lockState, value))
            {
                OnPropertyChanged(nameof(HasLockBadge));
                OnPropertyChanged(nameof(LockGlyph));
                OnPropertyChanged(nameof(LockToolTip));
            }
        }
    }

    public bool HasLockBadge => _lockState != LockState.None;
    public string LockGlyph => _lockState == LockState.Open ? "\uE785" : "\uE72E"; // Unlock / Lock
    public string LockToolTip => _lockState switch
    {
        LockState.Open when IsFolder => "Locked folder — unlocked while you're inside",
        LockState.Open => "Locked file — unlocked until you leave this folder", // NEW (unlock vs remove lock)
        LockState.Locked when IsFolder => "Locked folder — asks for your password to open",
        LockState.Locked => "Locked file — asks for your password to open",
        _ => string.Empty
    };

    internal void RefreshLock() => LockState = FileLockRegistry.StateOf(FullPath, IsFolder);



    private string? _mediaTitle;
    private string? _artist;
    private string? _album;
    private uint _trackNumber;
    private TimeSpan _duration;
    private string? _authors;   // NEW (folder types, step 3)
    private uint _pageCount;    // NEW (folder types, step 3)

    public bool HasMediaInfo { get; private set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(_mediaTitle)
        ? Path.GetFileNameWithoutExtension(Name)
        : _mediaTitle;

    public string Artist => _artist ?? string.Empty;

    public string Album => _album ?? string.Empty;

    public string TrackNumberText => _trackNumber > 0 ? _trackNumber.ToString() : string.Empty;

    public uint TrackNumber => _trackNumber;

    public TimeSpan Duration => _duration;

    // NEW (folder types, step 3): documents (Documents, Research).
    public string Authors => _authors ?? string.Empty;
    public uint PageCount => _pageCount;
    public string PageCountText => _pageCount > 0 ? _pageCount.ToString("N0") : string.Empty;

    public bool HasDuration => _duration > TimeSpan.Zero; // NEW (step 3): video tiles show their length

    public string DurationText => _duration <= TimeSpan.Zero
        ? string.Empty
        : _duration.TotalHours >= 1
            ? _duration.ToString(@"h\:mm\:ss")
            : _duration.ToString(@"m\:ss");

    private bool _isNowPlaying;
    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set => SetProperty(ref _isNowPlaying, value);
    }

    internal void ApplyMediaInfo(MediaPropertyService.MediaInfo info)
    {
        _mediaTitle = info.Title;
        _artist = info.Artist;
        _album = info.Album;
        _trackNumber = info.TrackNumber;
        _duration = info.Duration;
        _authors = info.Authors;       // NEW (step 3)
        _pageCount = info.PageCount;   // NEW (step 3)
        HasMediaInfo = true;

        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(TrackNumberText));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasDuration));    // NEW (step 3)
        OnPropertyChanged(nameof(Authors));        // NEW (step 3)
        OnPropertyChanged(nameof(PageCount));      // NEW (step 3)
        OnPropertyChanged(nameof(PageCountText));  // NEW (step 3)
        OnPropertyChanged(nameof(HasMediaInfo));
    }

    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name);

    // ------------------------------------------------------------------ NEW (folder types, step 2)

    // Screenshots and other date-grouped folders: the header this item sits under in details view.
    public string DateGroup => DateGroupFor(DateModified, DateTime.Now);

    internal static string DateGroupFor(DateTime modified, DateTime now)
    {
        if (modified == DateTime.MinValue)
            return "Date unknown";

        var today = now.Date;
        var day = modified.Date;

        if (day >= today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";

        var firstDay = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));

        if (day >= weekStart) return "Earlier this week";
        if (day >= weekStart.AddDays(-7)) return "Last week";

        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (day >= monthStart) return "Earlier this month";
        if (day >= monthStart.AddMonths(-1)) return "Last month";
        if (day.Year == today.Year) return "Earlier this year";

        return day.Year.ToString();
    }

    // NEW (folder types, step 3): Desktop groups folders, shortcuts and files apart.
    public int KindRank => IsFolder ? 0 : IsShortcut ? 1 : 2;
    public string KindGroup => KindRank switch { 0 => "Folders", 1 => "Shortcuts", _ => "Files" };

    // NEW (folder types, step 3): Downloads - the site a file was downloaded from (Windows records it
    // with the file). Read in the background when the Source column is shown.
    private string _sourceText = string.Empty;
    public string SourceText => _sourceText;
    public bool HasSource { get; private set; }

    internal void ApplySource(string? source)
    {
        HasSource = true;
        _sourceText = source ?? string.Empty;
        OnPropertyChanged(nameof(SourceText));
    }

    // Code folders: build output and dependency folders (bin, obj, node_modules, ...) are shown dimmed.
    private bool _isGenerated;
    public bool IsGenerated
    {
        get => _isGenerated;
        internal set => SetProperty(ref _isGenerated, value);
    }

    // Code folders: Git status of this file, or of anything inside this folder.
    private char _gitCode;

    public string GitStatusText => _gitCode switch
    {
        'M' => "Modified",
        'A' => "Added",
        'D' => "Deleted",
        'R' => "Renamed",
        'U' => "Conflict",
        '?' => "New",
        '!' => "Ignored",
        'C' => "Changes inside",
        _ => string.Empty
    };

    // CHANGED (themes): from the active theme.
    private static Brush GitModifiedBrush => ThemeService.Warn;
    private static Brush GitAddedBrush => ThemeService.Good;
    private static Brush GitConflictBrush => ThemeService.Bad;
    private static Brush GitQuietBrush => ThemeService.Quiet;

    public Brush GitStatusBrush => _gitCode switch
    {
        'A' or '?' => GitAddedBrush,
        'U' or 'D' => GitConflictBrush,
        'C' or '!' => GitQuietBrush,
        _ => GitModifiedBrush
    };

    internal void SetGitStatus(char code)
    {
        if (_gitCode == code)
            return;

        _gitCode = code;
        OnPropertyChanged(nameof(GitStatusText));
        OnPropertyChanged(nameof(GitStatusBrush));
    }

    // Design & 3D / Screenshots: pixel size of images and videos, read in the background when shown.
    private string _dimensionsText = string.Empty;
    public string DimensionsText => _dimensionsText;

    public bool HasDimensions { get; private set; }

    internal void ApplyDimensions(uint width, uint height)
    {
        HasDimensions = true;
        _dimensionsText = width > 0 && height > 0 ? $"{width:N0} × {height:N0}" : string.Empty;
        OnPropertyChanged(nameof(DimensionsText));
    }

    public string SizeText => IsDriveRoot
        ? $"{FormatSize(DriveAvailableSpace)} free of {FormatSize(DriveTotalSpace)}"
        : IsFolder ? string.Empty : FormatSize(Size);

    public double DriveUsagePercent => DriveTotalSpace <= 0
        ? 0
        : Math.Clamp((DriveTotalSpace - DriveAvailableSpace) * 100d / DriveTotalSpace, 0, 100);

    // CHANGED (themes): from the active theme. (The Frozen helper that built the fixed colours is gone.)
    private static Brush DriveFullBrush => ThemeService.Bad;
    private static Brush DriveWarnBrush => ThemeService.Warn;
    private static Brush DriveOkBrush => ThemeService.Good;

    public Brush DriveUsageBrush => DriveUsagePercent switch
    {
        >= 90 => DriveFullBrush,
        >= 75 => DriveWarnBrush,
        _ => DriveOkBrush
    };

    public string DateModifiedText => DateModified == DateTime.MinValue
        ? string.Empty
        : DateModified.ToString("g");

    public string DateCreatedText => DateCreated == DateTime.MinValue
        ? string.Empty
        : DateCreated.ToString("g");

    private string? _typeName;
    public string TypeName
    {
        get => _typeName ??= IconService.GetTypeName(this);
        set => SetProperty(ref _typeName, value);
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(DisplayImage));
                OnPropertyChanged(nameof(GridImage));
            }
        }
    }

    private ImageSource? _gridPlaceholder;
    public ImageSource? GridPlaceholder
    {
        get => _gridPlaceholder;
        internal set
        {
            if (SetProperty(ref _gridPlaceholder, value))
                OnPropertyChanged(nameof(GridImage));
        }
    }

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(DisplayImage));
                OnPropertyChanged(nameof(GridImage));
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public ImageSource? DisplayImage => _thumbnail ?? _icon;

    public ImageSource? GridImage => _thumbnail ?? _gridPlaceholder ?? _icon;

    internal static FileSystemItem FromFindData(string directory, in NativeMethods.WIN32_FIND_DATA data, bool inCloudRoot = false)
    {
        var name = data.cFileName;
        var size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

        return new FileSystemItem
        {
            Name = name,
            FullPath = Path.Combine(directory, name),
            Attributes = data.dwFileAttributes,
            ReparseTag = data.dwReserved0,
            Size = size,
            IsInCloudRoot = inCloudRoot,
            DateModified = ToDateTime(data.ftLastWriteTime),
            DateCreated = ToDateTime(data.ftCreationTime)
        };
    }

    internal static FileSystemItem FromDrive(DriveInfo drive)
    {
        var root = drive.RootDirectory.FullName;
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
        var kind = drive.DriveType == DriveType.Network ? "Network drive" : "Local drive";

        return new FileSystemItem
        {
            Name = $"{label} ({root.TrimEnd('\\')})",
            FullPath = root,
            Attributes = FileAttributes.Directory,
            IsDriveRoot = true,
            DriveKind = kind,
            DriveTotalSpace = drive.TotalSize,
            DriveAvailableSpace = drive.AvailableFreeSpace
        };
    }

    internal static FileSystemItem? FromLocation(string path, string? displayName = null)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            var name = displayName ?? Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name))
                name = path;

            var inCloudRoot = CloudStorageService.IsCloudPath(path);

            if (isFolder)
            {
                var info = new DirectoryInfo(path);
                return new FileSystemItem
                {
                    Name = name,
                    FullPath = path,
                    Attributes = attributes,
                    IsInCloudRoot = inCloudRoot,
                    DateModified = info.LastWriteTime,
                    DateCreated = info.CreationTime
                };
            }

            var file = new FileInfo(path);
            return new FileSystemItem
            {
                Name = name,
                FullPath = path,
                Attributes = attributes,
                Size = file.Length,
                IsInCloudRoot = inCloudRoot,
                DateModified = file.LastWriteTime,
                DateCreated = file.CreationTime
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static DateTime ToDateTime(NativeMethods.FILETIME fileTime)
    {
        var ticks = fileTime.ToLong();
        if (ticks <= 0)
            return DateTime.MinValue;

        try
        {
            return DateTime.FromFileTime(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    internal void ApplyRename(string newFullPath)
    {
        var previousExtension = Extension;
        var wasFolder = IsFolder;

        Name = Path.GetFileName(newFullPath);
        FullPath = newFullPath;

        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(IsShortcut));
        OnPropertyChanged(nameof(IsAudio));
        OnPropertyChanged(nameof(IsImageFile));

        if (wasFolder || !string.Equals(previousExtension, Extension, StringComparison.OrdinalIgnoreCase))
        {
            _typeName = null;
            OnPropertyChanged(nameof(TypeName));
            Icon = IconService.GetIcon(this);
            Thumbnail = null;
            GridPlaceholder = null;
        }

        RefreshTags();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
            return string.Empty;

        if (bytes < 1024)
            return bytes == 0 ? "0 KB" : "1 KB";

        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes / 1024d;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{Math.Ceiling(value):N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }

    public override string ToString() => FullPath;
}
