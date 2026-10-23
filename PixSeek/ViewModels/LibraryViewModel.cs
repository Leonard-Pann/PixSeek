using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Localization;

namespace PixSeek.ViewModels;
public partial class LibraryViewModel(LibraryDatabase db, OnnxEmbeddingService embeddings, ModelManager models, PeopleRepository people, FaceModelManager faceModels) : ObservableObject
{
    public ObservableCollection<MediaRow> Rows { get; } = [];
    public ObservableCollection<LibraryFolder> Folders { get; } = [];
    public ObservableCollection<LibraryFolder> FolderOptions { get; } = [];
    public ObservableCollection<PersonOption> PersonOptions { get; } = [];
    [ObservableProperty] private PersonOption? _selectedPerson;
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
    private readonly Dictionary<long, (string Path, bool IsVideo)> _selection = [];
    private int _selectionVersion;
    private bool _updatingSelection;
    public int SelectedCount => _selection.Count;
    public bool HasSelection => SelectedCount > 0;
    public string SelectionSummary => TranslationService.Text("SelectedMediaCount", SelectedCount);
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
    public Action<IReadOnlyList<string>>? AddPerson { get; set; }
    [RelayCommand] private void Person() => AddPerson?.Invoke(_selection.Values.Where(c => !c.IsVideo).Select(c => c.Path).ToArray());
    public Func<Task>? AddFolder { get; set; }
    public event Action? ScrollToTopRequested;
    public bool HasQuery => !string.IsNullOrEmpty(Query);
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
    private void Compress() => OptimizeFiles?.Invoke(_selection.Values.Select(c => c.Path).ToArray());
    public void CompressMedia(MediaCardViewModel card) => OptimizeFiles?.Invoke(card.IsSelected ? _selection.Values.Select(c => c.Path).ToArray() : [card.Media.FullPath]);

    [RelayCommand]
    private Task SelectAllAsync() => SetSelectionAsync(toggle: false);
    [RelayCommand]
    private Task ToggleAllAsync() => SetSelectionAsync(toggle: true);
    private async Task SetSelectionAsync(bool toggle)
    {
        var version = _filterVersion;
        var selectionVersion = ++_selectionVersion;
        try
        {
            var type = SelectedType; var folder = FolderId; var person = PersonId;
            var items = IsSearch
                ? _cards.Select(c => (c.Media.Id, Path: c.Media.FullPath, c.IsVideo)).ToArray()
                : await Task.Run(() => db.SelectionAsync(type, folder, person, faceModels.Version, FaceEmbeddingService.MatchThreshold));
            if (version != _filterVersion || selectionVersion != _selectionVersion) return;
            var clear = toggle && items.Count > 0 && items.All(item => _selection.ContainsKey(item.Id));
            _selection.Clear();
            if (!clear)
                foreach (var item in items) _selection[item.Id] = (item.Path, item.IsVideo);
            UpdateSelectedCards();
        }
        catch (Exception e) { Error?.Invoke(e.Message); }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        ++_selectionVersion;
        _selection.Clear();
        UpdateSelectedCards();
    }

    private void CardSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_updatingSelection || args.PropertyName != nameof(MediaCardViewModel.IsSelected) || sender is not MediaCardViewModel card) return;
        ++_selectionVersion;
        if (card.IsSelected) _selection[card.Media.Id] = (card.Media.FullPath, card.IsVideo);
        else _selection.Remove(card.Media.Id);
        NotifySelection();
    }

    private void UpdateSelectedCards()
    {
        _updatingSelection = true;
        try { foreach (var card in _cards) card.IsSelected = _selection.ContainsKey(card.Media.Id); }
        finally { _updatingSelection = false; }
        NotifySelection();
    }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }
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
        ScrollToTopRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ClearQueryAsync()
    {
        _suspendFilters = true;
        Query = "";
        IsSearch = false;
        _suspendFilters = false;
        ++_filterVersion;
        await LoadResultsAsync(false);
        ScrollToTopRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _suspendFilters = true;
        SelectedFolder = FolderOptions.FirstOrDefault();
        SelectedType = "Tout";
        SelectedPerson = PersonOptions.FirstOrDefault();
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
    partial void OnSelectedPersonChanged(PersonOption? value) => ScheduleFilterRefresh();
    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
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
    private long? PersonId => SelectedPerson?.Id > 0 ? SelectedPerson.Id : null;

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
                var person = PersonId;
                var namesTask = Task.Run(() => db.FilenameCandidatesAsync(query, type, folder, personId: person, faceVersion: faceModels.Version));
                IReadOnlyList<SearchResult> nearest = [];
                if (models.FilesPresent)
                {
                    if (_encodedQuery != query || _queryVector == null || _encodedVersion != models.Version)
                    {
                        _queryVector = await embeddings.GenerateTextEmbeddingAsync(query, default);
                        _encodedQuery = query;
                        _encodedVersion = models.Version;
                    }

                    nearest = await Task.Run(() => db.SearchFilteredAsync(_queryVector, 240, type, folder, personId: person, faceVersion: faceModels.Version));
                }

                var hybrid = HybridRanking.Merge(query, nearest, await namesTask, 240);
                var personMatches = await Task.Run(() => people.SearchAsync(query, type, folder, faceModels.Version, FaceEmbeddingService.MatchThreshold, personId: person));
                results = personMatches.Select(p => new SearchResult(p.Media, p.Similarity, null, p.Media.ThumbnailPath))
                    .Concat(hybrid).DistinctBy(p => p.Media.Id).Take(240).ToArray();
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
                var person = PersonId;
                var media = await Task.Run(() => db.MediaAsync(offset, limit + 1, type, sort, folder, personId: person, faceVersion: faceModels.Version));
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

            var created = new MediaCardViewModel(result) { IsSelected = _selection.ContainsKey(result.Media.Id) };
            created.PropertyChanged += CardSelectionChanged;
            return created;
        }).ToArray();
        var retained = _cards.ToHashSet();
        foreach (var removed in existing.Values.Where(c => !retained.Contains(c))) removed.PropertyChanged -= CardSelectionChanged;
        UpdateSelectedCards();
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
    public async Task RefreshPeopleAsync()
    {
        var selectedId = SelectedPerson?.Id ?? 0;
        var profiles = await Task.Run(() => people.ProfilesAsync());
        var options = new[] { new PersonOption(0, TranslationService.Text("AllPeople")) }
            .Concat(profiles.Select(p => new PersonOption(p.Id, string.Join(", ", p.Keywords)))).ToArray();
        if (PersonOptions.SequenceEqual(options)) return;
        var suspended = _suspendFilters;
        _suspendFilters = true;
        try
        {
            PersonOptions.Clear();
            foreach (var option in options) PersonOptions.Add(option);
            SelectedPerson = PersonOptions.FirstOrDefault(p => p.Id == selectedId) ?? PersonOptions[0];
            if ((SelectedPerson?.Id ?? 0) != selectedId) ++_filterVersion;
        }
        finally { _suspendFilters = suspended; }
    }
    public async Task LoadAsync(bool resetPage = true)
    {
        _suspendFilters = true;
        try
        {
            await RefreshPeopleAsync();
            if (_selection.Count > 0)
            {
                var current = await Task.Run(() => db.SelectionAsync("Tout", null, null, faceModels.Version, FaceEmbeddingService.MatchThreshold));
                var present = current.Select(m => m.Id).ToHashSet();
                foreach (var removed in _selection.Keys.Where(id => !present.Contains(id)).ToArray()) _selection.Remove(removed);
            }
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
