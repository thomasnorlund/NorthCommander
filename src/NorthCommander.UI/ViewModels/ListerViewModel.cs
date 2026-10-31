using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NorthCommander.Core.Viewer;

namespace NorthCommander.UI.ViewModels;

public enum ListerMode
{
    Text,
    Hex,
    Image,
}

/// <summary>
/// F3 file viewer. Only the visible page is read from the memory-mapped file, so huge files open instantly.
/// The position is a byte offset in text mode and a 16-byte row index in hex mode.
/// </summary>
public sealed partial class ListerViewModel : ViewModelBase
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif",
    };

    private MappedFileSource? _source;
    private TextPager? _pager;
    private long _top;
    private long _pageEnd;
    private int _rows = 1;
    private int _columns = 80;
    private double _scrollValue;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string FilePath { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMode), nameof(IsHexMode), nameof(StatusText))]
    public partial ListerMode Mode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsWrapEnabled { get; private set; } = true;

    [ObservableProperty]
    public partial string PageText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    public partial string? ImageMessage { get; private set; }

    [ObservableProperty]
    public partial bool CanShowImage { get; private set; }

    [ObservableProperty]
    public partial double ScrollMaximum { get; private set; }

    [ObservableProperty]
    public partial double ScrollViewport { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    public bool IsImageMode => Mode == ListerMode.Image;

    public bool IsHexMode => Mode == ListerMode.Hex;

    public long FileSize => _source?.Length ?? 0;

    /// <summary>Two-way bound to the scroll bar; dragging it jumps to that byte (text) or row (hex).</summary>
    public double ScrollValue
    {
        get => _scrollValue;
        set
        {
            if (Math.Abs(value - _scrollValue) < 0.5 || _source is null)
            {
                return;
            }

            _top = Mode == ListerMode.Text ? _pager!.AlignToLine((long)value, WrapColumns) : Math.Clamp((long)value, 0, MaxHexTop);
            Refresh();
        }
    }

    private int WrapColumns => IsWrapEnabled ? _columns : 0;

    private long MaxHexTop => Math.Max(0, HexFormatter.RowCount(FileSize) - _rows);

    /// <summary>Opens <paramref name="path"/>; returns an error message if it cannot be read.</summary>
    public string? Open(string path)
    {
        Close();
        try
        {
            _source = new MappedFileSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        _pager = new TextPager(_source);
        _top = 0;
        FilePath = path;
        CanShowImage = ImageExtensions.Contains(Path.GetExtension(path));
        Mode = CanShowImage ? ListerMode.Image : ListerMode.Text;
        IsOpen = true;
        if (CanShowImage)
        {
            _ = LoadImageAsync(path);
        }

        Refresh();
        return null;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _source?.Dispose();
        _source = null;
        _pager = null;
        Image?.Dispose();
        Image = null;
        ImageMessage = null;
        PageText = string.Empty;
    }

    /// <summary>Called by the view when its size changes: visible text rows and columns.</summary>
    public void SetViewport(int rows, int columns)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(10, columns);
        if (rows == _rows && columns == _columns)
        {
            return;
        }

        _rows = rows;
        _columns = columns;
        if (_pager is not null && Mode == ListerMode.Text)
        {
            _top = _pager.AlignToLine(_top, WrapColumns);
        }

        Refresh();
    }

    public void SetMode(ListerMode mode)
    {
        if (_pager is null || mode == Mode || (mode == ListerMode.Image && !CanShowImage))
        {
            return;
        }

        // Keep the position when switching between text and hex.
        var offset = Mode == ListerMode.Hex ? _top * HexFormatter.BytesPerRow : _top;
        _top = mode == ListerMode.Hex ? Math.Min(offset / HexFormatter.BytesPerRow, MaxHexTop) : _pager.AlignToLine(offset, WrapColumns);
        Mode = mode;
        Refresh();
    }

    public void ToggleWrap()
    {
        if (_pager is null)
        {
            return;
        }

        IsWrapEnabled = !IsWrapEnabled;
        _top = _pager.AlignToLine(_top, WrapColumns);
        Refresh();
    }

    public void ScrollLines(int delta)
    {
        if (_pager is null || delta == 0 || Mode == ListerMode.Image)
        {
            return;
        }

        if (Mode == ListerMode.Hex)
        {
            _top = Math.Clamp(_top + delta, 0, MaxHexTop);
        }
        else
        {
            for (var i = 0; i < Math.Abs(delta); i++)
            {
                if (delta > 0 && _pageEnd >= FileSize)
                {
                    break;
                }

                var next = delta > 0 ? _pager.ReadLine(_top, WrapColumns).End : _pager.PreviousLine(_top, WrapColumns);
                if (next == _top)
                {
                    break;
                }

                _top = next;
                if (delta > 0)
                {
                    _pageEnd = Math.Max(_pageEnd, _pager.ReadLine(_pageEnd, WrapColumns).End);
                }
            }
        }

        Refresh();
    }

    public void PageDown() => ScrollLines(Math.Max(1, _rows - 1));

    public void PageUp() => ScrollLines(-Math.Max(1, _rows - 1));

    public void GoToStart()
    {
        _top = 0;
        Refresh();
    }

    public void GoToEnd()
    {
        if (_pager is null)
        {
            return;
        }

        _top = Mode == ListerMode.Hex ? MaxHexTop : _pager.LastPageTop(_rows, WrapColumns);
        Refresh();
    }

    private void Refresh()
    {
        if (_source is null || _pager is null)
        {
            return;
        }

        if (Mode == ListerMode.Hex)
        {
            PageText = string.Join('\n', HexFormatter.FormatPage(_source, _top, _rows));
            _pageEnd = Math.Min(FileSize, (_top + _rows) * HexFormatter.BytesPerRow);
            ScrollMaximum = MaxHexTop;
            ScrollViewport = _rows;
        }
        else if (Mode == ListerMode.Text)
        {
            var lines = _pager.ReadPage(_top, _rows, WrapColumns);
            PageText = string.Join('\n', lines.Select(l => l.Text));
            _pageEnd = lines.Count > 0 ? lines[^1].End : _top;
            ScrollMaximum = FileSize;
            ScrollViewport = Math.Max(1, _pageEnd - _top);
        }

        _scrollValue = _top;
        OnPropertyChanged(nameof(ScrollValue));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var mode = Mode switch
        {
            ListerMode.Hex => "Hex",
            ListerMode.Image => "Image",
            _ => IsWrapEnabled ? "Text (wrap)" : "Text",
        };
        var percent = FileSize == 0 || Mode == ListerMode.Image ? 100 : (int)(_pageEnd * 100 / FileSize);
        StatusText = string.Create(
            CultureInfo.CurrentCulture,
            $"{Path.GetFileName(FilePath)}   │   {mode}   │   {FileSize:N0} bytes   │   {percent}%");
    }

    private async Task LoadImageAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() => new Bitmap(path));
            if (IsOpen && FilePath == path)
            {
                Image = bitmap;
            }
            else
            {
                bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (IsOpen && FilePath == path)
            {
                ImageMessage = $"Cannot display image: {ex.Message}";
            }
        }
    }
}
