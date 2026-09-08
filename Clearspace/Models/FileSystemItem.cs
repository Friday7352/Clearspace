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

    private static readonly Brush CloudRemoteBrush = Frozen(Color.FromRgb(156, 150, 141));
    private static readonly Brush CloudLocalBrush = Frozen(Color.FromRgb(112, 178, 132));
    private static readonly Brush CloudPinnedBrush = Frozen(Color.FromRgb(211, 161, 95));

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

    internal void RefreshTags() => Tags = TagService.TagsFor(FullPath);


    private string? _mediaTitle;
    private string? _artist;
    private string? _album;
    private uint _trackNumber;
    private TimeSpan _duration;

    public bool HasMediaInfo { get; private set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(_mediaTitle)
        ? Path.GetFileNameWithoutExtension(Name)
        : _mediaTitle;

    public string Artist => _artist ?? string.Empty;

    public string Album => _album ?? string.Empty;

    public string TrackNumberText => _trackNumber > 0 ? _trackNumber.ToString() : string.Empty;

    public uint TrackNumber => _trackNumber;

    public TimeSpan Duration => _duration;

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
        HasMediaInfo = true;

        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(TrackNumberText));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasMediaInfo));
    }

    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name);

    public string SizeText => IsDriveRoot
        ? $"{FormatSize(DriveAvailableSpace)} free of {FormatSize(DriveTotalSpace)}"
        : IsFolder ? string.Empty : FormatSize(Size);

    public double DriveUsagePercent => DriveTotalSpace <= 0
        ? 0
        : Math.Clamp((DriveTotalSpace - DriveAvailableSpace) * 100d / DriveTotalSpace, 0, 100);

    private static readonly Brush DriveFullBrush = Frozen(Color.FromRgb(214, 95, 84));
    private static readonly Brush DriveWarnBrush = Frozen(Color.FromRgb(211, 161, 95));
    private static readonly Brush DriveOkBrush = Frozen(Color.FromRgb(112, 178, 132));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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
