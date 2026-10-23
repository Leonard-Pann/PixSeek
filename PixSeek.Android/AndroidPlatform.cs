using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Views.InputMethods;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PixSeek.Core;
using PixSeek.Mobile;
using ABitmap = Android.Graphics.Bitmap;
using AUri = Android.Net.Uri;
using MediaType = PixSeek.Core.MediaType;
using Uri = System.Uri;
using Path = System.IO.Path;
using Encoding = System.Text.Encoding;
using Canvas = Android.Graphics.Canvas;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace PixSeek.Android;

public sealed class AndroidPaths(Context context) : IPlatformPaths
{
    public string Data => context.FilesDir!.AbsolutePath;
    public string Cache => context.CacheDir!.AbsolutePath;
    public string Models => Path.Combine(Data, "models");
    public string Logs => Path.Combine(Data, "logs");
    public string Native => context.ApplicationInfo!.NativeLibraryDir!;
}

/// <summary>No filesystem coercion for content://. All originals remain under Android's read grants.</summary>
public sealed class AndroidPlatform : IMobilePlatform, IMobileBootstrap
{
    private readonly MainActivity _activity;
    private readonly ContentResolver _resolver;
    private readonly SemaphoreSlim _pickGate = new(1), _decodeGate = new(2);
    private TaskCompletionSource<IReadOnlyList<string>>? _picker;
    private TaskCompletionSource? _permissions;
    private AndroidVideoPlayer? _video;
    private const int PickerCode = 4801, PermissionCode = 4802;
    public IPlatformPaths Paths { get; }

    public AndroidPlatform(MainActivity activity)
    {
        _activity = activity; _resolver = activity.ContentResolver!; Paths = new AndroidPaths(activity);
        Directory.CreateDirectory(Paths.Models); Directory.CreateDirectory(Paths.Logs);
    }
    public async Task PrepareAsync(IProgress<string> progress, CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            using var manifest = _activity.Assets!.Open("asset-index.json");
            var files = (await JsonSerializer.DeserializeAsync<List<BundleAsset>>(manifest, cancellationToken: ct))!;
            long copied = 0;
            foreach (var asset in files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(Paths.Data, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Paths.Data + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Invalid bundled asset path.");
                if (File.Exists(destination) && new FileInfo(destination).Length == asset.Size) continue;
                using var input = _activity.Assets!.Open(asset.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".partial";
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, ct);
                await using (var verify = File.OpenRead(temporary))
                    if (verify.Length != asset.Size || !Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Corrupt bundled asset: " + asset.Path);
                File.Move(temporary, destination, true);
                copied++;
                progress.Report("PixSeek\n" + copied + " / " + files.Count);
            }
        }, ct);
    }
    private sealed record BundleAsset(string Path, long Size, string Sha256);
    private static bool Granted(Context context, string permission) => context.CheckSelfPermission(permission) == Permission.Granted;
    private bool HasMediaPermission => OperatingSystem.IsAndroidVersionAtLeast(33)
        ? Granted(_activity, Manifest.Permission.ReadMediaImages) || Granted(_activity, Manifest.Permission.ReadMediaVideo) ||
          (OperatingSystem.IsAndroidVersionAtLeast(34) && Granted(_activity, Manifest.Permission.ReadMediaVisualUserSelected))
        : Granted(_activity, Manifest.Permission.ReadExternalStorage);

