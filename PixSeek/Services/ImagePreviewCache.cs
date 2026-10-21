using Avalonia.Media.Imaging;
using PixSeek.Core;
using PixSeek.Media;
using System.Security.Cryptography;
using System.Text;

namespace PixSeek.Services;

/// <summary>Short cache locks; foreground decoding and speculative prefetch have separate worker slots.</summary>
public sealed class ImagePreviewCache(IPlatformPaths paths, ImageMagickService fallback, IImagePreviewDecoder decoder) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _foreground = new(1, 1), _prefetch = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, Pending> _pending = [];
    private int _generation;
    private bool _disposed;
    public int DecodeCount { get; private set; }
    public int CacheHits { get; private set; }

    private sealed class Entry(Bitmap bitmap)
    {
        public Bitmap Bitmap = bitmap;
        public int References;
        public bool Cached;
        public long LastUse = Environment.TickCount64;
    }
    private sealed class Pending
    {
        public Task<Entry> Task = null!;
        public Entry? Entry;
        public int Waiters;
    }
    public sealed class Lease(Bitmap bitmap, Action release) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public Task<Lease> LoadAsync(string path, CancellationToken ct) => LoadCoreAsync(path, ct, false);

    public async Task PrefetchAsync(string path, CancellationToken ct)
    {
        try { using var lease = await LoadCoreAsync(path, ct, true).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Speculation never interrupts the current media. Opening retries and reports errors. */ }
    }

    private async Task<Lease> LoadCoreAsync(string path, CancellationToken ct, bool prefetch)
    {
        // File attributes on removable/network storage can block too; never query them on the UI thread.
        var key = await Task.Run(() =>
        {
            var info = new FileInfo(path);
            return path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }, ct).ConfigureAwait(false);
        Pending pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(key, out var cached))
            {
                CacheHits++;
                cached.References++;
                cached.LastUse = Environment.TickCount64;
                return LeaseFor(cached);
            }
            if (!_pending.TryGetValue(key, out pending!))
            {
                pending = new Pending { Waiters = 1 };
                var generation = _generation;
                _pending[key] = pending;
                pending.Task = Task.Run(() => DecodeAndStoreAsync(path, key, pending, generation, prefetch));
                _ = pending.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            else pending.Waiters++;
        }
        try
        {
            var entry = await pending.Task.WaitAsync(ct).ConfigureAwait(false);
            return LeaseFor(entry);
        }
        catch
        {
            lock (_gate)
            {
                if (pending.Entry is { } entry) Release(entry);
                else pending.Waiters--;
            }
            throw;
        }
    }

    private async Task<Entry> DecodeAndStoreAsync(string path, string key, Pending pending, int generation, bool prefetch)
    {
        var slot = prefetch ? _prefetch : _foreground;
        var ct = _lifetime.Token;
        try
        {
            await slot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate)
                    if (pending.Waiters == 0) throw new OperationCanceledException();
                Bitmap bitmap;
                try { bitmap = decoder.Decode(path); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    var cache = Path.Combine(paths.Cache, "preview-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");
                    if (!File.Exists(cache))
                    {
                        var temp = cache + "." + Guid.NewGuid().ToString("N") + ".partial.jpg";
                        try
                        {
                            await fallback.ThumbnailAsync(path, temp, 4096, ct).ConfigureAwait(false);
                            File.Move(temp, cache, true);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                    }
                    bitmap = decoder.Decode(cache);
                }
                lock (_gate)
                {
                    DecodeCount++;
                    var entry = new Entry(bitmap) { References = pending.Waiters, Cached = !_disposed && generation == _generation };
                    pending.Entry = entry;
                    if (_pending.GetValueOrDefault(key) == pending) _pending.Remove(key);
                    if (entry.Cached)
                    {
                        _entries[key] = entry;
                        foreach (var old in _entries.Where(p => p.Key != key).OrderBy(p => p.Value.LastUse).Take(Math.Max(0, _entries.Count - 3)).ToArray())
                        {
                            _entries.Remove(old.Key);
                            Retire(old.Value);
                        }
                    }
                    else if (entry.References == 0) entry.Bitmap.Dispose();
                    return entry;
                }
            }
            finally { slot.Release(); }
        }
        catch
        {
            lock (_gate)
                if (_pending.GetValueOrDefault(key) == pending) _pending.Remove(key);
            throw;
        }
    }

    private Lease LeaseFor(Entry entry) => new(entry.Bitmap, () => { lock (_gate) Release(entry); });
    private static void Release(Entry entry)
    {
        entry.References--;
        if (!entry.Cached && entry.References == 0) entry.Bitmap.Dispose();
    }
    private static void Retire(Entry entry)
    {
        entry.Cached = false;
        if (entry.References == 0) entry.Bitmap.Dispose();
    }
    public Task ClearAsync()
    {
        lock (_gate)
        {
            ++_generation;
            foreach (var entry in _entries.Values) Retire(entry);
            _entries.Clear();
            _pending.Clear(); // Older decodes finish for their leases, but cannot repopulate the cleared cache.
        }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            ++_generation;
            foreach (var entry in _entries.Values) Retire(entry);
            _entries.Clear();
        }
    }
}
