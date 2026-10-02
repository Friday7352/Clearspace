// Clearspace | Photo viewer state and editing.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class PhotoViewerViewModel : ObservableObject
{
    private List<FileSystemItem> _photos = [];
    private int _index = -1;
    private int _generation;

    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;

    public IntPtr OwnerHandle { get; set; }

    public event EventHandler? FileChanged;

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    private BitmapSource? _image;
    public BitmapSource? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(PixelSizeText));
                ZoomChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasImage => _image is not null;

    private void ReplaceImage(BitmapSource image)
    {
        _image = image;

        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(PixelSizeText));
        OnPropertyChanged(nameof(Subtitle));

        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ZoomChanged;

    private FileSystemItem? _current;
    public FileSystemItem? Current
    {
        get => _current;
        private set
        {
            if (SetProperty(ref _current, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(CanSaveInPlace));
            }
        }
    }

    public string Title => Current?.Name ?? string.Empty;

    public string Subtitle
    {
        get
        {
            if (Current is null)
                return string.Empty;

            var parts = new List<string> { $"{_index + 1} of {_photos.Count}" };

            if (!string.IsNullOrEmpty(PixelSizeText))
                parts.Add(PixelSizeText);

            parts.Add(Current.SizeText);

            if (!IsFitToWindow)
                parts.Add($"{Zoom * 100:N0}%");

            return string.Join("  ·  ", parts);
        }
    }

    public string PixelSizeText => _image is null ? string.Empty : $"{_image.PixelWidth} × {_image.PixelHeight}";

    public bool CanSaveInPlace => Current is not null && ImageEditService.CanSaveInPlace(Current.FullPath);

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }


    private double _zoom = 1;
    public double Zoom
    {
        get => _zoom;
        private set
        {
            var clamped = Math.Clamp(value, MinZoom, MaxZoom);

            if (!SetProperty(ref _zoom, clamped))
                return;

            OnPropertyChanged(nameof(ZoomText));
            OnPropertyChanged(nameof(Subtitle));
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ZoomText => IsFitToWindow ? "Fit" : $"{Zoom * 100:N0}%";

    private bool _isFitToWindow = true;
    public bool IsFitToWindow
    {
        get => _isFitToWindow;
        private set
        {
            if (SetProperty(ref _isFitToWindow, value))
            {
                OnPropertyChanged(nameof(ZoomText));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public void ZoomBy(double factor)
    {
        IsFitToWindow = false;
        Zoom *= factor;
    }

    public void SetZoom(double value)
    {
        IsFitToWindow = false;
        Zoom = value;
    }

    public void SeedZoom(double value) => _zoom = Math.Clamp(value, MinZoom, MaxZoom);

    public void FitToWindow()
    {
        IsFitToWindow = true;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActualSize()
    {
        IsFitToWindow = false;
        Zoom = 1;
    }


    private bool _isCropping;
    public bool IsCropping
    {
        get => _isCropping;
        private set
        {
            if (SetProperty(ref _isCropping, value))
                OnPropertyChanged(nameof(CanCommitCrop));
        }
    }

    private Int32Rect _cropRegion;
    public Int32Rect CropRegion
    {
        get => _cropRegion;
        set
        {
            _cropRegion = value;
            OnPropertyChanged(nameof(CanCommitCrop));
        }
    }

    public bool CanCommitCrop => IsCropping && _cropRegion.Width > 1 && _cropRegion.Height > 1;

    public void BeginCrop()
    {
        if (_image is null)
            return;

        FitToWindow();
        CropRegion = default;
        IsCropping = true;
        Status = "Drag to select an area, then save.";
    }

    public void CancelCrop()
    {
        IsCropping = false;
        CropRegion = default;
        Status = string.Empty;
    }


    public bool CanGoNext => _index >= 0 && _index < _photos.Count - 1;

    public bool CanGoPrevious => _index > 0;

    public void Open(IReadOnlyList<FileSystemItem> folderItems, FileSystemItem start)
    {
        if (!start.IsImageFile)
            return;

        _photos = folderItems.Where(item => item.IsImageFile).ToList();
        _index = _photos.FindIndex(item => ReferenceEquals(item, start));

        if (_index < 0)
        {
            _photos = [start];
            _index = 0;
        }

        IsOpen = true;
        _ = LoadCurrentAsync();
    }

    public void Close()
    {
        IsOpen = false;
        IsCropping = false;
        Image = null;
        Current = null;
        Status = string.Empty;
        _photos = [];
        _index = -1;

        Interlocked.Increment(ref _generation);
    }

    public void Next()
    {
        if (!CanGoNext)
            return;

        _index++;
        _ = LoadCurrentAsync();
    }

    public void Previous()
    {
        if (!CanGoPrevious)
            return;

        _index--;
        _ = LoadCurrentAsync();
    }


    public void Rotate(int degrees)
    {
        if (_image is null || Current is null)
            return;

        var rotated = ImageEditService.Rotate(_image, degrees);
        ReplaceImage(rotated);
        CropRegion = default;

        if (!CanSaveInPlace)
        {
            Status = "Rotated on screen only; this format cannot be written back.";
            return;
        }

        var error = ImageEditService.Save(rotated, Current.FullPath);
        Status = error is null ? "Rotation saved." : error;

        if (error is not null)
            return;

        InvalidatePreview();
        FileChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidatePreview()
    {
        if (Current is null)
            return;

        ThumbnailService.Invalidate(Current.FullPath);
        Current.Thumbnail = null;
    }

    public void CommitCrop(bool asCopy)
    {
        if (_image is null || Current is null || !CanCommitCrop)
            return;

        var cropped = ImageEditService.Crop(_image, _cropRegion);

        if (cropped is null)
        {
            Status = "That selection is too small to crop.";
            return;
        }

        var target = asCopy || !CanSaveInPlace
            ? ImageEditService.NextAvailableCopyPath(Current.FullPath)
            : Current.FullPath;

        var error = ImageEditService.Save(cropped, target);

        if (error is not null)
        {
            Status = error;
            return;
        }

        ReplaceImage(cropped);
        IsCropping = false;
        CropRegion = default;
        Status = asCopy || !CanSaveInPlace ? $"Saved {Path.GetFileName(target)}" : "Crop saved.";

        if (!asCopy)
            InvalidatePreview();

        FileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CopyImage()
    {
        if (_image is null)
            return;

        Status = ImageEditService.CopyToClipboard(_image)
            ? "Image copied."
            : "Could not reach the clipboard.";
    }

    public void CopyPath()
    {
        if (Current is null)
            return;

        try
        {
            Clipboard.SetText(Current.FullPath);
            Status = "Path copied.";
        }
        catch (Exception)
        {
            Status = "Could not reach the clipboard.";
        }
    }

    public void OpenWith()
    {
        if (Current is not null)
            FileOperationService.OpenWith(Current.FullPath, OwnerHandle);
    }

    public void ShowInFolder()
    {
        if (Current is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Current.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            Status = "Could not open the containing folder.";
        }
    }

    public void DeleteCurrent()
    {
        if (Current is null)
            return;

        var result = FileOperationService.Delete([Current.FullPath], OwnerHandle);
        if (!result.Succeeded)
        {
            Status = result.Message;
            FileChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _photos.RemoveAt(_index);
        FileChanged?.Invoke(this, EventArgs.Empty);

        if (_photos.Count == 0)
        {
            Close();
            return;
        }

        _index = Math.Min(_index, _photos.Count - 1);
        _ = LoadCurrentAsync();
    }


    private async Task LoadCurrentAsync()
    {
        if (_index < 0 || _index >= _photos.Count)
            return;

        var item = _photos[_index];
        Current = item;
        IsCropping = false;
        CropRegion = default;
        Status = string.Empty;
        FitToWindow();

        Image = item.Thumbnail as BitmapSource;
        IsLoading = true;

        var generation = Interlocked.Increment(ref _generation);
        var path = item.FullPath;

        try
        {
            var decoded = await Task.Run(() => Decode(path));

            if (generation != Volatile.Read(ref _generation))
                return;

            if (decoded is not null)
                Image = decoded;
            else
                Status = "Clearspace has no decoder for this image.";
        }
        catch (Exception)
        {
            Status = "That image could not be opened.";
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanGoPrevious));
            }
        }
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();

            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
