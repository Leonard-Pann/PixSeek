using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;

namespace PixSeek.ViewModels;
public partial class LibraryViewModel(LibraryDatabase db, OnnxEmbeddingService embeddings) : ObservableObject
{
    public ObservableCollection<MediaRow> Rows { get; } = [];
    public ObservableCollection<LibraryFolder> Folders { get; } = [];
    public string[] Types { get; } = ["Tout", "Photos", "Vidéos"];
    public string[] Sorts { get; } = ["Date", "Nom", "Taille"];

    [ObservableProperty]
    private string _selectedType = "Tout";
    [ObservableProperty]
    private string _selectedSort = "Date";
    [ObservableProperty]
    private LibraryFolder? _selectedFolder;
    [ObservableProperty]
    private string _query = "";
    [ObservableProperty]
    private string _summary = "Ajoutez un dossier pour commencer.";
    [ObservableProperty]
    private bool _isLoading;
    [ObservableProperty]
    private bool _hasMore;
    [ObservableProperty]
    private int _page;
    [ObservableProperty]
    private DateTime? _fromDate;
    [ObservableProperty]
    private DateTime? _toDate;
    [ObservableProperty]
    private bool _isSearch;
    private IReadOnlyList<MediaCardViewModel> _cards = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _columns = 4, _filterVersion;
    private bool _suspendFilters;
    private float[]? _queryVector;
    private string? _encodedQuery;
    public IReadOnlyList<MediaCardViewModel> Cards => _cards;
    public Action<MediaCardViewModel>? OpenMedia { get; set; }
    public Action<string>? Error { get; set; }
    public Action<IReadOnlyList<string>>? OptimizeFiles { get; set; }
    public Func<Task>? AddFolder { get; set; }
    public Task PendingFilterRefresh { get; private set; } = Task.CompletedTask;

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
    private async Task RefreshAsync() => await LoadResultsAsync(IsSearch);
    [RelayCommand]
    private async Task SearchAsync()
    {
        Page = 0;
        await LoadResultsAsync(!string.IsNullOrWhiteSpace(Query));
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _suspendFilters = true;
        try
        {
            SelectedFolder = null;
            SelectedType = "Tout";
            FromDate = null;
            ToDate = null;
        }
        finally
        {
            _suspendFilters = false;
        }

        Page = 0;
        await LoadResultsAsync(IsSearch);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (HasMore)
        {
            Page++;
            await LoadResultsAsync(IsSearch);
        }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (Page > 0)
        {
            Page--;
            await LoadResultsAsync(IsSearch);
        }
    }

    partial void OnSelectedTypeChanged(string value) => ScheduleFilterRefresh();
    partial void OnSelectedSortChanged(string value) => ScheduleFilterRefresh();
    partial void OnSelectedFolderChanged(LibraryFolder? value) => ScheduleFilterRefresh();
    partial void OnFromDateChanged(DateTime? value) => ScheduleFilterRefresh();
    partial void OnToDateChanged(DateTime? value) => ScheduleFilterRefresh();
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
        if (_suspendFilters)
            return;
        var version = ++_filterVersion;
        PendingFilterRefresh = ApplyFiltersAfterDelayAsync(version);
    }

    private async Task ApplyFiltersAfterDelayAsync(int version)
    {
        await Task.Delay(150);
        if (version != _filterVersion)
            return;
        Page = 0;
        await LoadResultsAsync(IsSearch);
    }

    private static DateTime? Boundary(DateTime? date, bool nextDay = false) => date.HasValue ? DateTime.SpecifyKind(date.Value.Date.AddDays(nextDay ? 1 : 0), DateTimeKind.Local).ToUniversalTime() : null;
    private async Task LoadResultsAsync(bool search, bool background = false)
    {
        if (background && _loadGate.CurrentCount == 0)
            return;
        await _loadGate.WaitAsync();
        var showLoading = !background;
        if (showLoading)
            IsLoading = true;
        try
        {
            var from = Boundary(FromDate);
            var to = Boundary(ToDate, true);
            if (from.HasValue && to.HasValue && from >= to)
                throw new ArgumentException("La date de début doit précéder la date de fin.");
            IReadOnlyList<SearchResult> results;
            if (search && !string.IsNullOrWhiteSpace(Query))
            {
                if (_encodedQuery != Query || _queryVector == null)
                {
                    _queryVector = await embeddings.GenerateTextEmbeddingAsync(Query, default);
                    _encodedQuery = Query;
                }

                var nearest = await db.SearchAsync(_queryVector, 240, default);
                results = nearest.Where(r => (SelectedType == "Tout" || r.Media.MediaType == (SelectedType == "Photos" ? MediaType.Image : MediaType.Video)) && (SelectedFolder == null || r.Media.LibraryFolderId == SelectedFolder.Id) && (!from.HasValue || r.Media.LastModifiedAt.ToUniversalTime() >= from) && (!to.HasValue || r.Media.LastModifiedAt.ToUniversalTime() < to)).ToArray();
                HasMore = false;
                IsSearch = true;
                Summary = $"{results.Count} résultats pour « {Query} »";
            }
            else
            {
                var media = await db.MediaAsync(Page * 240, 241, SelectedType, SelectedSort, SelectedFolder?.Id, fromDate: from, toDateExclusive: to);
                HasMore = media.Count > 240;
                IsSearch = false;
                results = media.Take(240).Select(m => new SearchResult(m, 0, null, m.ThumbnailPath)).ToArray();
                Summary = results.Count == 0 ? "Aucun média pour ces critères. Ajoutez un dossier pour commencer." : $"{results.Count} médias · page {Page + 1}";
            }

            UpdateCards(results);
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
        finally
        {
            if (showLoading)
                IsLoading = false;
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
                Rows.Add(new(chunks[row]));
                continue;
            }

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
            var selectedId = SelectedFolder?.Id;
            var folders = await db.FoldersAsync();
            if (!Folders.SequenceEqual(folders))
            {
                Folders.Clear();
                foreach (var folder in folders)
                    Folders.Add(folder);
                SelectedFolder = Folders.FirstOrDefault(f => f.Id == selectedId);
            }
        }
        finally
        {
            _suspendFilters = false;
        }

        if (resetPage)
            Page = 0;
        await LoadResultsAsync(IsSearch);
    }
}
