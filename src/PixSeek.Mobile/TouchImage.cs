using Avalonia;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PixSeek.Mobile;

public sealed class TouchImage : Control
{
    public static readonly StyledProperty<Bitmap?> BitmapProperty = AvaloniaProperty.Register<TouchImage, Bitmap?>(nameof(Bitmap));
    public Bitmap? Bitmap { get => GetValue(BitmapProperty); set => SetValue(BitmapProperty, value); }
    private double _zoom = 1;
    private Vector _translation;
    private Point? _drag;
    private double Fit => Bitmap == null ? 1 : Math.Min(Bounds.Width / Bitmap.PixelSize.Width, Bounds.Height / Bitmap.PixelSize.Height);
    private double Scale => Fit * _zoom;
    private double _lastScale = 1;
    public TouchImage()
    {
        ClipToBounds = true;
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(PinchEvent, (_, e) =>
        {
            ZoomAt(e.ScaleOrigin, e.Scale / _lastScale); _lastScale = e.Scale; e.Handled = true;
        });
        AddHandler(PinchEndedEvent, (_, _) => _lastScale = 1);
        DoubleTapped += (_, _) => { if (_zoom > 1) Reset(); else ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 2.5); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BitmapProperty || change.Property == BoundsProperty) Reset();
    }
    private void Reset()
    {
        _zoom = 1; _lastScale = 1;
        _translation = Bitmap == null ? default : new((Bounds.Width - Bitmap.PixelSize.Width * Scale) / 2, (Bounds.Height - Bitmap.PixelSize.Height * Scale) / 2);
        InvalidateVisual();
    }
    private void ZoomAt(Point point, double factor)
    {
        if (Bitmap == null || Fit <= 0) return;
        var world = (point - new Point(_translation.X, _translation.Y)) / Scale;
        _zoom = Math.Clamp(_zoom * factor, 1, 12);
        if (_zoom == 1) { Reset(); return; }
        _translation = new(point.X - world.X * Scale, point.Y - world.Y * Scale);
        InvalidateVisual();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _drag = e.GetPosition(this);
        // Leave touch capture to PinchGestureRecognizer; mouse wheel remains useful on tablets.
        if (e.Pointer.Type == PointerType.Mouse) e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is { } previous && _zoom > 1 && _lastScale == 1)
        {
            var position = e.GetPosition(this); _translation += position - previous; _drag = position; InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = null; e.Pointer.Capture(null); base.OnPointerReleased(e); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { ZoomAt(e.GetPosition(this), Math.Pow(1.18, e.Delta.Y)); e.Handled = true; }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Bitmap != null) context.DrawImage(Bitmap, new Rect(0, 0, Bitmap.PixelSize.Width, Bitmap.PixelSize.Height),
            new Rect(_translation.X, _translation.Y, Bitmap.PixelSize.Width * Scale, Bitmap.PixelSize.Height * Scale));
    }
}
