using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Services;
using PixSeek.Localization;

namespace PixSeek.ViewModels;
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ModelManager _models;
    private readonly OnnxEmbeddingService _embeddings;
    private readonly LibraryDatabase _db;
    private readonly IPlatformPaths _paths;
    private readonly ImagePreviewCache _previews;
    private bool _loading, _loaded;
    private int _saveVersion;
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private int _activationVersion;
    public string RequestedModelId { get; private set; }
    [ObservableProperty]
    private Choice _theme;
    [ObservableProperty]
    private Choice _language;
    [ObservableProperty]
    private decimal? _frameInterval = 10;
    [ObservableProperty]
    private decimal? _maxFrames = 120;
    [ObservableProperty]
    private bool _isModelBusy;
    [ObservableProperty]
    private double _downloadProgress;
    [ObservableProperty]
    private string _modelStatus = "";
    [ObservableProperty]
    private string _cacheStatus = "";
    public Choice[] Themes { get; } = [new("Système", "System"), new("Sombre", "Dark"), new("Clair", "Light")];
    public Choice[] Languages { get; } = [new("en", "English", true), new("fr", "Français", true), new("it", "Italiano", true), new("de", "Deutsch", true), new("es", "Español", true)];
    public IReadOnlyList<ModelOptionViewModel> Models { get; }
    public string Version => TranslationService.Text("Version", typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
    public Action<string>? Error { get; set; }
    public Func<Task>? BeforeModelChange { get; set; }
    public Func<Task>? AfterModelReady { get; set; }
    public Func<Task>? AfterSamplingChanged { get; set; }
    public Func<Task>? AfterCacheClear { get; set; }
    public Func<Task>? AfterLanguageChanged { get; set; }
    public Task PendingSave { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _download;
    public SettingsViewModel(SettingsStore store, ModelManager models, OnnxEmbeddingService embeddings, LibraryDatabase db, IPlatformPaths paths, ImagePreviewCache previews)
    {
        _store = store;
        _models = models;
        RequestedModelId = models.ActiveId;
        _embeddings = embeddings;
        _db = db;
        _paths = paths;
        _previews = previews;
        _theme = Themes[0];
        _language = Languages[0];
        TranslationService.Current.Load(store.Current.Language);
        Models = models.AvailableIds.Select(id => new ModelOptionViewModel(id, models, this)).ToArray();
    }

    partial void OnThemeChanged(Choice value)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = value.Id == "Sombre" ? ThemeVariant.Dark : value.Id == "Clair" ? ThemeVariant.Light : ThemeVariant.Default;
        ScheduleSave();
    }

    partial void OnLanguageChanged(Choice value)
    {
        if (!_loading)
        {
            TranslationService.Current.Load(value.Id);
            OnPropertyChanged(nameof(Version));
            foreach (var model in Models)
                model.Refresh();
            if (AfterLanguageChanged != null)
                _ = AfterLanguageChanged();
        }

        ScheduleSave();
    }

    partial void OnFrameIntervalChanged(decimal? value) => ScheduleSave();
    partial void OnMaxFramesChanged(decimal? value) => ScheduleSave();
    partial void OnIsModelBusyChanged(bool value)
    {
        foreach (var model in Models)
            model.Refresh();
    }

    private void ScheduleSave()
    {
        if (!_loading)
            PendingSave = SaveAfterDelayAsync(++_saveVersion);
    }

    private async Task SaveAfterDelayAsync(int version)
    {
        await Task.Delay(250);
        if (version != _saveVersion)
            return;
        try
        {
            var interval = Math.Clamp((int)(FrameInterval ?? 10), 1, 600);
            var frames = Math.Clamp((int)(MaxFrames ?? 120), 1, 1000);
            var samplingChanged = _store.Current.FrameIntervalSeconds != interval || _store.Current.MaxVideoFrames != frames;
            await _store.UpdateAsync(s => s with { Theme = Theme.Id, Language = Language.Id, FrameIntervalSeconds = interval, MaxVideoFrames = frames });
            if (samplingChanged && AfterSamplingChanged != null)
                await AfterSamplingChanged();
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
    }

    public Task LoadAsync()
    {
        if (!_loaded)
        {
            _loading = true;
            Theme = Themes.FirstOrDefault(t => t.Id == _store.Current.Theme) ?? Themes[0];
            Language = Languages.FirstOrDefault(l => l.Id == _store.Current.Language) ?? Languages[0];
            FrameInterval = _store.Current.FrameIntervalSeconds;
            MaxFrames = _store.Current.MaxVideoFrames;
            _loading = false;
            _loaded = true;
        }

        foreach (var model in Models)
            model.Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ClearCachesAsync()
    {
        try
        {
            if (BeforeModelChange != null)
                await BeforeModelChange();
            await _previews.ClearAsync();
            var protectedFrames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var connection = await _db.OpenAsync())
            {
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT ThumbnailPath FROM VideoFrame";
                await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    protectedFrames.Add(reader.GetString(0));
            }

            foreach (var file in Directory.EnumerateFiles(_paths.Cache))
                if (!protectedFrames.Contains(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                }

            CacheStatus = TranslationService.Text("CacheCleared");
            if (AfterCacheClear != null)
                await AfterCacheClear();
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
    }

    [RelayCommand]
    private async Task ManageModelAsync(ModelOptionViewModel? option)
    {
        if (option == null || IsModelBusy || option.IsActive)
            return;
        IsModelBusy = true;
        _download = new();
        DownloadProgress = 0;
        try
        {
            var manager = _models.For(option.Id);
            if (manager.FilesPresent)
                await Task.Run(manager.Delete);
            else
                await manager.DownloadAsync(new Progress<WorkProgress>(p =>
                {
                    DownloadProgress = p.Fraction * 100;
                    ModelStatus = TranslationService.Text("Download") + $" · {p.Fraction * 100:F0}%";
                }), _download.Token);
            ModelStatus = manager.FilesPresent ? TranslationService.Text("Installed") : TranslationService.Text("NotInstalled");
        }
        catch (OperationCanceledException)
        {
            ModelStatus = TranslationService.Text("Cancel");
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
        finally
        {
            _download.Dispose();
            _download = null;
            IsModelBusy = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ActivateModelAsync(ModelOptionViewModel? option)
    {
        if (option == null || !option.IsInstalled || _download != null)
        {
            foreach (var model in Models)
                model.Refresh();
            return;
        }

        RequestedModelId = option.Id;
        foreach (var model in Models)
            model.Refresh();
        var version = ++_activationVersion;
        await _activationGate.WaitAsync();
        if (version != _activationVersion)
        {
            _activationGate.Release();
            return;
        }
        if (_models.ActiveId == option.Id)
        {
            _activationGate.Release();
            return;
        }
        IsModelBusy = true;
        var oldId = _models.ActiveId;
        try
        {
            if (BeforeModelChange != null)
                await BeforeModelChange();
            ModelStatus = TranslationService.Text("ModelChanging");
            await Task.Run(async () =>
            {
                await _embeddings.ResetAsync();
                _models.Select(option.Id);
                await _embeddings.InitializeAsync();
                await _db.ConfigureVectorsAsync(_models.Version, _models.Manifest.Dimensions, modelName: _models.Name);
                await _store.UpdateAsync(s => s with { ActiveModelId = option.Id });
            });
            if (version == _activationVersion && AfterModelReady != null)
                await AfterModelReady();
            ModelStatus = TranslationService.Text("Ready");
        }
        catch (Exception e)
        {
            _models.Select(oldId);
            await Task.Run(() => _embeddings.ResetAsync());
            if (version == _activationVersion)
                RequestedModelId = oldId;
            Error?.Invoke(e.Message);
        }
        finally
        {
            IsModelBusy = false;
            foreach (var model in Models)
                model.Refresh();
            _activationGate.Release();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();
    public void Stop() => _download?.Cancel();
}

public partial class ModelOptionViewModel(string id, ModelManager models, SettingsViewModel owner) : ObservableObject
{
    public string Id => id;
    public string Name => models.For(id).Name;
    public string Description => id == "siglip2" ? "SigLIP2 text + ViT-B/16 · 768D · 1.54 GB" : "XLM-RoBERTa text + ViT-B/32 · 512D · 1.48 GB";
    public bool IsInstalled => models.For(id).FilesPresent;
    public bool CanSelect => IsInstalled;
    public bool CanManage => !owner.IsModelBusy && !IsActive;
    public string ManageLabel => TranslationService.Text(IsInstalled ? "Delete" : "Download");
    public string InstallStatus => TranslationService.Text(IsInstalled ? "Installed" : "NotInstalled");
    public IAsyncRelayCommand ManageCommand => owner.ManageModelCommand;

    private bool _refreshing;
    [ObservableProperty]
    private bool _isActive;
    partial void OnIsActiveChanged(bool value)
    {
        if (_refreshing)
            return;
        if (value)
            owner.ActivateModelCommand.Execute(this);
        else
            Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        IsActive = owner.RequestedModelId == id && IsInstalled;
        _refreshing = false;
        foreach (var property in new[]
        {
            nameof(IsInstalled),
            nameof(CanSelect),
            nameof(CanManage),
            nameof(ManageLabel),
            nameof(InstallStatus)
        }

        )
            OnPropertyChanged(property);
    }
}
