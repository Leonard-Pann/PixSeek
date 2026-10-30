using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PixSeek.Controls;
/// <summary>Realized cards own only a bounded thumbnail bitmap, released when virtualized away.</summary>
public sealed class AsyncThumbnail : Image
{
    public static readonly StyledProperty<string?> FilePathProperty = AvaloniaProperty.Register<AsyncThumbnail, string?>(nameof(FilePath));
    public string? FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    // Read by the initial viewport only; failed/missing thumbnails are settled placeholders.
    public bool IsLoadComplete { get; private set; }

    private Bitmap? _bitmap;
    private CancellationTokenSource? _load;
    private static readonly SemaphoreSlim DecodeSlots = new(3, 3);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilePathProperty && this.IsAttachedToVisualTree())
            _ = LoadAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        IsLoadComplete = false;
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task LoadAsync()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = new();
        IsLoadComplete = false;
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        var token = _load.Token;
        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            IsLoadComplete = true;
            return;
        }
        Bitmap? bitmap = null;
        try
        {
            await DecodeSlots.WaitAsync(token);
            try
            {
                bitmap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 480);
                }, token);
            }
            finally
            {
                DecodeSlots.Release();
            }

            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _bitmap?.Dispose();
            _bitmap = bitmap;
            Source = bitmap;
        }
        catch (Exception)
        {
            bitmap?.Dispose();
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoadComplete = true;
        }
    }
}
