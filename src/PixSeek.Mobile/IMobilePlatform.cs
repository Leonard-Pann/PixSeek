using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PixSeek.Core;

namespace PixSeek.Mobile;

public sealed record MobileSource(string Uri, string Name, string Kind, string? BucketId = null, IReadOnlyList<string>? Items = null);
public sealed record DeviceAlbum(string Id, string Name, int Count);
public sealed record DeviceMedia(string Uri, string Name, MediaType Type, long Size, DateTime Modified, int Width = 0, int Height = 0, long Duration = 0);

/// <summary>Read-only mobile access. URI permissions belong to the platform, not the view models.</summary>
public interface IMobilePlatform : IImageTensorSource
{
    IPlatformPaths Paths { get; }
    Task<IReadOnlyList<DeviceAlbum>> AlbumsAsync(CancellationToken ct);
    Task<MobileSource?> PickTreeAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> PickPhotosAsync(bool photosOnly, CancellationToken ct);
    Task<IReadOnlyList<DeviceMedia>> EnumerateAsync(MobileSource source, bool recursive, CancellationToken ct);
    Task<DeviceMedia> DescribeAsync(string uri, CancellationToken ct);
    Task<string> ThumbnailAsync(MediaFile media, CancellationToken ct);
    Task<string> VideoFrameAsync(MediaFile media, long milliseconds, CancellationToken ct);
    Task<Bitmap> PreviewAsync(MediaFile media, CancellationToken ct);
    Control CreateVideoPlayer(MediaFile media, long timestamp, Action<string> error);
    void StopVideo();
    void HideKeyboard();
    void Share(MediaFile media);
    void KeepScreenOn(bool enabled);
}
