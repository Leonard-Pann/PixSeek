using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PixSeek.Controls;
public sealed class ZoomImage : Control
{
    public static readonly StyledProperty<Bitmap?> BitmapProperty = AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Bitmap));
    public Bitmap? Bitmap { get => GetValue(BitmapProperty); set => SetValue(BitmapProperty, value); }

    private double _zoom = 1;
    private Vector _translation;
    private Point? _drag;
    private double Fit => Bitmap == null ? 1 : Math.Min(Bounds.Width / Bitmap.PixelSize.Width, Bounds.Height / Bitmap.PixelSize.Height);
    public double Scale => Fit * _zoom;
    public Vector Translation => _translation;

    public ZoomImage()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BitmapProperty || change.Property == BoundsProperty)
            Reset();
    }

    public void Reset()
    {
        _zoom = 1;
        _translation = Bitmap == null ? default : new((Bounds.Width - Bitmap.PixelSize.Width * Scale) / 2, (Bounds.Height - Bitmap.PixelSize.Height * Scale) / 2);
        InvalidateVisual();
    }

    public void ZoomAt(Point point, double factor)
    {
        if (Bitmap == null || Fit <= 0)
            return;
        var old = Scale;
        var world = (point - new Point(_translation.X, _translation.Y)) / old;
        _zoom = Math.Clamp(_zoom * factor, 1, 24);
        if (_zoom == 1)
        {
            Reset();
            return;
        }

        _translation = new(point.X - world.X * Scale, point.Y - world.Y * Scale);
        InvalidateVisual();
    }

    public void PanBy(Vector delta)
    {
        if (_zoom > 1)
        {
            _translation += delta;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), Math.Pow(1.18, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _drag = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is { } previous)
        {
            var point = e.GetPosition(this);
            PanBy(point - previous);
            _drag = point;
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Bitmap != null)
            context.DrawImage(Bitmap, new Rect(0, 0, Bitmap.PixelSize.Width, Bitmap.PixelSize.Height), new Rect(_translation.X, _translation.Y, Bitmap.PixelSize.Width * Scale, Bitmap.PixelSize.Height * Scale));
    }
}
