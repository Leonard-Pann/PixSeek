using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.Media;
using PixSeek.Services;

namespace PixSeek.ViewModels;

public partial class MediaViewerViewModel(ImagePreviewCache previews, FfprobeService probe) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isVideo;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private double _volume = 80;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private string _errorMessage = "";
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsSilent => Muted || Volume == 0;
    public bool HasEnded => _ended;
    public event Action? FrameChanged;
    public string PlaybackIcon => IsPlaying ? "Ⅱ" : "▶";
    // Round container fractions to seconds; 74.98s displays as 1:15, and 75.02s also as 1:15.
    private double DisplayDurationSeconds => Math.Round(Duration / 1000, MidpointRounding.AwayFromZero);
    public string TimeLabel => FormatTime(_ended && !_seekAfterEnd ? DisplayDurationSeconds : Math.Min(Position / 1000, DisplayDurationSeconds))
        + " / " + FormatTime(DisplayDurationSeconds);
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds))).ToString(@"hh\:mm\:ss");

    private IReadOnlyList<MediaCardViewModel> _cards = [];
    private int _index, _generation;
    private CancellationTokenSource? _loading;
    private ImagePreviewCache.Lease? _lease;
    private EmbeddedVideoSession? _video;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _updatingTime, _ended, _seekAfterEnd;
    private double? _pendingSeek;
    private long _seekStarted;
    public Task PendingLoad { get; private set; } = Task.CompletedTask;
    public int RenderedVideoFrames => _video?.RenderedFrames ?? 0;

    public Task OpenAsync(IReadOnlyList<MediaCardViewModel> cards, MediaCardViewModel selected)
    {
        _cards = cards.ToArray();
        _index = Array.IndexOf(_cards.ToArray(), selected);
        if (_index < 0) { _cards = [selected]; _index = 0; }
        IsOpen = true;
        return PendingLoad = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new();
        var ct = _loading.Token;
        var card = _cards[_index];
        // Clear synchronously, before disposal, probing or decoding: navigation is visibly acknowledged.
        IsLoading = true;
        ErrorMessage = "";
        Image = null;
        IsPlaying = false;
        _clock.Stop();
        _ended = _seekAfterEnd = false;
        _pendingSeek = null;
        var previous = _video;
        _video = null;
        var previousLease = _lease;
        _lease = null;
        IsVideo = card.IsVideo;
        Duration = 1;
        UpdateTime(0);
        try
        {
            // Always let Avalonia paint the black loading surface, including warm-cache navigation.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (previous != null) await previous.DisposeAsync();
            previousLease?.Dispose();
            if (generation != _generation) return;
            if (card.IsVideo)
            {
                var metadata = await Task.Run(() => probe.ProbeAsync(card.Media.FullPath, ct), ct);
                var size = FfprobeService.DisplaySize(metadata);
                var session = await Task.Run(() => new EmbeddedVideoSession(size.Width, size.Height), ct);
                if (generation != _generation || ct.IsCancellationRequested)
                {
                    await session.DisposeAsync();
                    return;
                }
                _video = session;
                Image = session.Frame;
                session.FrameAvailable += () =>
                {
                    if (!ReferenceEquals(_video, session)) return;
                    IsLoading = false;
                    FrameChanged?.Invoke();
                };
                session.Player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_video, session)) return;
                    _ended = true;
                    _seekAfterEnd = false;
                    _pendingSeek = null;
                    IsPlaying = false;
                    _clock.Stop();
                    UpdateTime(Duration);
                    OnPropertyChanged(nameof(HasEnded));
                });
                session.Player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_video, session)) return;
                    ErrorMessage = "Unable to decode this video.";
                    IsLoading = false;
                    IsPlaying = false;
                });
                Duration = Math.Max(1, metadata.DurationMilliseconds);
                UpdateTime(card.Timestamp);
                session.Player.Volume = (int)Volume;
                session.Player.Mute = Muted;
                await Task.Run(() => session.Play(card.Media.FullPath, card.Timestamp), ct);
                if (generation != _generation) return;
                IsPlaying = true;
                _clock.Tick -= Tick;
                _clock.Tick += Tick;
                _clock.Start();
            }
            else
            {
                var lease = await previews.LoadAsync(card.Media.FullPath, ct);
                if (generation != _generation || ct.IsCancellationRequested) { lease.Dispose(); return; }
                _lease = lease;
                Image = lease.Bitmap;
                IsLoading = false;
                if (_cards.Count > 1)
                {
                    var next = _cards[(_index + 1) % _cards.Count];
                    if (!next.IsVideo) _ = PrefetchAsync(next.Media.FullPath, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (generation == _generation) { ErrorMessage = e.Message; IsLoading = false; }
        }
    }

    private async Task PrefetchAsync(string path, CancellationToken ct)
    {
        try { using var lease = await previews.LoadAsync(path, ct); }
        catch (Exception) { }
    }

    private void UpdateTime(double value)
    {
        _updatingTime = true;
        try { Position = Math.Clamp(value, 0, Duration); }
        finally { _updatingTime = false; }
        OnPropertyChanged(nameof(TimeLabel));
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_video == null || _ended) return;
        if (_video.Player.Length > 0) Duration = Math.Max(Duration, _video.Player.Length);
        var time = Math.Max(0, _video.Player.Time);
        if (_pendingSeek.HasValue)
        {
            // VLC time samples can lag behind a seek. Do not snap the thumb back to the old position.
            if (Math.Abs(time - _pendingSeek.Value) < 600 || Environment.TickCount64 - _seekStarted > 1500)
                _pendingSeek = null;
            else return;
        }
        UpdateTime(time);
        IsPlaying = _video.Player.IsPlaying;
    }

    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(TimeLabel));
        if (_updatingTime || _video == null) return;
        if (_ended) { _seekAfterEnd = true; OnPropertyChanged(nameof(TimeLabel)); return; }
        _pendingSeek = Math.Clamp(value, 0, Duration);
        _seekStarted = Environment.TickCount64;
        _video.Player.Time = (long)_pendingSeek.Value;
    }

    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(TimeLabel));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlaybackIcon));
    partial void OnVolumeChanged(double value)
    {
        if (_video != null) _video.Player.Volume = (int)value;
        OnPropertyChanged(nameof(IsSilent));
    }
    partial void OnMutedChanged(bool value)
    {
        if (_video != null) _video.Player.Mute = value;
        OnPropertyChanged(nameof(IsSilent));
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private async Task TogglePlaybackAsync()
    {
        var session = _video;
        if (session == null) return;
        try
        {
            if (_ended)
            {
                var timestamp = _seekAfterEnd && Position < Duration ? (long)Position : 0;
                var path = _cards[_index].Media.FullPath;
                _ended = _seekAfterEnd = false;
                _pendingSeek = timestamp;
                _seekStarted = Environment.TickCount64;
                UpdateTime(timestamp);
                IsLoading = true;
                // After EndReached VLC no longer has an active input. Time/Play alone are ineffective.
                await Task.Run(() => session.Play(path, timestamp));
                if (!ReferenceEquals(_video, session)) return;
                IsPlaying = true;
                _clock.Start();
                OnPropertyChanged(nameof(HasEnded));
            }
            else
            {
                var pause = session.Player.IsPlaying;
                await session.SetPausedAsync(pause);
                if (ReferenceEquals(_video, session)) IsPlaying = !pause;
            }
        }
        catch (Exception e) { if (ReferenceEquals(_video, session)) { ErrorMessage = e.Message; IsLoading = false; } }
    }

    [RelayCommand] private void ToggleMute() => Muted = !Muted;
    [RelayCommand(AllowConcurrentExecutions = true)] private Task PreviousAsync() => MoveAsync(-1);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task NextAsync() => MoveAsync(1);
    private Task MoveAsync(int step)
    {
        if (_cards.Count == 0) return Task.CompletedTask;
        _index = (_index + step + _cards.Count) % _cards.Count;
        return PendingLoad = LoadAsync();
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        IsOpen = false;
        ++_generation;
        _loading?.Cancel();
        _clock.Stop();
        Image = null;
        IsLoading = false;
        IsPlaying = false;
        var video = _video;
        _video = null;
        var lease = _lease;
        _lease = null;
        if (video != null) await video.DisposeAsync();
        lease?.Dispose();
        await PendingLoad;
    }
    public void Stop() => CloseCommand.Execute(null);
}
