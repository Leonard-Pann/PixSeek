using PixSeek.Core;

namespace PixSeek.Infrastructure;
public sealed class LibraryMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _debounce;
    private readonly CancellationTokenSource _stop = new();
    private string _signature = "";
    public event Action? ChangeRequested;
    private Task? _periodic;
    public TimeSpan ReconciliationInterval { get; } = TimeSpan.FromMinutes(1);

    public void Update(IReadOnlyList<LibraryFolder> folders)
    {
        var signature = string.Join(';', folders.Select(f => f.Path + ':' + f.Recursive + ':' + Directory.Exists(f.Path)));
        if (_signature == signature && _periodic != null)
            return;
        _signature = signature;
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        foreach (var folder in folders.Where(f => Directory.Exists(f.Path)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = folder.Recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += Signal;
                watcher.Deleted += Signal;
                watcher.Changed += Signal;
                watcher.Renamed += (sender, args) =>
                {
                    if (MediaFormats.IsMedia(args.OldFullPath) || MediaFormats.IsMedia(args.FullPath))
                        Schedule();
                };
                watcher.Error += (_, _) => Schedule();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception e)when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        _periodic ??= PeriodicAsync();
    }

    private void Signal(object sender, FileSystemEventArgs args)
    {
        if (MediaFormats.IsMedia(args.FullPath))
            Schedule();
    }

    private void Schedule()
    {
        lock (_watchers)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _ = DebounceAsync(_debounce.Token);
        }
    }

    private async Task DebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(750, ct);
            ChangeRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PeriodicAsync()
    {
        using var timer = new PeriodicTimer(ReconciliationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                ChangeRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_watchers)
        {
            _debounce?.Cancel();
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();
        }
    }
}
