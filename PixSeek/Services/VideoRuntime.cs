using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using PixSeek.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PixSeek.Services;

/// <summary>Plugin discovery/native initialization is shared across clips, not repeated at every navigation.</summary>
public sealed class VideoRuntime(IPlatformPaths paths, ILogger<VideoRuntime> logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly object _startupGate = new();
    private readonly Dictionary<bool, LibVLC> _libraries = [];
    private readonly SemaphoreSlim _players = new(2, 2);
    private Task? _warmup;
    private volatile bool _disposed;
    public int InitializationCount { get; private set; }
    public bool IsWarm { get; private set; }
    public TimeSpan WarmupDuration { get; private set; }

    /// <summary>One background initialization per process, including a real H.264/AAC decode.
    /// Never plays a user file or emits sound. Concurrent video opens join the same task.</summary>
    public Task WarmUpAsync()
    {
        lock (_startupGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _warmup ??= Task.Run(WarmUpCoreAsync);
        }
    }

    private async Task WarmUpCoreAsync()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var library = Get(false);
            var directory = Path.Combine(paths.Cache, "player");
            Directory.CreateDirectory(directory);
            var clip = Path.Combine(directory, "warmup-h264-aac-v1.mp4");
            if (!File.Exists(clip))
            {
                using var source = typeof(VideoRuntime).Assembly.GetManifestResourceStream("PixSeek.PlaybackWarmup.mp4")
                    ?? throw new FileNotFoundException("Embedded playback warmup clip is missing.");
                var temporary = clip + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    await using (var output = File.Create(temporary)) await source.CopyToAsync(output).ConfigureAwait(false);
                    File.Move(temporary, clip, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var buffer = Marshal.AllocHGlobal(16 * 16 * 4);
            try
            {
                using var player = new MediaPlayer(library) { Mute = true, Volume = 0 };
                player.SetVideoFormat("RV32", 16, 16, 16 * 4);
                player.SetVideoCallbacks((_, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; },
                    (_, _, _) => { }, (_, _) => firstFrame.TrySetResult());
                player.EncounteredError += (_, _) => firstFrame.TrySetException(new InvalidOperationException("Playback warmup failed."));
                using var media = new LibVLCSharp.Shared.Media(library, clip, FromType.FromPath);
                media.AddOption(":file-caching=100");
                if (!player.Play(media)) throw new InvalidOperationException("Playback warmup could not start.");
                try { await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); IsWarm = true; }
                finally { player.Stop(); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            logger.LogInformation("Video runtime prepared in {Milliseconds} ms", timer.ElapsedMilliseconds);
        }
        catch (Exception error)
        {
            // Warmup is speculative. A missing decoder must not prevent the gallery from starting;
            // opening a real clip still retries and reports its own error.
            logger.LogWarning(error, "Video runtime background preparation failed");
        }
        finally { WarmupDuration = timer.Elapsed; }
    }
    public async Task<EmbeddedVideoSession> CreateSessionAsync(int width, int height, CancellationToken ct)
    {
        await WarmUpAsync().WaitAsync(ct).ConfigureAwait(false);
        await _players.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => new EmbeddedVideoSession(this, width, height, () => _players.Release()), ct).ConfigureAwait(false); }
        catch { _players.Release(); throw; }
    }
    internal LibVLC Get(bool silent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_libraries.TryGetValue(silent, out var library)) return library;
            EmbeddedVideoSession.InitializeNative();
            library = new LibVLC(silent ? ["--quiet", "--no-video-title-show", "--avcodec-hw=none", "--aout=dummy"] : ["--quiet", "--no-video-title-show", "--avcodec-hw=none"]);
            _libraries.Add(silent, library);
            InitializationCount++;
            return library;
        }
    }
    public void Dispose()
    {
        Task? warmup;
        lock (_startupGate) { _disposed = true; warmup = _warmup; }
        // Keep native libraries alive until the speculative player has stopped.
        if (warmup is { IsCompleted: false })
        {
            _ = warmup.ContinueWith(_ => ReleaseLibraries(), TaskScheduler.Default);
            return;
        }
        ReleaseLibraries();
    }
    private void ReleaseLibraries()
    {
        lock (_gate)
        {
            foreach (var library in _libraries.Values) library.Dispose();
            _libraries.Clear();
        }
    }
}
