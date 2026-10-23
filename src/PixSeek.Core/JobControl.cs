namespace PixSeek.Core;
/// <summary>Cooperative pause at file boundaries. Cancellation also stops an active native process.</summary>
public sealed class JobControl : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _resume = Completed();
    public CancellationToken Token => _cts.Token;
    public bool IsPaused { get; private set; }

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Pause()
    {
        if (IsPaused)
            return;
        _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
        _resume.TrySetResult();
    }

    public void Cancel()
    {
        _cts.Cancel();
        Resume();
    }

    public Task CheckpointAsync() => _resume.Task.WaitAsync(Token);
    public Task WaitAsync(CancellationToken cancellationToken) => _resume.Task.WaitAsync(cancellationToken);
    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }
}