    private async Task RequestMediaPermissionAsync(CancellationToken ct)
    {
        if (HasMediaPermission) return;
        await _pickGate.WaitAsync(ct);
        try
        {
            _permissions = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string[] names = OperatingSystem.IsAndroidVersionAtLeast(34)
                ? [Manifest.Permission.ReadMediaImages, Manifest.Permission.ReadMediaVideo, Manifest.Permission.ReadMediaVisualUserSelected]
                : OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? [Manifest.Permission.ReadMediaImages, Manifest.Permission.ReadMediaVideo] : [Manifest.Permission.ReadExternalStorage];
            _activity.RunOnUiThread(() => _activity.RequestPermissions(names, PermissionCode));
            await _permissions.Task.WaitAsync(ct);
            if (!HasMediaPermission) throw new UnauthorizedAccessException("Media access denied. Use the Android photo picker or select a folder.");
        }
        finally { _permissions = null; _pickGate.Release(); }
    }
    public void OnPermissions(int code) { if (code == PermissionCode) _permissions?.TrySetResult(); }
    public bool OnResult(int code, Result result, Intent? data)
    {
        if (code != PickerCode) return false;
        var uris = new List<string>();
        if (result == Result.Ok && data != null)
        {
            if (data.ClipData != null)
                for (var i = 0; i < data.ClipData.ItemCount; i++) if (data.ClipData.GetItemAt(i)?.Uri is { } uri) uris.Add(uri.ToString()!);
            if (data.Data != null) uris.Add(data.Data.ToString()!);
            foreach (var uri in uris.Distinct())
            {
                try { _resolver.TakePersistableUriPermission(AUri.Parse(uri)!, ActivityFlags.GrantReadUriPermission); }
                catch (Java.Lang.SecurityException) { /* Some reference-only picker providers grant access for this session. */ }
            }
        }
        _picker?.TrySetResult(uris.Distinct().ToArray()); return true;
    }
    private async Task<IReadOnlyList<string>> LaunchPickerAsync(Intent intent, CancellationToken ct)
    {
        await _pickGate.WaitAsync(ct);
        try
        {
            _picker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activity.RunOnUiThread(() =>
            {
                try { _activity.StartActivityForResult(intent, PickerCode); }
                catch (Exception e) { _picker.TrySetException(e); }
            });
            return await _picker.Task.WaitAsync(ct);
        }
        finally { _picker = null; _pickGate.Release(); }
    }
    public async Task<MobileSource?> PickTreeAsync(CancellationToken ct)
    {
        using var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        var selected = await LaunchPickerAsync(intent, ct);
        if (selected.Count == 0) return null;
        var root = AUri.Parse(selected[0])!;
        var document = DocumentsContract.BuildDocumentUriUsingTree(root, DocumentsContract.GetTreeDocumentId(root));
        using var cursor = _resolver.Query(document!, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
        var name = cursor?.MoveToFirst() == true ? cursor.GetString(0) ?? "Folder" : "Folder";
        return new(selected[0], name, "tree");
    }
    public async Task<IReadOnlyList<string>> PickPhotosAsync(bool photosOnly, CancellationToken ct)
    {
        // Android's gallery picker on 13+; SAF provides the same explicit read-only access on 9–12.
        using var intent = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? new Intent(MediaStore.ActionPickImages) : new Intent(Intent.ActionOpenDocument);
        if (photosOnly) intent.SetType("image/*");
        else if (!OperatingSystem.IsAndroidVersionAtLeast(33)) { intent.SetType("*/*"); intent.PutExtra(Intent.ExtraMimeTypes, new[] { "image/*", "video/*" }); }
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            intent.PutExtra(MediaStore.ExtraPickImagesMax, Math.Min(50, MediaStore.PickImagesMaxLimit));
        else { intent.AddCategory(Intent.CategoryOpenable); intent.PutExtra(Intent.ExtraAllowMultiple, true); }
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        return await LaunchPickerAsync(intent, ct);
    }
    public async Task<IReadOnlyList<DeviceAlbum>> AlbumsAsync(CancellationToken ct)
    {
        await RequestMediaPermissionAsync(ct);
        return await Task.Run(() =>
        {
            var albums = new Dictionary<string, (string Name, int Count)>();
            foreach (var collection in Collections())
            {
                try
                {
                    using var cursor = _resolver.Query(collection, ["bucket_id", "bucket_display_name"], null, null, null);
                    while (cursor?.MoveToNext() == true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var id = cursor.GetString(0) ?? "0"; var name = cursor.GetString(1) ?? "Photos";
                        albums[id] = (name, albums.GetValueOrDefault(id).Count + 1);
                    }
                }
                catch (Java.Lang.SecurityException) { }
            }
            return (IReadOnlyList<DeviceAlbum>)albums.OrderBy(a => a.Value.Name).Select(a => new DeviceAlbum(a.Key, a.Value.Name, a.Value.Count)).ToArray();
        }, ct);
    }
    private static IEnumerable<AUri> Collections()
    {
        yield return MediaStore.Images.Media.ExternalContentUri!;
        yield return MediaStore.Video.Media.ExternalContentUri!;
    }
    public Task<IReadOnlyList<DeviceMedia>> EnumerateAsync(MobileSource source, bool recursive, CancellationToken ct) => Task.Run(async () =>
    {
        if (source.Kind == "selection")
        {
            var selected = new List<DeviceMedia>();
            foreach (var uri in source.Items ?? [])
            {
                try { selected.Add(await DescribeAsync(uri, ct)); }
                catch (Java.IO.FileNotFoundException) { }
            }
            return (IReadOnlyList<DeviceMedia>)selected;
        }
        if (source.Kind == "tree") return EnumerateTree(source, recursive, ct);
        if (!HasMediaPermission) throw new UnauthorizedAccessException("Android media permission was revoked. Select the gallery again.");
        var media = new List<DeviceMedia>();
        var allowedCollections = 0;
        foreach (var collection in Collections())
        {
            var video = collection.ToString()!.Contains("/video/", StringComparison.Ordinal);
            try
            {
                string[] columns = video ? ["_id", "_display_name", "_size", "date_modified", "width", "height", "duration"] : ["_id", "_display_name", "_size", "date_modified", "width", "height"];
                using var cursor = _resolver.Query(collection, columns, source.Kind == "album" ? "bucket_id=?" : null,
                    source.Kind == "album" ? [source.BucketId!] : null, "date_modified DESC");
                allowedCollections++;
                while (cursor?.MoveToNext() == true)
                {
                    ct.ThrowIfCancellationRequested();
                    media.Add(new(ContentUris.WithAppendedId(collection, cursor.GetLong(0))!.ToString()!, cursor.GetString(1) ?? "media",
                        video ? MediaType.Video : MediaType.Image, cursor.IsNull(2) ? 0 : cursor.GetLong(2),
                        DateTimeOffset.FromUnixTimeSeconds(cursor.IsNull(3) ? 0 : cursor.GetLong(3)).UtcDateTime,
                        cursor.IsNull(4) ? 0 : cursor.GetInt(4), cursor.IsNull(5) ? 0 : cursor.GetInt(5), video && !cursor.IsNull(6) ? cursor.GetLong(6) : 0));
                }
            }
            catch (Java.Lang.SecurityException) { }
        }
        if (allowedCollections == 0) throw new UnauthorizedAccessException("Android media permission is unavailable.");
        return media;
    }, ct);
    private IReadOnlyList<DeviceMedia> EnumerateTree(MobileSource source, bool recursive, CancellationToken ct)
    {
        var root = AUri.Parse(source.Uri)!; var queue = new Queue<string>();
        queue.Enqueue(DocumentsContract.GetTreeDocumentId(root)!);
        var seen = new HashSet<string>(); var results = new List<DeviceMedia>();
        while (queue.TryDequeue(out var id))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(id)) continue;
            var children = DocumentsContract.BuildChildDocumentsUriUsingTree(root, id)!;
            using var cursor = _resolver.Query(children, ["document_id", "_display_name", "mime_type", "_size", "last_modified"], null, null, null);
            if (cursor == null) throw new IOException("Document provider unavailable.");
            while (cursor.MoveToNext())
            {
                ct.ThrowIfCancellationRequested();
                var childId = cursor.GetString(0)!; var name = cursor.GetString(1) ?? "media"; var mime = cursor.GetString(2) ?? "";
                if (mime == DocumentsContract.Document.MimeTypeDir) { if (recursive) queue.Enqueue(childId); continue; }
                if (!mime.StartsWith("image/", StringComparison.Ordinal) && !mime.StartsWith("video/", StringComparison.Ordinal) && !MediaFormats.IsMedia(name)) continue;
                var child = DocumentsContract.BuildDocumentUriUsingTree(root, childId)!;
                results.Add(new(child.ToString()!, name, mime.StartsWith("video/", StringComparison.Ordinal) || MediaFormats.Videos.Contains(Path.GetExtension(name)) ? MediaType.Video : MediaType.Image,
                    cursor.IsNull(3) ? 0 : cursor.GetLong(3), DateTimeOffset.FromUnixTimeMilliseconds(cursor.IsNull(4) ? 0 : cursor.GetLong(4)).UtcDateTime));
            }
        }
        return results;
    }
    public Task<DeviceMedia> DescribeAsync(string uri, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var cursor = _resolver.Query(AUri.Parse(uri)!, null, null, null, null);
        if (cursor?.MoveToFirst() != true) throw new IOException("This media is no longer accessible.");
        string Text(string column) { var index = cursor.GetColumnIndex(column); return index < 0 || cursor.IsNull(index) ? "" : cursor.GetString(index) ?? ""; }
        long Number(string column) => long.TryParse(Text(column), out var n) ? n : 0;
        var name = Text("_display_name"); if (name.Length == 0) name = "media";
        var mime = _resolver.GetType(AUri.Parse(uri)!);
        var video = mime?.StartsWith("video/", StringComparison.Ordinal) == true;
        var modified = Number("last_modified"); if (modified == 0) modified = Number("date_modified") * 1000;
        return new DeviceMedia(uri, name, video ? MediaType.Video : MediaType.Image, Number("_size"), DateTimeOffset.FromUnixTimeMilliseconds(modified).UtcDateTime,
            (int)Number("width"), (int)Number("height"), Number("duration"));
    }, ct);

    private string CacheFile(MediaFile media, string suffix) => Path.Combine(Paths.Cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{media.FullPath}|{media.SizeBytes}|{media.LastModifiedAt.Ticks}|android3|{suffix}"))) + ".jpg");
    public Task<string> ThumbnailAsync(MediaFile media, CancellationToken ct) => media.MediaType == MediaType.Video ? VideoFrameAsync(media, 0, ct) : Task.Run(async () =>
    {
        var path = CacheFile(media, "thumb"); if (File.Exists(path)) return path;
        await _decodeGate.WaitAsync(ct);
        try { using var decoded = Decode(media.FullPath, 480); SaveJpeg(decoded, path, 86); return path; }
        finally { _decodeGate.Release(); }
    }, ct);
    public Task<string> VideoFrameAsync(MediaFile media, long milliseconds, CancellationToken ct) => Task.Run(async () =>
    {
        var path = CacheFile(media, "frame-" + milliseconds); if (File.Exists(path)) return path;
        await _decodeGate.WaitAsync(ct);
        try
        {
            using var retriever = new MediaMetadataRetriever();
            await retriever.SetDataSourceAsync(_activity, AUri.Parse(media.FullPath)!);
            ct.ThrowIfCancellationRequested();
            var width = int.TryParse(retriever.ExtractMetadata(MetadataKey.VideoWidth), out var w) ? w : 640;
            var height = int.TryParse(retriever.ExtractMetadata(MetadataKey.VideoHeight), out var h) ? h : 640;
            var scale = Math.Min(1, 640d / Math.Max(width, height));
            using var frame = retriever.GetScaledFrameAtTime(milliseconds * 1000, Option.Closest, Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)))
                ?? throw new IOException("Android cannot decode a frame from this video.");
            SaveJpeg(frame, path, 88); return path;
        }
        finally { _decodeGate.Release(); }
    }, ct);
    public Task<Avalonia.Media.Imaging.Bitmap> PreviewAsync(MediaFile media, CancellationToken ct) => Task.Run(async () =>
    {
        await _decodeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var decoded = Decode(media.FullPath, 2560);
            var pixels = new int[decoded.Width * decoded.Height];
            decoded.GetPixels(pixels, 0, decoded.Width, 0, 0, decoded.Width, decoded.Height);
            ct.ThrowIfCancellationRequested();
            var target = new WriteableBitmap(new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = target.Lock();
            for (var y = 0; y < decoded.Height; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * decoded.Width, fb.Address + y * fb.RowBytes, decoded.Width);
            return (Avalonia.Media.Imaging.Bitmap)target;
        }
        finally { _decodeGate.Release(); }
    }, ct);
    private ABitmap Decode(string uri, int maxSize)
    {
        var parsed = uri.StartsWith("content:", StringComparison.Ordinal) ? AUri.Parse(uri)! : AUri.FromFile(new Java.IO.File(uri))!;
        using var source = ImageDecoder.CreateSource(_resolver, parsed);
        return ImageDecoder.DecodeBitmap(source, new HeaderListener(maxSize));
    }
    private sealed class HeaderListener(int maxSize) : Java.Lang.Object, ImageDecoder.IOnHeaderDecodedListener
    {
        public void OnHeaderDecoded(ImageDecoder decoder, ImageDecoder.ImageInfo info, ImageDecoder.Source source)
        {
            decoder.Allocator = ImageDecoderAllocator.Software;
            var scale = Math.Min(1, maxSize / (double)Math.Max(info.Size.Width, info.Size.Height));
            decoder.SetTargetSize(Math.Max(1, (int)(info.Size.Width * scale)), Math.Max(1, (int)(info.Size.Height * scale)));
        }
    }
    private static void SaveJpeg(ABitmap bitmap, string destination, int quality)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var file = File.Create(temporary))
                if (!bitmap.Compress(ABitmap.CompressFormat.Jpeg!, quality, file)) throw new IOException("Thumbnail encoding failed.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task<byte[]> ReadModelRgbAsync(string file, CancellationToken ct, bool squash = false) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var bitmap = Decode(file, 1024);
        using var output = ABitmap.CreateBitmap(224, 224, ABitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(output); canvas.DrawColor(Color.White);
        var scale = squash ? 1 : Math.Max(224d / bitmap.Width, 224d / bitmap.Height);
        var width = squash ? 224 : (float)(bitmap.Width * scale); var height = squash ? 224 : (float)(bitmap.Height * scale);
        using var paint = new Paint(PaintFlags.FilterBitmap);
        canvas.DrawBitmap(bitmap, null, new RectF((224 - width) / 2, (224 - height) / 2, (224 + width) / 2, (224 + height) / 2), paint);
        return Rgb(output);
    }, ct);
    public Task<byte[]> ReadFaceRgbAsync(string file, int size, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var bitmap = Decode(file, 2048);
        using var output = ABitmap.CreateBitmap(size, size, ABitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(output); canvas.DrawColor(Color.Black);
        var scale = Math.Min(1, size / (double)Math.Max(bitmap.Width, bitmap.Height));
        using var paint = new Paint(PaintFlags.FilterBitmap);
        canvas.DrawBitmap(bitmap, null, new RectF(0, 0, (float)(bitmap.Width * scale), (float)(bitmap.Height * scale)), paint);
        return Rgb(output);
    }, ct);
    public Task WriteFaceThumbnailAsync(byte[] rgb, int size, string destination, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var pixels = new int[size * size];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = unchecked((int)0xff000000) | rgb[i * 3] << 16 | rgb[i * 3 + 1] << 8 | rgb[i * 3 + 2];
        using var bitmap = ABitmap.CreateBitmap(pixels, size, size, ABitmap.Config.Argb8888!)!;
        SaveJpeg(bitmap, destination, 90);
    }, ct);
    private static byte[] Rgb(ABitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        bitmap.GetPixels(pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);
        var rgb = new byte[pixels.Length * 3];
        for (var i = 0; i < pixels.Length; i++) { rgb[i * 3] = (byte)(pixels[i] >> 16); rgb[i * 3 + 1] = (byte)(pixels[i] >> 8); rgb[i * 3 + 2] = (byte)pixels[i]; }
        return rgb;
    }
    public Control CreateVideoPlayer(MediaFile media, long timestamp, Action<string> error)
    {
        StopVideo(); _video = new(_activity, media.FullPath, timestamp, error); return _video;
    }
    public void StopVideo() { _activity.RunOnUiThread(() => _video?.Stop()); }
    public void HideKeyboard() => _activity.RunOnUiThread(() =>
    {
        if (_activity.GetSystemService(Context.InputMethodService) is InputMethodManager keyboard)
            keyboard.HideSoftInputFromWindow(_activity.Window?.DecorView?.WindowToken, HideSoftInputFlags.None);
    });
    public void KeepScreenOn(bool enabled) => _activity.RunOnUiThread(() =>
    {
        if (enabled) _activity.Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        else _activity.Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
    });
    public void Share(MediaFile media)
    {
        using var intent = new Intent(Intent.ActionSend);
        var uri = AUri.Parse(media.FullPath)!;
        intent.SetType(_resolver.GetType(uri) ?? (media.MediaType == MediaType.Image ? "image/*" : "video/*"));
        intent.PutExtra(Intent.ExtraStream, uri); intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        intent.ClipData = ClipData.NewRawUri(media.FileName, uri);
        _activity.StartActivity(Intent.CreateChooser(intent, "PixSeek"));
    }
}
