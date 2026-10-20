using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Localization;

namespace PixSeek.ViewModels;
public partial class LibraryViewModel(LibraryDatabase db, OnnxEmbeddingService embeddings, ModelManager models) : ObservableObject
{
    public ObservableCollection<MediaRow> Rows { get; } = [];
    public ObservableCollection<LibraryFolder> Folders { get; } = [];
    public ObservableCollection<LibraryFolder> FolderOptions { get; } = [];
    public Choice[] Types { get; } = [new("Tout", "AllMedia"), new("Photos", "Photos"), new("Vidéos", "Videos")];
    public Choice[] Sorts { get; } = [new("Date", "Date"), new("Nom", "Name"), new("Taille", "Size")];

    [ObservableProperty]
    private string _selectedType = "Tout";
    [ObservableProperty]
    private string _selectedSort = "Date";
    [ObservableProperty]
    private LibraryFolder? _selectedFolder;
    [ObservableProperty]
    private string _query = "";
    [ObservableProperty]
    private string _summary = "";
    [ObservableProperty]
    private bool _isLoading;
    [ObservableProperty]
    private bool _hasMore;
    [ObservableProperty]
    private bool _isLoadingMore;
    [ObservableProperty]
    private bool _isSearch;
    [ObservableProperty]
    private bool _isSearchBlocked;
    public Choice SelectedTypeChoice
    {
        get => Types.First(c => c.Id == SelectedType);
        set
        {
            if (value != null)
                SelectedType = value.Id;
        }
    }

    public Choice SelectedSortChoice
    {
        get => Sorts.First(c => c.Id == SelectedSort);
        set
        {
            if (value != null)
                SelectedSort = value.Id;
        }
    }

