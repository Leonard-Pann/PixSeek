using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Localization;
using PixSeek.Services;

namespace PixSeek.ViewModels;

public partial class FaceCandidateViewModel(DetectedFace face) : ObservableObject
{
    public DetectedFace Face => face;
    public string ThumbnailPath => face.ThumbnailPath;
    public string FileName => Path.GetFileName(face.SourcePath);
    [ObservableProperty] private bool _isSelected;
}
public sealed record PersonOption(long Id, string Label);

public partial class PeopleViewModel(PeopleRepository people, FaceModelManager models, FaceEmbeddingService faces, FaceLibraryIndexer indexer) : ObservableObject
{
    public ObservableCollection<FaceCandidateViewModel> Candidates { get; } = [];
    public ObservableCollection<PersonOption> Profiles { get; } = [];
    [ObservableProperty] private string _keywords = "";
    [ObservableProperty] private string _inputSummary = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _indexStatus = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isIndexing;
    public bool CanAdd => !IsBusy && Candidates.Any(c => c.IsSelected) && PeopleRepository.Normalize(Keywords).Length > 0;
    public Action? RequestClose { get; set; }
    public Action<string>? Error { get; set; }
    public Func<Task>? IndexUpdated { get; set; }
    public Task PendingReferences { get; private set; } = Task.CompletedTask;
    public Task PendingIndex { get; private set; } = Task.CompletedTask;
    private CancellationTokenSource? _form, _index;
    private bool _stopped, _queued, _rerun;
    private int _referenceGeneration;
    partial void OnKeywordsChanged(string value) => OnPropertyChanged(nameof(CanAdd));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAdd));

    public void Prepare(IReadOnlyList<string> selected)
    {
        _form?.Cancel(); _form?.Dispose(); _form = new();
        Candidates.Clear(); Keywords = ""; InputSummary = ""; Message = ""; Progress = 0;
        PendingReferences = LoadReferencesAsync(selected, _form.Token, replace: true);
    }
    public async Task LoadReferencesAsync(IReadOnlyList<string> paths, CancellationToken ct, bool replace = true)
    {
        var generation = ++_referenceGeneration;
        IsBusy = true;
        try
        {
            await RefreshProfilesAsync();
            if (generation != _referenceGeneration) return;
            if (replace) Candidates.Clear();
            if (paths.Count == 0) { InputSummary = TranslationService.Text("ChooseReferencePhotos"); return; }
            if (paths.Count > 20) { Message = TranslationService.Text("ReferenceLimit"); return; }
            Message = TranslationService.Text("FaceModelLoading");
            var downloadProgress = new Progress<WorkProgress>(p => { if (generation == _referenceGeneration) Progress = p.Fraction * 100; });
            await Task.Run(() => models.EnsureAsync(downloadProgress, ct), ct);
            for (var i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Message = TranslationService.Text("DetectingFaces", Path.GetFileName(paths[i]));
                var found = await faces.DetectAsync(paths[i], ct);
                if (generation != _referenceGeneration) return;
                foreach (var face in found)
                {
                    var candidate = new FaceCandidateViewModel(face) { IsSelected = found.Count == 1 };
                    candidate.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName != nameof(FaceCandidateViewModel.IsSelected)) return;
                        if (candidate.IsSelected)
                            foreach (var other in Candidates.Where(c => !ReferenceEquals(c, candidate) && c.Face.SourcePath == candidate.Face.SourcePath)) other.IsSelected = false;
                        OnPropertyChanged(nameof(CanAdd));
                    };
                    Candidates.Add(candidate);
                }
                Progress = (i + 1d) / paths.Count * 100;
            }
            InputSummary = paths.Count == 1 ? TranslationService.Text("ReferencePhotoSingular") : TranslationService.Text("ReferencePhotoCount", paths.Count);
            Message = TranslationService.Text(Candidates.Count == 0 ? "NoFacesFound" : "ChooseTargetFaces");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == _referenceGeneration) Message = e.Message; }
        finally { if (generation == _referenceGeneration) { IsBusy = false; OnPropertyChanged(nameof(CanAdd)); } }
    }
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var selected = await DesktopActions.PickImagesAsync(TranslationService.Text("ChooseReferencePhotos"));
        if (selected.Count > 0)
        {
            _form?.Cancel(); _form?.Dispose(); _form = new();
            await (PendingReferences = LoadReferencesAsync(selected, _form.Token));
        }
    }
    [RelayCommand]
    private async Task AddAsync()
    {
        if (!CanAdd) return;
        var generation = _referenceGeneration;
        var ct = _form?.Token ?? default;
        IsBusy = true;
        try
        {
            var references = Candidates.Where(c => c.IsSelected).Select(c => c.Face).ToArray();
            var keywords = Keywords.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await Task.Run(() => people.AddAsync(keywords, references, models.Version, ct));
            await RefreshProfilesAsync();
            if (generation == _referenceGeneration) RequestClose?.Invoke();
            if (IndexUpdated != null) await IndexUpdated();
            QueueIndex();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == _referenceGeneration) Message = e.Message; }
        finally { if (generation == _referenceGeneration) IsBusy = false; }
    }
    [RelayCommand]
    private async Task RemoveAsync(PersonOption? option)
    {
        if (option == null || IsBusy) return;
        try
        {
            await Task.Run(() => people.DeleteAsync(option.Id));
            await RefreshProfilesAsync();
            if (IndexUpdated != null) await IndexUpdated();
        }
        catch (Exception e) { Message = e.Message; }
    }
    private async Task RefreshProfilesAsync()
    {
        var profiles = await Task.Run(() => people.ProfilesAsync());
        Profiles.Clear();
        foreach (var profile in profiles) Profiles.Add(new(profile.Id, string.Join(", ", profile.Keywords)));
    }
    [RelayCommand]
    private void Cancel() { ++_referenceGeneration; _form?.Cancel(); IsBusy = false; RequestClose?.Invoke(); }
    public void Resume() => _stopped = false;
    public void QueueIndex()
    {
        if (_stopped) return;
        if (_queued) { _rerun = true; return; }
        _queued = true;
        PendingIndex = RunIndexAsync();
    }
    private async Task RunIndexAsync()
    {
        try
        {
            do
            {
                _rerun = false;
                if (_stopped) return;
                _index?.Dispose(); _index = new();
                IsIndexing = true;
                IndexStatus = TranslationService.Text("IndexingFaces");
                await Task.Run(() => indexer.ScanAsync(new Progress<WorkProgress>(_ => { }), _index.Token));
                IndexStatus = TranslationService.Text("FacesReady");
                if (IndexUpdated != null) await IndexUpdated();
            } while (_rerun && !_stopped);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { IndexStatus = e.Message; Error?.Invoke(e.Message); }
        finally { _queued = false; IsIndexing = false; }
    }
    public void Stop() { _stopped = true; _form?.Cancel(); _index?.Cancel(); }
}
