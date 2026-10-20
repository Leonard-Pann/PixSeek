using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using PixSeek.Core;
using PixSeek.Media;
using System.Security.Cryptography;
using System.Text;

namespace PixSeek.Services;
public sealed class ImagePreviewCache(IPlatformPaths paths, ImageMagickService fallback) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = [];
    public int DecodeCount { get; private set; }
    public int CacheHits { get; private set; }

    private sealed class Entry(Bitmap bitmap)
    {
        public Bitmap Bitmap = bitmap;
        public int References;
        public bool Cached = true;
        public long LastUse = Environment.TickCount64;
    }

    public sealed class Lease(Bitmap bitmap, Action release) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;

        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public async Task<Lease> LoadAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                Bitmap bitmap;
                try
                {
                    bitmap = await Task.Run(() => Decode(path), ct);
                }
                catch (Exception e)when (e is not OperationCanceledException)
                {
                    // Native Skia handles common formats directly; uncommon codecs get a persistent preview.
                    var cache = Path.Combine(paths.Cache, "preview-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");
                    if (!File.Exists(cache))
                    {
                        var temp = cache + ".partial.jpg";
                        try
                        {
                            await fallback.ThumbnailAsync(path, temp, 4096, ct);
                            File.Move(temp, cache, true);
                        }
                        finally
                        {
                            if (File.Exists(temp))
                                File.Delete(temp);
                        }
                    }

                    bitmap = await Task.Run(() => Decode(cache), ct);
                }

                DecodeCount++;
                _entries[key] = entry = new(bitmap);
            }
            else
                CacheHits++;
            lock (entry)
            {
                entry.References++;
                entry.LastUse = Environment.TickCount64;
            }

            foreach (var old in _entries.Where(p => p.Key != key).OrderBy(p => p.Value.LastUse).Take(Math.Max(0, _entries.Count - 3)).ToArray())
            {
                _entries.Remove(old.Key);
                Retire(old.Value);
            }

            return new(entry.Bitmap, () =>
            {
                lock (entry)
                {
                    entry.References--;
                    if (!entry.Cached && entry.References == 0)
                        entry.Bitmap.Dispose();
                }
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Bitmap Decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unsupported image.");
        var scale = Math.Min(1f, 4096f / Math.Max(codec.Info.Width, codec.Info.Height));
        var dimensions = codec.GetScaledDimensions(scale);
        using var decoded = new SKBitmap(new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException(result.ToString());
        var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        using var oriented = new SKBitmap(swap ? decoded.Height : decoded.Width, swap ? decoded.Width : decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(oriented);
        switch (codec.EncodedOrigin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(decoded.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(decoded.Width, decoded.Height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, decoded.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(decoded.Height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(decoded.Height, decoded.Width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, decoded.Width);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(decoded, 0, 0);
        canvas.Flush();
        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, oriented.GetPixels(), new PixelSize(oriented.Width, oriented.Height), new Vector(96, 96), oriented.RowBytes);
    }

    private static void Retire(Entry entry)
    {
        lock (entry)
        {
            entry.Cached = false;
            if (entry.References == 0)
                entry.Bitmap.Dispose();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var entry in _entries.Values)
                Retire(entry);
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            Retire(entry);
        _entries.Clear();
        _gate.Dispose();
    }
}