    private IReadOnlyList<MediaCardViewModel> _cards = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _columns = 4, _filterVersion, _loadedFilterVersion = -1;
    private bool _suspendFilters;
    private float[]? _queryVector;
    private string? _encodedQuery, _encodedVersion;
    public IReadOnlyList<MediaCardViewModel> Cards => _cards;
    public Action<MediaCardViewModel>? OpenMedia { get; set; }
    public Action<string>? Error { get; set; }
    public Action<IReadOnlyList<string>>? OptimizeFiles { get; set; }
    public Action? OpenSettings { get; set; }
    public Func<Task>? AddFolder { get; set; }
    public Task PendingFilterRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void Settings() => OpenSettings?.Invoke();
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (AddFolder != null)
            await AddFolder();
    }

    [RelayCommand]
    private void Compress() => OptimizeFiles?.Invoke(_cards.Where(c => c.IsSelected).Select(c => c.Media.FullPath).ToArray());
    public void CompressMedia(MediaCardViewModel card) => OptimizeFiles?.Invoke(card.IsSelected ? _cards.Where(c => c.IsSelected).Select(c => c.Media.FullPath).ToArray() : [card.Media.FullPath]);
    [RelayCommand]
    private void Open(MediaCardViewModel? card)
    {
        if (card != null)
            OpenMedia?.Invoke(card);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadResultsAsync(IsSearch);
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearchBlocked)
            return;
        ++_filterVersion;
        await LoadResultsAsync(!string.IsNullOrWhiteSpace(Query));
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _suspendFilters = true;
        SelectedFolder = FolderOptions.FirstOrDefault();
        SelectedType = "Tout";
        _suspendFilters = false;
        ++_filterVersion;
        await LoadResultsAsync(IsSearch);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsSearch || IsLoadingMore || IsLoading)
            return;
        await LoadResultsAsync(false, append: true);
    }

    partial void OnSelectedTypeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedTypeChoice));
        ScheduleFilterRefresh();
    }

    partial void OnSelectedSortChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedSortChoice));
        ScheduleFilterRefresh();
    }

    partial void OnSelectedFolderChanged(LibraryFolder? value) => ScheduleFilterRefresh();
    partial void OnQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) && IsSearch)
        {
            IsSearch = false;
            ScheduleFilterRefresh();
        }
    }

    private void ScheduleFilterRefresh()
    {
        if (!_suspendFilters)
            PendingFilterRefresh = ApplyFiltersAfterDelayAsync(++_filterVersion);
    }

    private async Task ApplyFiltersAfterDelayAsync(int version)
    {
        await Task.Delay(150);
        if (version == _filterVersion)
        {
            await LoadResultsAsync(IsSearch);
        }
    }

    public void InvalidateQuery()
    {
        _queryVector = null;
        _encodedVersion = null;
    }

    private long? FolderId => SelectedFolder?.Id > 0 ? SelectedFolder.Id : null;

    private async Task LoadResultsAsync(bool search, bool background = false, bool append = false)
    {
        if (search && IsSearchBlocked)
            return;
        if (background && _loadGate.CurrentCount == 0)
            return;
        await _loadGate.WaitAsync();
        if (append && _loadedFilterVersion != _filterVersion)
        {
            _loadGate.Release();
            return;
        }
        var version = _filterVersion;
        if (append)
            IsLoadingMore = true;
        else if (!background)
            IsLoading = true;
        try
        {
            IReadOnlyList<SearchResult> results;
            if (search && !string.IsNullOrWhiteSpace(Query))
            {
                var query = Query;
                var type = SelectedType;
                var folder = FolderId;
                var namesTask = Task.Run(() => db.FilenameCandidatesAsync(query, type, folder));
                IReadOnlyList<SearchResult> nearest = [];
                if (models.FilesPresent)
                {
                    if (_encodedQuery != query || _queryVector == null || _encodedVersion != models.Version)
                    {
                        _queryVector = await embeddings.GenerateTextEmbeddingAsync(query, default);
                        _encodedQuery = query;
                        _encodedVersion = models.Version;
                    }

                    nearest = await Task.Run(() => db.SearchFilteredAsync(_queryVector, 240, type, folder));
                }

                results = HybridRanking.Merge(query, nearest, await namesTask, 240);
                if (version != _filterVersion) return;
                HasMore = false;
                IsSearch = true;
                Summary = TranslationService.Text("SearchCount", results.Count, query);
            }
            else
            {
                var offset = append ? _cards.Count : 0;
                var limit = background ? Math.Max(240, _cards.Count) : 240;
                var type = SelectedType;
                var sort = SelectedSort;
                var folder = FolderId;
                var media = await Task.Run(() => db.MediaAsync(offset, limit + 1, type, sort, folder));
                if (version != _filterVersion) return;
                HasMore = media.Count > limit;
                IsSearch = false;
                var page = media.Take(limit).Select(m => new SearchResult(m, 0, null, m.ThumbnailPath));
                results = append ? _cards.Select(c => c.Result).Concat(page).DistinctBy(r => r.Media.Id).ToArray() : page.ToArray();
                Summary = results.Count == 0 ? TranslationService.Text("EmptyLibrary") : TranslationService.Text("LoadedMediaCount", results.Count);
            }

            UpdateCards(results);
            _loadedFilterVersion = version;
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
        finally
        {
            if (!background)
                IsLoading = false;
            IsLoadingMore = false;
            _loadGate.Release();
        }
    }

    private void UpdateCards(IReadOnlyList<SearchResult> results)
    {
        var existing = _cards.ToDictionary(c => c.Media.Id);
        _cards = results.Select(result =>
        {
            if (existing.TryGetValue(result.Media.Id, out var card))
            {
                card.Update(result);
                return card;
            }

            return new MediaCardViewModel(result);
        }).ToArray();
        ApplyRows();
    }

    public void Reflow(double width)
    {
        var count = Math.Clamp((int)(width / 210), 2, 7);
        if (count == _columns)
            return;
        _columns = count;
        ApplyRows();
    }

    private void ApplyRows()
    {
        var chunks = _cards.Chunk(_columns).ToArray();
        for (var row = 0; row < chunks.Length; row++)
        {
            if (row >= Rows.Count)
            {
                Rows.Add(new(chunks[row], _columns));
                continue;
            }

            Rows[row].Columns = _columns;
            var cards = Rows[row].Cards;
            for (var index = 0; index < chunks[row].Length; index++)
            {
                var wanted = chunks[row][index];
                if (index < cards.Count && ReferenceEquals(cards[index], wanted))
                    continue;
                var current = cards.IndexOf(wanted);
                if (current >= 0)
                    cards.Move(current, index);
                else
                    cards.Insert(index, wanted);
            }

            while (cards.Count > chunks[row].Length)
                cards.RemoveAt(cards.Count - 1);
        }

        while (Rows.Count > chunks.Length)
            Rows.RemoveAt(Rows.Count - 1);
    }

    public Task RefreshInBackgroundAsync() => LoadResultsAsync(IsSearch, background: true);
    public async Task LoadAsync(bool resetPage = true)
    {
        _suspendFilters = true;
        try
        {
            var selectedId = SelectedFolder?.Id ?? 0;
            var folders = await Task.Run(() => db.FoldersAsync());
            if (!Folders.SequenceEqual(folders))
            {
                Folders.Clear();
                foreach (var folder in folders)
                    Folders.Add(folder);
            }

            var allLabel = TranslationService.Text("AllFolders");
            if (FolderOptions.Count == 0 || FolderOptions[0].DisplayName != allLabel || !FolderOptions.Skip(1).SequenceEqual(folders))
            {
                FolderOptions.Clear();
                FolderOptions.Add(new(0, "", allLabel, true));
                foreach (var folder in folders)
                    FolderOptions.Add(folder);
                SelectedFolder = FolderOptions.FirstOrDefault(f => f.Id == selectedId) ?? FolderOptions[0];
            }
        }
        finally
        {
            _suspendFilters = false;
        }

        await LoadResultsAsync(IsSearch, background: !resetPage);
    }
}
