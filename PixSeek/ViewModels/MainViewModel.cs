using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.Logging;
using PixSeek.Services;
using PixSeek.Localization;

namespace PixSeek.ViewModels;
public partial class MainViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private readonly LibraryIndexer _indexer;
    private readonly NativeTools _tools;
    private readonly ModelManager _models;
    private readonly ILogger<MainViewModel> _logger;
    private readonly LibraryMonitor _monitor;
    private readonly VideoRuntime _videoRuntime;
    private JobControl? _indexControl;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshingGallery, _rescanRequested, _stopped;
    private int _modelScanRequest;
    private int _facesDirty;
    private CancellationTokenSource? _startupCancellation;
    private CancellationTokenSource? _toolsCancellation;
    private int _compressionOpenRequest;
    public Task ToolPreparation { get; private set; } = Task.CompletedTask;
    private Task _toolObservation = Task.CompletedTask;
    public Task PendingCompressionOpen { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private bool _isPreparingCompression;
    // A view supplies a one-shot viewport signal. Headless consumers need no thumbnail wait.
    public Func<CancellationToken, Task>? WaitForInitialGalleryAsync { get; set; }
    [ObservableProperty] private bool _isPreparingLibrary = true;
    public bool IsLibraryInteractive => !IsPreparingLibrary && !IsDialogOpen;
    partial void OnIsPreparingLibraryChanged(bool value) => OnPropertyChanged(nameof(IsLibraryInteractive));
    public LibraryViewModel Library { get; }
    public OptimizerViewModel Optimizer { get; }
    public SettingsViewModel Settings { get; }
    public MediaViewerViewModel Viewer { get; }
    public PeopleViewModel People { get; }

    [ObservableProperty]
    private int _pageIndex = 1;
    [ObservableProperty]
    private string _status = "";
    [ObservableProperty]
    private string _errorMessage = "";
    [ObservableProperty]
    private bool _isIndexing;
    [ObservableProperty]
    private bool _isPaused;
    [ObservableProperty]
    private double _indexProgress;
    [ObservableProperty]
    private bool _isIndexProgressIndeterminate;
    [ObservableProperty]
    private bool _isCompressionDialogOpen;
    [ObservableProperty] private bool _isPersonDialogOpen;
    public bool IsDialogOpen => IsCompressionDialogOpen || IsPersonDialogOpen;
    partial void OnIsCompressionDialogOpenChanged(bool value) { OnPropertyChanged(nameof(IsDialogOpen)); OnPropertyChanged(nameof(IsLibraryInteractive)); }
    partial void OnIsPersonDialogOpenChanged(bool value) { OnPropertyChanged(nameof(IsDialogOpen)); OnPropertyChanged(nameof(IsLibraryInteractive)); }
    public bool IsGallery => PageIndex != 4;
    public bool IsSettings => PageIndex == 4;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public MainViewModel(LibraryDatabase db, LibraryIndexer indexer, NativeTools tools, ModelManager models, LibraryViewModel library, OptimizerViewModel optimizer, SettingsViewModel settings, MediaViewerViewModel viewer, LibraryMonitor monitor, ILogger<MainViewModel> logger, PeopleViewModel people, VideoRuntime videoRuntime)
    {
        _db = db;
        _indexer = indexer;
        _tools = tools;
        _models = models;
        _logger = logger;
        _monitor = monitor;
        _videoRuntime = videoRuntime;
        Library = library;
        Optimizer = optimizer;
        Settings = settings;
        Viewer = viewer;
        People = people;
        People.Error = ShowError;
        Library.AddPerson = paths => { IsPersonDialogOpen = true; People.Prepare(paths); };
        People.RequestClose = () => IsPersonDialogOpen = false;
        People.IndexUpdated = async () =>
        {
            await Library.RefreshPeopleAsync();
            await Library.RefreshInBackgroundAsync();
        };
        _indexer.PhotoPrepared += () => Interlocked.Exchange(ref _facesDirty, 1);
        Library.Error = ShowError;
        Optimizer.Error = ShowError;
        Settings.Error = ShowError;
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SettingsViewModel.IsModelBusy))
                return;
            Library.IsSearchBlocked = Settings.IsModelBusy;
            if (!Settings.IsModelBusy)
                Library.RefreshCommand.Execute(null);
        };
        Library.OpenMedia = card =>
        {
            _ = Viewer.OpenAsync(Library.Cards, card);
        };
        Library.OpenSettings = () => PageIndex = 4;
        Library.OptimizeFiles = ShowCompression;
        Library.AddFolder = AddFolderAsync;
        Optimizer.RequestClose = () => IsCompressionDialogOpen = false;
        Settings.BeforeModelChange = CancelAndWaitAsync;
        Settings.AfterModelReady = () =>
        {
            Library.InvalidateQuery();
            _ = StartModelScanAsync();
            return Task.CompletedTask;
        };
        Settings.AfterSamplingChanged = ReindexAsync;
        Settings.AfterCacheClear = async () =>
        {
            await CancelAndWaitAsync();
            await RescanAsync();
        };
        Settings.AfterLanguageChanged = () => Library.LoadAsync(resetPage: false);
        _monitor.ChangeRequested += () => Dispatcher.UIThread.Post(() =>
        {
            if (!_stopped)
                _ = CheckForLibraryChangesAsync();
        });
        _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (Interlocked.Exchange(ref _facesDirty, 0) != 0 && !_stopped) People.QueueIndex();
            if ((!IsIndexing && !People.IsIndexing) || _refreshingGallery)
                return;
            _refreshingGallery = true;
            try
            {
                if (IsGallery)
                    await Library.RefreshInBackgroundAsync();
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _refreshingGallery = false;
            }
        };
    }

    private async Task StartModelScanAsync()
    {
        var request = ++_modelScanRequest;
        try
        {
            var folders = await Task.Run(() => _db.FoldersAsync());
            if (request == _modelScanRequest && !_stopped)
                await RunScanAsync(folders, false);
        }
        catch (Exception e) { ShowError(e.Message); }
    }

    partial void OnPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsGallery));
        OnPropertyChanged(nameof(IsSettings));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    public async Task InitializeAsync()
    {
        using var startup = new CancellationTokenSource();
        _startupCancellation = startup;
        var token = startup.Token;
        _stopped = false;
        People.Resume();
        try
        {
            await Settings.LoadAsync();
            // Let the initial overlay render before starting any synchronous preparation.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            _ = _videoRuntime.WarmUpAsync();
            await Task.Run(() => _db.InitializeAsync(token), token).WaitAsync(token);
            if (_models.FilesPresent)
                await Task.Run(() => _db.ConfigureVectorsAsync(_models.Version, _models.Manifest.Dimensions, token, modelName: _models.Name), token).WaitAsync(token);
            Optimizer.LoadSettings();
            await Library.LoadAsync().WaitAsync(token);
            if (IsPreparingLibrary && !HasError && Library.Cards.Count > 0 && WaitForInitialGalleryAsync is { } wait)
            {
                using var viewport = CancellationTokenSource.CreateLinkedTokenSource(token);
                // Safety ceiling for broken decoders, hidden/minimized windows or no realized cards.
                // This is a maximum wait, never a minimum display duration or progress estimate.
                viewport.CancelAfter(TimeSpan.FromSeconds(3));
                try { await wait(viewport.Token).WaitAsync(viewport.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            IsPreparingLibrary = false;
            // Optional codec probes must not compete with the first viewport or hold its loader.
            StartToolPreparation();
            Status = TranslationService.Text("Ready");
            var folders = await _db.FoldersAsync();
            token.ThrowIfCancellationRequested();
            _refreshTimer.Start();
            _monitor.Update(folders);
            People.QueueIndex();
            await CheckForLibraryChangesAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
        finally
        {
            IsPreparingLibrary = false;
            if (ReferenceEquals(_startupCancellation, startup)) _startupCancellation = null;
        }
    }

    public void ShowError(string message)
    {
        ErrorMessage = message;
        _logger.LogError("{Message}", message);
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = "";
    [RelayCommand]
    private void Navigate(string page)
    {
        if (page == "3")
            ShowCompression([]);
        else
            PageIndex = page == "4" ? 4 : 1;
    }

    public void ShowCompression(IReadOnlyList<string> files)
    {
        IsCompressionDialogOpen = true;
        PendingCompressionOpen = OpenCompressionAsync(files.ToArray(), ++_compressionOpenRequest);
    }

    private void StartToolPreparation()
    {
        if (_toolsCancellation is { IsCancellationRequested: false } && !ToolPreparation.IsFaulted) return;
        _toolsCancellation?.Dispose();
        _toolsCancellation = new();
        var token = _toolsCancellation.Token;
        ToolPreparation = Task.Run(async () => { await _tools.DetectAsync(token); }, token);
        // Observe background failures even if compression is never opened.
        _toolObservation = ObserveToolPreparationAsync(ToolPreparation);
    }

    private async Task ObserveToolPreparationAsync(Task preparation)
    {
        try { await preparation; }
        catch (OperationCanceledException) { }
        catch (Exception e) { _logger.LogWarning(e, "Compression tools could not be prepared in the background"); }
    }

    private async Task OpenCompressionAsync(IReadOnlyList<string> files, int request)
    {
        StartToolPreparation();
        IsPreparingCompression = !ToolPreparation.IsCompleted;
        try
        {
            await ToolPreparation;
            if (!_stopped && request == _compressionOpenRequest && IsCompressionDialogOpen && !Optimizer.IsBusy)
                Optimizer.Prepare(files.Count > 0 ? files : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!_stopped && request == _compressionOpenRequest)
            {
                IsCompressionDialogOpen = false;
                ShowError(e.Message);
            }
        }
        finally
        {
            if (request == _compressionOpenRequest) IsPreparingCompression = false;
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        try
        {
            var path = await DesktopActions.PickFolderAsync(TranslationService.Text("AddFolder"));
            if (path == null)
                return;
            await _db.AddFolderAsync(path, true);
            PageIndex = 1;
            await Library.LoadAsync();
            _monitor.Update(await _db.FoldersAsync());
            await CheckForLibraryChangesAsync();
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    public async Task CheckForLibraryChangesAsync()
    {
        if (_stopped)
            return;
        if (IsIndexing || Settings.IsModelBusy)
        {
            _rescanRequested = true;
            return;
        }

        var folders = await _db.FoldersAsync();
        if (folders.Count > 0)
            await RunScanAsync(folders, false);
    }

    [RelayCommand]
    private Task RescanAsync() => CheckForLibraryChangesAsync();
    [RelayCommand]
    private async Task ReindexAsync()
    {
        await CancelAndWaitAsync();
        if (!_stopped)
            await RunScanAsync(await _db.FoldersAsync(), true);
    }

    private async Task CancelAndWaitAsync()
    {
        ++_modelScanRequest;
        _rescanRequested = false;
        _indexControl?.Cancel();
        while (IsIndexing)
            await Task.Delay(30);
    }

    private async Task RunScanAsync(IReadOnlyList<LibraryFolder> folders, bool force)
    {
        if (_stopped)
            return;
        if (IsIndexing)
        {
            _rescanRequested = true;
            return;
        }

        IsIndexing = true;
        IndexProgress = 0;
        IsIndexProgressIndeterminate = true;
        using var control = new JobControl();
        _indexControl = control;
        var progress = new Progress<WorkProgress>(p =>
        {
            if (!IsIndexing || !ReferenceEquals(_indexControl, control))
                return;
            Status = TranslationService.Text("Indexing") + (p.Total > 0 ? $" {p.Completed}/{p.Total}" : "");
            IsIndexProgressIndeterminate = p.IsIndeterminate;
            if (!p.IsIndeterminate)
                IndexProgress = Math.Max(IndexProgress, Math.Clamp(p.Fraction * 100, 0, 100));
        });
        try
        {
            await _indexer.ScanAsync(folders, control, progress, force);
            IndexProgress = 100;
            Status = TranslationService.Text("IndexComplete");
        }
        catch (OperationCanceledException)
        {
            Status = TranslationService.Text("IndexInterrupted");
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
        finally
        {
            _indexControl = null;
            IsIndexProgressIndeterminate = false;
            IsPaused = false;
            await Library.LoadAsync(resetPage: false);
            await Settings.LoadAsync();
            _monitor.Update(await _db.FoldersAsync());
            IsIndexing = false;
            People.QueueIndex();
            if (_rescanRequested && !_stopped && !Settings.IsModelBusy)
            {
                _rescanRequested = false;
                Dispatcher.UIThread.Post(() => _ = CheckForLibraryChangesAsync());
            }
        }
    }

    [RelayCommand]
    private void PauseIndex()
    {
        if (_indexControl == null)
            return;
        if (IsPaused)
            _indexControl.Resume();
        else
            _indexControl.Pause();
        IsPaused = !IsPaused;
    }

    [RelayCommand]
    private void CancelIndex() => _indexControl?.Cancel();
    [RelayCommand]
    private async Task RemoveFolderAsync(LibraryFolder? folder)
    {
        if (folder == null)
            return;
        try
        {
            await CancelAndWaitAsync();
            await _db.RemoveFolderAsync(folder.Id);
            await Library.LoadAsync();
            _monitor.Update(await _db.FoldersAsync());
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    public async Task SetRecursiveAsync(LibraryFolder folder, bool recursive)
    {
        if (folder.Recursive == recursive)
            return;
        try
        {
            await CancelAndWaitAsync();
            await _db.SetFolderRecursiveAsync(folder.Id, recursive);
            await Library.LoadAsync();
            _monitor.Update(await _db.FoldersAsync());
            await CheckForLibraryChangesAsync();
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    public void Stop()
    {
        _toolsCancellation?.Cancel();
        _startupCancellation?.Cancel();
        IsPreparingLibrary = false;
        ++_modelScanRequest;
        _stopped = true;
        _refreshTimer.Stop();
        _indexControl?.Cancel();
        Optimizer.Stop();
        Settings.Stop();
        Viewer.Stop();
        People.Stop();
    }

    public async Task ShutdownAsync()
    {
        _toolsCancellation?.Cancel();
        _startupCancellation?.Cancel();
        IsPreparingLibrary = false;
        _stopped = true;
        _refreshTimer.Stop();
        Optimizer.Stop();
        Settings.Stop();
        People.Stop();
        await _toolObservation;
        await PendingCompressionOpen;
        await CancelAndWaitAsync();
        await Viewer.CloseCommand.ExecuteAsync(null);
        await Settings.PendingSave;
        await People.PendingIndex;
        await People.PendingReferences;
        if (Optimizer.ValidateCommand.ExecutionTask is { } encoding)
            await encoding;
        if (Settings.ManageModelCommand.ExecutionTask is { } modelDownload)
            await modelDownload;
        if (Settings.ActivateModelCommand.ExecutionTask is { } modelSwitch)
            await modelSwitch;
    }
}
