// Clearspace | Audio player state and controls.

using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class AudioPlayerViewModel : ObservableObject
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _ticker;

    private List<FileSystemItem> _queue = [];
    private int _index = -1;
    private bool _isScrubbing;
    private bool _updatingPosition;

    public AudioPlayerViewModel()
    {
        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += (_, _) => Next();
        _player.MediaFailed += OnMediaFailed;
        _player.Volume = 0.7;

        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _ticker.Tick += (_, _) => PushPosition();
    }

    private FileSystemItem? _current;
    public FileSystemItem? Current
    {
        get => _current;
        private set
        {
            var previous = _current;

            if (!SetProperty(ref _current, value))
                return;

            if (previous is not null)
                previous.IsNowPlaying = false;

            if (value is not null)
                value.IsNowPlaying = true;

            OnPropertyChanged(nameof(TrackTitle));
            OnPropertyChanged(nameof(TrackSubtitle));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool IsActive => Current is not null;

    public string TrackTitle => Current?.DisplayTitle ?? string.Empty;

    public string TrackSubtitle
    {
        get
        {
            if (Current is null)
                return string.Empty;

            var artist = Current.Artist;
            var album = Current.Album;

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
                return $"{artist} — {album}";

            return !string.IsNullOrWhiteSpace(artist) ? artist
                 : !string.IsNullOrWhiteSpace(album) ? album
                 : Path.GetFileName(Current.FullPath);
        }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
                OnPropertyChanged(nameof(PlayGlyph));
        }
    }

    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!SetProperty(ref _positionSeconds, value))
                return;

            OnPropertyChanged(nameof(PositionText));

            if (!_updatingPosition && _player.NaturalDuration.HasTimeSpan)
                _player.Position = TimeSpan.FromSeconds(value);
        }
    }

    private double _durationSeconds;
    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (SetProperty(ref _durationSeconds, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    public string PositionText => Format(TimeSpan.FromSeconds(_positionSeconds));

    public string DurationText => Format(TimeSpan.FromSeconds(_durationSeconds));

    public double Volume
    {
        get => _player.Volume;
        set
        {
            _player.Volume = Math.Clamp(value, 0, 1);
            OnPropertyChanged();
        }
    }

    public bool CanGoNext => _index >= 0 && _index < _queue.Count - 1;

    public bool CanGoPrevious => _index > 0;

    public void Play(IReadOnlyList<FileSystemItem> folderItems, FileSystemItem start)
    {
        _queue = folderItems.Where(item => item.IsAudio).ToList();
        _index = _queue.FindIndex(item => ReferenceEquals(item, start));

        if (_index < 0)
        {
            if (!start.IsAudio)
                return;

            _queue = [start];
            _index = 0;
        }

        OpenCurrent();
    }

    public void TogglePlay()
    {
        if (Current is null)
            return;

        if (IsPlaying)
        {
            _player.Pause();
            _ticker.Stop();
            IsPlaying = false;
        }
        else
        {
            _player.Play();
            _ticker.Start();
            IsPlaying = true;
        }
    }

    public void Next()
    {
        if (!CanGoNext)
        {
            Stop();
            return;
        }

        _index++;
        OpenCurrent();
    }

    public void Previous()
    {
        if (_positionSeconds > 3 || !CanGoPrevious)
        {
            _player.Position = TimeSpan.Zero;
            return;
        }

        _index--;
        OpenCurrent();
    }

    public void Stop()
    {
        _ticker.Stop();
        _player.Stop();
        _player.Close();
        IsPlaying = false;
        Current = null;
        DurationSeconds = 0;
        SetPositionQuietly(0);
        RaiseNavigationState();
    }

    public void BeginScrub() => _isScrubbing = true;

    public void EndScrub()
    {
        _isScrubbing = false;

        if (_player.NaturalDuration.HasTimeSpan)
            _player.Position = TimeSpan.FromSeconds(_positionSeconds);
    }

    private void OpenCurrent()
    {
        if (_index < 0 || _index >= _queue.Count)
            return;

        var track = _queue[_index];
        Current = track;

        MediaPropertyService.Request(track);

        try
        {
            _player.Open(new Uri(track.FullPath));
            _player.Play();
            _ticker.Start();
            IsPlaying = true;
        }
        catch (Exception)
        {
            Next();
        }

        RaiseNavigationState();
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        DurationSeconds = _player.NaturalDuration.HasTimeSpan
            ? _player.NaturalDuration.TimeSpan.TotalSeconds
            : 0;

        SetPositionQuietly(0);
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (CanGoNext)
            Next();
        else
            Stop();
    }

    private void PushPosition()
    {
        if (_isScrubbing)
            return;

        SetPositionQuietly(_player.Position.TotalSeconds);
    }

    private void SetPositionQuietly(double seconds)
    {
        _updatingPosition = true;
        try
        {
            PositionSeconds = seconds;
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    private void RaiseNavigationState()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    private static string Format(TimeSpan value)
        => value <= TimeSpan.Zero
            ? "0:00"
            : value.TotalHours >= 1
                ? value.ToString(@"h\:mm\:ss")
                : value.ToString(@"m\:ss");
}
