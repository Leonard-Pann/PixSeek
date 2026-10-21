using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace PixSeek.Services;
/// <summary>LibVLC renders into an Avalonia bitmap: no native child window or detached overlay window.</summary>
public sealed class EmbeddedVideoSession : IAsyncDisposable
{
    private readonly LibVLC _lib;
    public MediaPlayer Player { get; }

    private readonly object _frameGate = new();
    private readonly object _playbackGate = new();
    private IntPtr _buffer;
    private readonly uint _width, _height, _pitch;
    private volatile bool _disposed;
    private int _queued;
    public WriteableBitmap Frame { get; }

    public event Action? FrameAvailable;
    public int RenderedFrames { get; private set; }

    public static void InitializeNative()
    {
        var rid = $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var staged = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "vlc");
        if (Directory.Exists(staged))
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(staged, "plugins"));
            LibVLCSharp.Shared.Core.Initialize(Path.Combine(staged, "lib"));
        }
        else
            LibVLCSharp.Shared.Core.Initialize();
    }

    public EmbeddedVideoSession(int width, int height, bool silent = false)
    {
        InitializeNative();
        _lib = new LibVLC(silent ? ["--quiet", "--no-video-title-show", "--avcodec-hw=none", "--aout=dummy"] : ["--quiet", "--no-video-title-show", "--avcodec-hw=none"]);
        Player = new MediaPlayer(_lib);
        var factor = Math.Min(1d, Math.Min(1920d / Math.Max(1, width), 1080d / Math.Max(1, height)));
        _width = (uint)Math.Max(16, (int)(width * factor));
        _height = (uint)Math.Max(16, (int)(height * factor));
        _pitch = (_width * 4 + 31) & ~31u;
        _buffer = Marshal.AllocHGlobal(checked((int)(_pitch * _height)));
        Frame = new WriteableBitmap(new PixelSize((int)_width, (int)_height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        Player.SetVideoFormat("RV32", _width, _height, _pitch);
        Player.SetVideoCallbacks((opaque, planes) =>
        {
            Monitor.Enter(_frameGate);
            Marshal.WriteIntPtr(planes, _buffer);
            return IntPtr.Zero;
        }, (opaque, picture, planes) => Monitor.Exit(_frameGate), (opaque, picture) =>
        {
            if (!_disposed && Interlocked.Exchange(ref _queued, 1) == 0)
                Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
        });
    }

    private unsafe void CopyFrame()
    {
        try
        {
            if (_disposed)
                return;
            lock (_frameGate)
            {
                using var target = Frame.Lock();
                for (var y = 0; y < _height; y++)
                    new ReadOnlySpan<byte>((void*)(_buffer + (int)(y * _pitch)), (int)_width * 4).CopyTo(new Span<byte>((void*)(target.Address + (int)y * target.RowBytes), (int)_width * 4));
            }

            RenderedFrames++;
            FrameAvailable?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _queued, 0);
        }
    }

    public void Play(string path, long timestamp = 0)
    {
        lock (_playbackGate)
        {
            if (_disposed) return;
            using var media = new LibVLCSharp.Shared.Media(_lib, path, FromType.FromPath);
            media.AddOption(":start-time=" + (Math.Max(0, timestamp) / 1000d).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Player.Play(media))
                throw new InvalidOperationException("Unable to play this video.");
        }
    }

    public Task SetPausedAsync(bool paused) => Task.Run(() =>
    {
        lock (_playbackGate)
            if (!_disposed) Player.SetPause(paused);
    });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await Task.Run(() =>
        {
            lock (_playbackGate)
            {
                Player.Stop();
                Player.Dispose();
                _lib.Dispose();
            }
        });
        lock (_frameGate)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            Frame.Dispose();
        }
    }
}
