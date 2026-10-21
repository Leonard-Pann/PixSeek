using LibVLCSharp.Shared;

namespace PixSeek.Services;

/// <summary>Plugin discovery/native initialization is shared across clips, not repeated at every navigation.</summary>
public sealed class VideoRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<bool, LibVLC> _libraries = [];
    private readonly SemaphoreSlim _players = new(2, 2);
    public int InitializationCount { get; private set; }
    public async Task<EmbeddedVideoSession> CreateSessionAsync(int width, int height, CancellationToken ct)
    {
        await _players.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => new EmbeddedVideoSession(this, width, height, () => _players.Release()), ct).ConfigureAwait(false); }
        catch { _players.Release(); throw; }
    }
    internal LibVLC Get(bool silent)
    {
        lock (_gate)
        {
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
        lock (_gate)
        {
            foreach (var library in _libraries.Values) library.Dispose();
            _libraries.Clear();
        }
    }
}
