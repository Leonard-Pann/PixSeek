using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace PixSeek.Mobile;

/// <summary>Only realized virtual rows decode thumbnails. Detached containers release native bitmaps.</summary>
public sealed class MobileThumbnail : Image
{
    public static readonly StyledProperty<string?> FilePathProperty = AvaloniaProperty.Register<MobileThumbnail, string?>(nameof(FilePath));
    public string? FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    private int _generation;
    private Bitmap? _bitmap;
    private static readonly SemaphoreSlim Decoders = new(3);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilePathProperty) _ = LoadAsync();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _ = LoadAsync(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { ++_generation; Release(); base.OnDetachedFromVisualTree(e); }
    private async Task LoadAsync()
    {
        var generation = ++_generation; var path = FilePath;
        Release();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                await Decoders.WaitAsync();
                try { using var file = File.OpenRead(path); return Bitmap.DecodeToWidth(file, 320); }
                finally { Decoders.Release(); }
            });
            if (generation != _generation) { bitmap.Dispose(); return; }
            _bitmap = bitmap; Source = bitmap;
        }
        catch (Exception e) when (e is IOException or ArgumentException) { }
    }
    private void Release() { Source = null; _bitmap?.Dispose(); _bitmap = null; }
}
