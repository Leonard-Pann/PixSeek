using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.Logging;
using PixSeek.Services;

namespace PixSeek.ViewModels;
public partial class MainViewModel : ViewModelBase
{
    private readonly LibraryDatabase _db;
    private readonly LibraryIndexer _indexer;
    private readonly NativeTools _tools;
    private readonly ModelManager _models;
    private readonly ILogger<MainViewModel> _logger;
    private JobControl? _indexControl;
    private readonly DispatcherTimer _refreshTimer;
    public LibraryViewModel Library { get; }
    public OptimizerViewModel Optimizer { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private int _pageIndex = 1;
    [ObservableProperty]
    private string _status = "Initialisation…";
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
    private bool _refreshingGallery;
    [ObservableProperty]
    private long _photos;
    [ObservableProperty]
    private long _videos;
    [ObservableProperty]
    private long _folderCount;
    [ObservableProperty]
    private long _indexed;
    [ObservableProperty]
    private long _pending;
    [ObservableProperty]
    private string _aiStatus = "IA à configurer";
    [ObservableProperty]
    private bool _sidebarExpanded = true;
    public bool IsHome => PageIndex == 0;
    public bool IsGallery => PageIndex is 1 or 2;
    public bool IsSearch => PageIndex == 2;
    public bool IsSettings => PageIndex == 4;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string PageTitle => PageIndex switch
    {
        1 or 2 => "",
        4 => "Paramètres",
        _ => "Bienvenue chez vous"
    };
    public double SidebarWidth => SidebarExpanded ? 220 : 80;

    public MainViewModel(LibraryDatabase db, LibraryIndexer indexer, NativeTools tools, ModelManager models, LibraryViewModel library, OptimizerViewModel optimizer, SettingsViewModel settings, ILogger<MainViewModel> logger)
    {
        _db = db;
        _indexer = indexer;
        _tools = tools;
        _models = models;
        _logger = logger;
        Library = library;
        Optimizer = optimizer;
        Settings = settings;
        Library.Error = ShowError;
        Optimizer.Error = ShowError;
        Settings.Error = ShowError;
        Library.OpenMedia = OpenMedia;
        Settings.AfterModelReady = () => RescanAsync();
        Library.OptimizeFiles = ShowCompression;
        Library.AddFolder = AddFolderAsync;
        Optimizer.RequestClose = () => IsCompressionDialogOpen = false;
        _refreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (IsIndexing && !_refreshingGallery)
            {
                _refreshingGallery = true;
                try
                {
                    await UpdateStatsAsync();
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
            }
        };
    }

    partial void OnPageIndexChanged(int value)
    {
        foreach (var p in new[]
        {
            nameof(IsHome),
            nameof(IsGallery),
            nameof(IsSearch),
            nameof(IsSettings),
            nameof(PageTitle)
        }

        )
            OnPropertyChanged(p);
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSidebarExpandedChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));
    public async Task InitializeAsync()
    {
        try
        {
            await _db.InitializeAsync();
            try
            {
                await _tools.DetectAsync();
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }

            if (_models.FilesPresent)
                try
                {
                    await _db.ConfigureVectorsAsync(_models.Version, _models.Manifest.Dimensions);
                }
                catch (Exception e)
                {
                    ShowError(e.Message);
                }

            Optimizer.LoadSettings();
            await Library.LoadAsync();
            await Settings.LoadAsync();
            await UpdateStatsAsync();
            Status = "Prêt · vos fichiers restent sur cet ordinateur";
            _refreshTimer.Start();
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            Status = "Initialisation incomplète · consultez les diagnostics";
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
        if (!int.TryParse(page, out var p))
            return;
        if (p == 3)
            ShowCompression([]);
        else
            PageIndex = p;
    }

    public void ShowCompression(IReadOnlyList<string> files)
    {
        if (Optimizer.IsBusy)
        {
            IsCompressionDialogOpen = true;
            return;
        }

        Optimizer.Prepare(files.Count > 0 ? files : null);
        IsCompressionDialogOpen = true;
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (IsIndexing)
            return;
        try
        {
            var folder = await DesktopActions.PickFolderAsync("Ajouter un dossier à PixSeek");
            if (folder == null)
                return;
            var added = await _db.AddFolderAsync(folder, true);
            PageIndex = 1;
            await Library.LoadAsync();
            await RunScanAsync([added], false);
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (!IsIndexing)
            await RunScanAsync(await _db.FoldersAsync(), false);
    }

    [RelayCommand]
    private async Task ReindexAsync()
    {
        if (!IsIndexing)
            await RunScanAsync(await _db.FoldersAsync(), true);
    }

    private async Task RunScanAsync(IReadOnlyList<LibraryFolder> folders, bool force)
    {
        IsIndexing = true;
        IndexProgress = 0;
        IsIndexProgressIndeterminate = true;
        _indexControl = new();
        var progress = new Progress<WorkProgress>(p =>
        {
            if (!IsIndexing)
                return;
            Status = p.Message;
            IsIndexProgressIndeterminate = p.IsIndeterminate;
            if (!p.IsIndeterminate)
                IndexProgress = Math.Max(IndexProgress, Math.Clamp(p.Fraction * 100, 0, 100));
        });
        try
        {
            await Task.Run(() => _indexer.ScanAsync(folders, _indexControl, progress, force));
        }
        catch (OperationCanceledException)
        {
            Status = "Indexation interrompue · Rescanner pour reprendre";
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
        finally
        {
            _indexControl.Dispose();
            _indexControl = null;
            IsIndexing = false;
            IsIndexProgressIndeterminate = false;
            IsPaused = false;
            await Library.LoadAsync(resetPage: false);
            await UpdateStatsAsync();
            await Settings.LoadAsync();
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
        Status = IsPaused ? "Pause après le média en cours" : "Reprise de l'indexation";
    }

    [RelayCommand]
    private void CancelIndex() => _indexControl?.Cancel();
    [RelayCommand]
    private async Task RemoveFolderAsync(LibraryFolder? folder)
    {
        if (folder == null || IsIndexing)
            return;
        try
        {
            await _db.RemoveFolderAsync(folder.Id);
            await Library.LoadAsync();
            await UpdateStatsAsync();
            Status = "Dossier retiré de la bibliothèque. Fichiers conservés.";
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    [RelayCommand]
    private async Task ToggleRecursiveAsync(LibraryFolder? folder)
    {
        if (folder == null || IsIndexing)
            return;
        try
        {
            await _db.SetFolderRecursiveAsync(folder.Id, !folder.Recursive);
            await Library.LoadAsync();
            Status = "Sous-dossiers : " + (!folder.Recursive ? "inclus" : "exclus") + ". Rescannez pour appliquer.";
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    private async Task UpdateStatsAsync()
    {
        var s = await _db.StatsAsync();
        Photos = s.Photos;
        Videos = s.Videos;
        FolderCount = s.Folders;
        Indexed = s.Indexed;
        Pending = s.Pending;
        AiStatus = _models.FilesPresent ? "Modèle local disponible · CPU" : "Téléchargez le modèle dans Paramètres";
    }

    private void OpenMedia(MediaCardViewModel card)
    {
        try
        {
            if (card.IsVideo)
            {
                DesktopActions.PlayVideo(card.Media, card.Timestamp, _tools);
                Status = "FFplay · Espace : pause · flèches : seek · 9/0 : volume · F : plein écran";
                return;
            }

            var viewer = new Views.MediaViewerWindow(Library.Cards, card, _tools, Optimizer, () => IsCompressionDialogOpen = true);
            viewer.Show(DesktopActions.MainWindow!);
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        _indexControl?.Cancel();
        Optimizer.Stop();
        Settings.Stop();
    }
}
