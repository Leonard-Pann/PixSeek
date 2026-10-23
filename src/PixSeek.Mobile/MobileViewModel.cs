using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;

namespace PixSeek.Mobile;

public sealed record MobileOption(long? Id, string Label);
public sealed class MobileRow(IReadOnlyList<MobileCell> cells) { public IReadOnlyList<MobileCell> Cells => cells; }
public partial class MobileCell(MediaFile media, double width, Action<MobileCell> open, Action<MobileCell> select) : ObservableObject
{
    public MediaFile Media { get; set; } = media;
    public double Width => width;
    public double Height => width;
    public bool IsVideo => Media.MediaType == MediaType.Video;
    public string Duration => TimeSpan.FromMilliseconds(Media.DurationMilliseconds).ToString(@"m\:ss");
    [ObservableProperty] private string _thumbnail = media.ThumbnailPath;
    [ObservableProperty] private bool _selected;
    public Action<MobileCell> Open { get; } = open;
    public Action<MobileCell> Select { get; } = select;
}
public partial class MobileFace(DetectedFace face) : ObservableObject
{
    public DetectedFace Face => face;
    public string Thumbnail => face.ThumbnailPath;
    [ObservableProperty] private bool _selected;
}

public partial class MobileViewModel : ObservableObject, IDisposable
{
    public IMobilePlatform Platform { get; }
    public LibraryDatabase Database { get; }
    public PeopleRepository People { get; }
    public ModelManager Models { get; }
    public MobileEmbeddingService Embeddings { get; }
    public FaceModelManager FaceModels { get; }
    public FaceEmbeddingService FaceEngine { get; }
    public MobileLibrary Library { get; }
    public SettingsStore Settings { get; }
    public MobileStrings Text { get; }
    public ObservableCollection<MobileRow> Rows { get; } = [];
    public ObservableCollection<MobileOption> Folders { get; } = [];
    public ObservableCollection<MobileOption> Persons { get; } = [];
    public ObservableCollection<LibraryFolder> LibraryFolders { get; } = [];
    public ObservableCollection<DeviceAlbum> Albums { get; } = [];
    public ObservableCollection<MobileFace> Faces { get; } = [];
    public ObservableCollection<MobileOption> Profiles { get; } = [];
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _counter = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private bool _isSelecting;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _mediaTypeIndex;
    [ObservableProperty] private MobileOption? _folder;
    [ObservableProperty] private MobileOption? _person;
    [ObservableProperty] private string _panel = "";
    [ObservableProperty] private bool _viewerOpen;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private Control? _videoPlayer;
    [ObservableProperty] private string _viewerName = "";
    [ObservableProperty] private string _personKeywords = "";
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _frameInterval;
    [ObservableProperty] private int _maxFrames;
    public bool AddOpen => Panel == "add";
    public bool SettingsOpen => Panel == "settings";
    public bool PersonOpen => Panel == "person";
    public bool HasPanel => Panel.Length != 0;
    public string SelectionLabel => SelectedCount + " " + Text["Selected"];
    public bool HasError => Error.Length != 0;
    public bool HasQuery => Query.Length != 0;
    public string Version => "PixSeek 0.3.2 · Android";
    private readonly HashSet<long> _selection = [];
    private List<SearchResult> _visible = [];
    private int _limit = 120, _generation, _viewerGeneration, _viewerIndex, _columns = 3;
    private double _cellWidth = 112;
    private CancellationTokenSource? _search, _viewer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ready, _refreshQueued, _savingEnabled, _loadMore;
    private DateTime _lastRefresh;
    public event Action? ScrollToTop;
    public MobileViewModel(IMobilePlatform platform)
    {
        Platform = platform;
        Database = new(platform.Paths);
        People = new(Database);
        Models = new(platform.Paths);
        Embeddings = new(Models, platform);
        FaceModels = new(platform.Paths);
        FaceEngine = new(FaceModels, platform, platform.Paths);
        Library = new(platform, Database, People, Models, Embeddings, FaceEngine);
        Settings = new(platform.Paths);
        Text = new(Settings.Current.Language);
        ThemeIndex = Settings.Current.Theme == "Sombre" ? 1 : Settings.Current.Theme == "Clair" ? 2 : 0;
        LanguageIndex = Math.Max(0, Array.IndexOf(new[] { "en", "fr", "it", "de", "es" }, Text.Language));
        FrameInterval = Settings.Current.FrameIntervalSeconds;
        MaxFrames = Math.Min(24, Settings.Current.MaxVideoFrames);
        Library.FrameInterval = FrameInterval; Library.MaxFrames = MaxFrames;
        ApplyTheme();
        Library.Progress += p => Dispatcher.UIThread.Post(() =>
        {
            IsIndexing = p.Total > 0 && p.Completed < p.Total;
            Status = p.Message; Counter = $"{p.Completed} / {p.Total}"; Progress = p.Fraction * 100;
            Platform.KeepScreenOn(IsIndexing);
        });
        Library.Error += message => Dispatcher.UIThread.Post(() => Error = message);
        Library.Changed += () => Dispatcher.UIThread.Post(QueueRefresh);
        _savingEnabled = true;
    }
    public async Task InitializeAsync()
    {
        try
        {
            IsLoading = true;
            await Task.Run(async () =>
            {
                await Database.InitializeAsync(_lifetime.Token);
                await Database.ConfigureVectorsAsync(Models.Version, Models.Manifest.Dimensions, _lifetime.Token);
                await People.InitializeAsync(_lifetime.Token);
            });
            _ready = true;
            await RefreshOptionsAsync();
            await RefreshAsync();
            Library.Start();
        }
        catch (Exception e) { Error = e.Message; }
        finally { IsLoading = false; }
    }
    partial void OnPanelChanged(string value)
    {
        OnPropertyChanged(nameof(AddOpen)); OnPropertyChanged(nameof(SettingsOpen)); OnPropertyChanged(nameof(PersonOpen)); OnPropertyChanged(nameof(HasPanel));
        Platform.HideKeyboard();
    }
    partial void OnMediaTypeIndexChanged(int value) => ResetResults();
    partial void OnFolderChanged(MobileOption? value) => ResetResults();
    partial void OnPersonChanged(MobileOption? value) => ResetResults();
    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        _search?.Cancel(); _search?.Dispose(); _search = new();
        _ = DebounceAsync(_search.Token);
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    private async Task DebounceAsync(CancellationToken ct)
    {
        try { await Task.Delay(400, ct); _limit = 120; ScrollToTop?.Invoke(); await RefreshAsync(ct); }
        catch (OperationCanceledException) { }
    }
    private void ResetResults() { if (!_ready) return; _limit = 120; ScrollToTop?.Invoke(); QueueRefresh(); }
    public void UpdateWidth(double width)
    {
        if (width < 100) return;
        var columns = width > 840 ? 6 : width > 600 ? 5 : 3;
        var cellWidth = Math.Floor((width - 8 - (columns - 1) * 3) / columns);
        if (columns == _columns && Math.Abs(cellWidth - _cellWidth) < 1) return;
        _columns = columns; _cellWidth = cellWidth; RebuildRows(force: true);
    }
    public void QueueRefresh()
    {
        if (!_ready || _refreshQueued) return;
        _refreshQueued = true;
        _ = RefreshLaterAsync();
    }
    private async Task RefreshLaterAsync()
    {
        try
        {
            var delay = 800 - (DateTime.UtcNow - _lastRefresh).TotalMilliseconds;
            if (delay > 0) await Task.Delay((int)delay, _lifetime.Token);
            _refreshQueued = false;
            await RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
    }
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_ready) return;
        var generation = ++_generation;
        var query = Query.Trim(); var type = MediaTypeIndex == 1 ? "Photos" : MediaTypeIndex == 2 ? "Vidéos" : "Tout";
        var folder = Folder?.Id; var person = Person?.Id;
        try
        {
            var results = await Task.Run(async () =>
            {
                if (query.Length == 0)
                    return (await Database.MediaAsync(0, _limit, type, folder: folder, ct: ct, personId: person, faceVersion: FaceEngine.Version))
                        .Select(m => new SearchResult(m, 0, null, m.ThumbnailPath)).ToList();
                var semantic = await Database.SearchFilteredAsync(await Embeddings.GenerateTextEmbeddingAsync(query, ct), 240, type, folder, ct, person, FaceEngine.Version);
                var names = await Database.FilenameCandidatesAsync(query, type, folder, ct, person, FaceEngine.Version);
                var matches = await People.SearchAsync(query, type, folder, FaceEngine.Version, .45, ct, person);
                return matches.Select(m => new SearchResult(m.Media, m.Similarity, null, m.Media.ThumbnailPath))
                    .Concat(HybridRanking.Merge(query, semantic, names, 240)).DistinctBy(r => r.Media.Id).ToList();
            }, ct);
            if (generation != _generation) return;
            _visible = results; HasMedia = results.Count != 0;
            RebuildRows();
            _lastRefresh = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == _generation) Error = e.Message; }
    }
    private void RebuildRows(bool force = false)
    {
        var cells = Rows.SelectMany(r => r.Cells).ToArray();
        if (!force && cells.Select(c => c.Media.Id).SequenceEqual(_visible.Select(r => r.Media.Id)))
        {
            for (var i = 0; i < cells.Length; i++) { cells[i].Media = _visible[i].Media; cells[i].Thumbnail = _visible[i].ThumbnailPath; }
            return;
        }
        Rows.Clear();
        foreach (var group in _visible.Chunk(_columns))
            Rows.Add(new(group.Select(r => new MobileCell(r.Media, _cellWidth, OpenCell, SelectCell) { Selected = _selection.Contains(r.Media.Id), Thumbnail = r.ThumbnailPath }).ToArray()));
    }
    public async Task LoadMoreAsync()
    {
        if (_loadMore || Query.Length != 0 || _visible.Count < _limit) return;
        _loadMore = true;
        try { _limit += 120; await RefreshAsync(); }
        finally { _loadMore = false; }
    }
    public void SelectCell(MobileCell cell)
    {
        if (!_selection.Add(cell.Media.Id)) _selection.Remove(cell.Media.Id);
        cell.Selected = _selection.Contains(cell.Media.Id);
        IsSelecting = true; SelectedCount = _selection.Count; OnPropertyChanged(nameof(SelectionLabel));
    }
    public void OpenCell(MobileCell cell)
    {
        if (IsSelecting) { SelectCell(cell); return; }
        _viewerIndex = _visible.FindIndex(r => r.Media.Id == cell.Media.Id);
        _ = OpenViewerAsync();
    }
    private async Task OpenViewerAsync()
    {
        if (_viewerIndex < 0 || _viewerIndex >= _visible.Count) return;
        var generation = ++_viewerGeneration;
        _viewer?.Cancel(); _viewer?.Dispose(); _viewer = new();
        var ct = _viewer.Token;
        Platform.StopVideo(); VideoPlayer = null;
        var previous = Preview; Preview = null; previous?.Dispose();
        ViewerOpen = true;
        var result = _visible[_viewerIndex]; ViewerName = result.Media.FileName;
        try
        {
            if (result.Media.MediaType == MediaType.Video)
                VideoPlayer = Platform.CreateVideoPlayer(result.Media, result.TimestampMilliseconds ?? 0, error => Error = error);
            else
            {
                var bitmap = await Platform.PreviewAsync(result.Media, ct);
                if (generation != _viewerGeneration || !ViewerOpen) bitmap.Dispose();
                else Preview = bitmap;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == _viewerGeneration) Error = e.Message; }
    }
    [RelayCommand] private void Next() { if (_viewerIndex < _visible.Count - 1) { _viewerIndex++; _ = OpenViewerAsync(); } }
    [RelayCommand] private void Previous() { if (_viewerIndex > 0) { _viewerIndex--; _ = OpenViewerAsync(); } }
    [RelayCommand] private void CloseViewer()
    {
        ++_viewerGeneration; _viewer?.Cancel(); Platform.StopVideo(); VideoPlayer = null; ViewerOpen = false;
        var old = Preview; Preview = null; old?.Dispose();
    }
    [RelayCommand] private void Share() { if (ViewerOpen) Platform.Share(_visible[_viewerIndex].Media); }
    [RelayCommand] private void ClearSearch() => Query = "";
    [RelayCommand] private void ClearSelection()
    {
        _selection.Clear(); SelectedCount = 0; IsSelecting = false; foreach (var c in Rows.SelectMany(r => r.Cells)) c.Selected = false;
        OnPropertyChanged(nameof(SelectionLabel));
    }
    [RelayCommand] private void OpenAdd() { Error = ""; Albums.Clear(); Panel = "add"; }
    [RelayCommand] private async Task OpenSettingsAsync() { await RefreshOptionsAsync(); Panel = "settings"; }
    [RelayCommand] private void ClosePanel() { Panel = ""; }
    [RelayCommand] private void Pause() => Library.TogglePause();
    [RelayCommand] private void DismissError() => Error = "";
    [RelayCommand] private async Task AddGalleryAsync() => await GuardAsync(async () =>
    {
        await Platform.AlbumsAsync(_lifetime.Token);
        await Library.AddAsync(new("pixseek://gallery", Text["DeviceGallery"], "gallery"), _lifetime.Token);
        Panel = ""; await RefreshOptionsAsync();
    });
    [RelayCommand] private async Task ShowAlbumsAsync() => await GuardAsync(async () =>
    {
        var albums = await Platform.AlbumsAsync(_lifetime.Token); Albums.Clear(); foreach (var album in albums) Albums.Add(album);
    });
    [RelayCommand] private async Task AddAlbumAsync(DeviceAlbum album) => await GuardAsync(async () =>
    {
        await Library.AddAsync(new("pixseek://album/" + album.Id, album.Name, "album", album.Id), _lifetime.Token);
        Panel = ""; await RefreshOptionsAsync();
    });
    [RelayCommand] private async Task BrowseAsync() => await GuardAsync(async () =>
    {
        var source = await Platform.PickTreeAsync(_lifetime.Token);
        if (source != null) { await Library.AddAsync(source, _lifetime.Token); Panel = ""; await RefreshOptionsAsync(); }
    });
    [RelayCommand] private async Task PickMediaAsync() => await GuardAsync(async () =>
    {
        var items = await Platform.PickPhotosAsync(false, _lifetime.Token);
        if (items.Count > 0) { await Library.AddAsync(new("pixseek://selection", Text["SelectedMedia"], "selection", Items: items), _lifetime.Token); Panel = ""; await RefreshOptionsAsync(); }
    });
    [RelayCommand] private async Task RemoveFolderAsync(LibraryFolder folder) => await GuardAsync(async () =>
    {
        await Library.RemoveAsync(folder, _lifetime.Token); await RefreshOptionsAsync(); await RefreshAsync();
    });
    [RelayCommand] private async Task ReindexAsync() => await GuardAsync(async () =>
    {
        await Task.Run(async () =>
        {
            await using var c = await Database.OpenAsync(_lifetime.Token);
            await LibraryDatabase.ExecuteAsync(c, "UPDATE MediaFile SET IndexedVersion=NULL; DELETE FROM FaceScan;", _lifetime.Token);
        });
        Library.QueueScan(); Panel = "";
    });
    [RelayCommand] private async Task ClearCacheAsync() => await GuardAsync(async () =>
    {
        // Only generated JPEG previews, never models, source URIs or database files.
        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(Platform.Paths.Cache, "*.jpg"))
                try { File.Delete(file); } catch (IOException) { }
        });
        Library.QueueScan(); Status = Text["CacheCleared"];
    });
    [RelayCommand] private async Task OpenPersonAsync()
    {
        PersonKeywords = ""; Faces.Clear(); Error = ""; Panel = "person";
        var selected = _visible.Where(r => _selection.Contains(r.Media.Id) && r.Media.MediaType == MediaType.Image).Take(20).Select(r => r.Media.FullPath).ToArray();
        if (selected.Length > 0) await DetectAsync(selected);
    }
    [RelayCommand] private async Task PickReferencesAsync() => await GuardAsync(async () =>
    {
        var selected = await Platform.PickPhotosAsync(true, _lifetime.Token);
        if (selected.Count > 0) await DetectAsync(selected.Take(20).ToArray());
    });
    private async Task DetectAsync(IReadOnlyList<string> uris)
    {
        IsDetecting = true; Faces.Clear();
        try
        {
            foreach (var uri in uris)
            {
                var detected = await FaceEngine.DetectAsync(uri, _lifetime.Token);
                foreach (var face in detected) Faces.Add(new(face) { Selected = detected.Count == 1 });
            }
            if (Faces.Count == 0) Error = Text["NoFacesFound"];
        }
        catch (Exception e) { Error = e.Message; }
        finally { IsDetecting = false; }
    }
    [RelayCommand] private async Task AddPersonAsync() => await GuardAsync(async () =>
    {
        var selected = Faces.Where(f => f.Selected).Select(f => f.Face).ToArray();
        var keywords = PersonKeywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (selected.Length == 0 || keywords.Length == 0) { Error = Text["ChooseFaceAndName"]; return; }
        if (selected.GroupBy(f => f.SourcePath).Any(g => g.Count() > 1)) { Error = Text["OneFacePerPhoto"]; return; }
        await Task.Run(() => People.AddAsync(keywords, selected, FaceEngine.Version, _lifetime.Token));
        Panel = ""; ClearSelection(); await RefreshOptionsAsync(); await RefreshAsync();
    });
    [RelayCommand] private async Task RemovePersonAsync(MobileOption person) => await GuardAsync(async () =>
    {
        if (person.Id.HasValue) await People.DeleteAsync(person.Id.Value, _lifetime.Token);
        Person = null; await RefreshOptionsAsync(); await RefreshAsync();
    });
    private async Task RefreshOptionsAsync()
    {
        var folders = await Database.FoldersAsync(); var profiles = await People.ProfilesAsync();
        var selectedFolder = Folder?.Id; var selectedPerson = Person?.Id;
        Folders.Clear(); Folders.Add(new(null, Text["AllFolders"]));
        LibraryFolders.Clear();
        foreach (var folder in folders) { Folders.Add(new(folder.Id, folder.DisplayName)); LibraryFolders.Add(folder); }
        Folder = Folders.FirstOrDefault(f => f.Id == selectedFolder) ?? Folders[0];
        Persons.Clear(); Persons.Add(new(null, Text["AllPeople"])); Profiles.Clear();
        foreach (var profile in profiles)
        {
            var option = new MobileOption(profile.Id, string.Join(", ", profile.Keywords)); Persons.Add(option); Profiles.Add(option);
        }
        Person = Persons.FirstOrDefault(p => p.Id == selectedPerson) ?? Persons[0];
    }
    private async Task GuardAsync(Func<Task> work)
    {
        try { Error = ""; await work(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = e.Message; }
    }
    partial void OnThemeIndexChanged(int value) { ApplyTheme(); SavePreferences(); }
    partial void OnLanguageIndexChanged(int value)
    {
        if (!_savingEnabled) return;
        Text.SetLanguage(new[] { "en", "fr", "it", "de", "es" }[Math.Clamp(value, 0, 4)]);
        SavePreferences(); _ = RefreshOptionsAsync();
    }
    partial void OnFrameIntervalChanged(int value) { if (Library != null) Library.FrameInterval = Math.Clamp(value, 1, 600); SavePreferences(); }
    partial void OnMaxFramesChanged(int value) { if (Library != null) Library.MaxFrames = Math.Clamp(value, 1, 120); SavePreferences(); }
    private void ApplyTheme()
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = ThemeIndex == 1 ? ThemeVariant.Dark : ThemeIndex == 2 ? ThemeVariant.Light : ThemeVariant.Default;
    }
    private void SavePreferences()
    {
        if (!_savingEnabled) return;
        _ = GuardAsync(() => Settings.UpdateAsync(s => s with { Theme = ThemeIndex == 1 ? "Sombre" : ThemeIndex == 2 ? "Clair" : "Système",
            Language = Text.Language, FrameIntervalSeconds = Math.Clamp(FrameInterval, 1, 600), MaxVideoFrames = Math.Clamp(MaxFrames, 1, 120) }));
    }
    public bool Back()
    {
        if (ViewerOpen) { CloseViewer(); return true; }
        if (HasPanel) { ClosePanel(); return true; }
        if (IsSelecting) { ClearSelection(); return true; }
        return false;
    }
    public void Suspend() { Platform.StopVideo(); Library.Stop(); Platform.KeepScreenOn(false); }
    public async Task ResumeAsync()
    {
        if (!_ready) return;
        await Library.Completion;
        Library.Start();
    }
    public void Dispose() { _lifetime.Cancel(); Library.Stop(); _viewer?.Cancel(); Platform.StopVideo(); Preview?.Dispose(); }
}
