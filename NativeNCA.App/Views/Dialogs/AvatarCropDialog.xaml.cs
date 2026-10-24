using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace NativeNCA.App.Views.Dialogs;

/// <summary>
/// Picks a picture for a key and lets the user frame it in a circle: drag to move,
/// mouse wheel or slider to zoom. The result is a 256×256 PNG.
/// </summary>
public partial class AvatarCropDialog : ContentDialog
{
    public const int OutputSize = 256;
    private const double ViewSize = 280, CropInset = 16, CropSize = 248, MaxSourceSide = 2048;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".heic", ".tif", ".tiff"];

    private BitmapSource? _source;
    private double _minScale = 1, _scale = 1;
    private Point _offset;            // top-left of the picture in viewport coordinates
    private Point? _dragStart;
    private bool _updatingSlider;

    /// <summary>Set when the dialog closes with "Сохранить".</summary>
    public byte[]? ResultPng { get; private set; }

    /// <summary>True when the user chose "Удалить фото" (the dialog then returns Secondary).</summary>
    public bool RemoveRequested { get; private set; }

    public AvatarCropDialog(ContentDialogHost host, string? currentAvatarPath) : base(host)
    {
        InitializeComponent();

        if (currentAvatarPath != null && File.Exists(currentAvatarPath))
        {
            RemoveButton.Visibility = Visibility.Visible;
            TryLoad(currentAvatarPath);
        }

        UpdateState();
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button == ContentDialogButton.Primary)
        {
            if (_source == null)
            {
                ShowError("Сначала выберите фото");
                return;
            }
            ResultPng = RenderCrop();
        }

        base.OnButtonClick(button);
    }

    // ===================== Loading =====================

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите фото для ключа",
            Filter = "Изображения|" + string.Join(";", ImageExtensions.Select(x => "*" + x)) + "|Все файлы|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            TryLoad(dialog.FileName);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        RemoveRequested = true;
        Hide(ContentDialogResult.Secondary);
    }

    private static string? GetDroppedImage(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedImage(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedImage(e) is { } file) TryLoad(file);
    }

    private void TryLoad(string path)
    {
        try
        {
            _source = LoadOriented(path);
            ErrorBar.IsOpen = false;
        }
        catch (Exception)
        {
            ShowError("Формат не поддерживается или файл повреждён");
            return;
        }

        // Start with the whole crop circle covered and the picture centred.
        _minScale = Math.Max(CropSize / _source.PixelWidth, CropSize / _source.PixelHeight);
        _scale = _minScale;
        _offset = new Point((ViewSize - _source.PixelWidth * _scale) / 2, (ViewSize - _source.PixelHeight * _scale) / 2);

        Photo.Source = _source;
        SetSlider(1);
        ApplyLayout();
        UpdateState();
    }

    /// <summary>Decodes the picture, applies the EXIF rotation from phone cameras and limits its size.</summary>
    private static BitmapSource LoadOriented(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

        double angle = 0;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort orientation)
                angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (NotSupportedException)
        {
            // Formats without metadata support.
        }

        var transform = new TransformGroup();
        double longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest > MaxSourceSide)
        {
            double s = MaxSourceSide / longest;
            transform.Children.Add(new ScaleTransform(s, s));
        }
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));

        BitmapSource result = transform.Children.Count > 0 ? new TransformedBitmap(frame, transform) : frame;
        result.Freeze();
        return result;
    }

    // ===================== Pan & zoom =====================

    private void ApplyLayout()
    {
        if (_source == null) return;
        Photo.Width = _source.PixelWidth * _scale;
        Photo.Height = _source.PixelHeight * _scale;
        Canvas.SetLeft(Photo, _offset.X);
        Canvas.SetTop(Photo, _offset.Y);
    }

    /// <summary>Keeps the crop circle fully covered by the picture.</summary>
    private void ClampOffset()
    {
        if (_source == null) return;
        double w = _source.PixelWidth * _scale, h = _source.PixelHeight * _scale;
        _offset.X = Math.Min(CropInset, Math.Max(CropInset + CropSize - w, _offset.X));
        _offset.Y = Math.Min(CropInset, Math.Max(CropInset + CropSize - h, _offset.Y));
    }

    private void ZoomTo(double factor, Point anchor)
    {
        if (_source == null) return;
        factor = Math.Clamp(factor, ZoomSlider.Minimum, ZoomSlider.Maximum);
        double newScale = _minScale * factor;

        // Keep the point under the cursor (or the centre) in place.
        var imagePoint = new Point((anchor.X - _offset.X) / _scale, (anchor.Y - _offset.Y) / _scale);
        _scale = newScale;
        _offset = new Point(anchor.X - imagePoint.X * _scale, anchor.Y - imagePoint.Y * _scale);

        ClampOffset();
        ApplyLayout();
        SetSlider(factor);
    }

    private void SetSlider(double value)
    {
        _updatingSlider = true;
        ZoomSlider.Value = value;
        _updatingSlider = false;
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSlider) ZoomTo(e.NewValue, new Point(ViewSize / 2, ViewSize / 2));
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomTo(_scale / _minScale * Math.Pow(1.12, e.Delta / 120.0), e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_source == null) return;
        _dragStart = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var now = e.GetPosition(Viewport);
        _offset = new Point(_offset.X + now.X - start.X, _offset.Y + now.Y - start.Y);
        _dragStart = now;
        ClampOffset();
        ApplyLayout();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = _source != null ? Cursors.Hand : null;
    }

    // ===================== Output =====================

    /// <summary>Renders the square around the crop circle to a 256×256 PNG.</summary>
    private byte[] RenderCrop()
    {
        double k = OutputSize / CropSize;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_source, new Rect(
                (_offset.X - CropInset) * k, (_offset.Y - CropInset) * k,
                _source!.PixelWidth * _scale * k, _source.PixelHeight * _scale * k));
        }

        var bitmap = new RenderTargetBitmap(OutputSize, OutputSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private void UpdateState()
    {
        bool loaded = _source != null;
        EmptyState.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        CropOverlay.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        ChangeButton.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        Controls.IsEnabled = loaded;
        Viewport.Cursor = loaded ? Cursors.Hand : null;
        IsPrimaryButtonEnabled = loaded;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
