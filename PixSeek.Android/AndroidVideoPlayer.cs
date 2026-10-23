using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using AUri = Android.Net.Uri;

namespace PixSeek.Android;

public sealed class AndroidVideoPlayer(Context context, string uri, long timestamp, Action<string> error) : NativeControlHost
{
    private VideoView? _video;
    private MediaController? _controller;
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var container = new FrameLayout(context);
        container.SetBackgroundColor(Color.Black);
        _video = new VideoView(context);
        container.AddView(_video, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent, GravityFlags.Center));
        _controller = new MediaController(context);
        _controller.SetAnchorView(_video); _video.SetMediaController(_controller);
        _video.Prepared += (_, e) =>
        {
            if (timestamp > 0) _video.SeekTo((int)Math.Min(timestamp, int.MaxValue));
            _video.Start();
        };
        _video.Completion += (_, _) => { _video.SeekTo(0); _video.Pause(); _controller.Show(0); };
        _video.Error += (_, e) => { e.Handled = true; error("Android cannot play this video format (" + e.What + ")."); };
        _video.SetVideoURI(AUri.Parse(uri));
        return new AndroidViewControlHandle(container);
    }
    public void Stop() { _controller?.Hide(); _video?.StopPlayback(); }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Stop(); _video?.Dispose(); _video = null; _controller?.Dispose(); _controller = null;
        if (control is AndroidViewControlHandle android) android.View.Dispose();
    }
}
