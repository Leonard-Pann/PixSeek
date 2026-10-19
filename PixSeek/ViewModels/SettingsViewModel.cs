using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.Services;

namespace PixSeek.ViewModels;
public partial class SettingsViewModel(SettingsStore store, ModelManager models, OnnxEmbeddingService embeddings, NativeTools tools, LibraryDatabase db, IPlatformPaths paths) : ObservableObject
{
    [ObservableProperty]
    private string _theme = "Système";
    [ObservableProperty]
    private decimal? _frameInterval = 10;
    [ObservableProperty]
    private decimal? _maxFrames = 120;
    [ObservableProperty]
    private string _modelStatus = "Vérification du modèle…";
    [ObservableProperty]
    private string _diagnostics = "Diagnostics en cours…";
    [ObservableProperty]
    private bool _isDownloading;
    [ObservableProperty]
    private double _downloadProgress;
    public string[] Themes { get; } = ["Système", "Clair", "Sombre"];
    public string ModelName => "XLM-RoBERTa / ViT-B-32 · français et multilingue";
    public string ModelPath => models.DirectoryPath;
    public string CachePath => paths.Cache;
    public string DataPath => paths.Data;
    public Action<string>? Error { get; set; }
    public Func<Task>? AfterModelReady { get; set; }

    private CancellationTokenSource? _download;
    partial void OnThemeChanged(string value)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = value == "Sombre" ? ThemeVariant.Dark : value == "Clair" ? ThemeVariant.Light : ThemeVariant.Default;
    }

    public Task LoadAsync()
    {
        Theme = store.Current.Theme;
        FrameInterval = store.Current.FrameIntervalSeconds;
        MaxFrames = store.Current.MaxVideoFrames;
        ModelStatus = embeddings.IsReady ? "Prêt · inférence CPU locale" : models.FilesPresent ? "Poids présents · chargement au premier usage" : "Modèle à télécharger · environ 1,48 Go";
        var caps = tools.Capabilities;
        Diagnostics = $".NET {Environment.Version}\nAvalonia {typeof(Application).Assembly.GetName().Version}\nONNX Runtime {typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version}\nsqlite-vec {db.VectorStatus}\n{caps?.FfmpegVersion}\n{caps?.MagickVersion}\nAV1 : {caps?.Av1Encoder ?? "indisponible"}\nJPEG XL : {caps?.Jxl}\nAVIF : {caps?.Avif}\nBackend : {embeddings.Backend}\nBinaires : {paths.Native}\nBase : {db.DatabasePath}\nModèle : {models.Version}\n{embeddings.InputDescription}";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await store.SaveAsync(store.Current with { Theme = Theme, FrameIntervalSeconds = Math.Clamp((int)(FrameInterval ?? 10), 1, 600), MaxVideoFrames = Math.Clamp((int)(MaxFrames ?? 120), 1, 1000) });
            ModelStatus = "Paramètres enregistrés. Rescannez pour appliquer l'échantillonnage vidéo.";
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (IsDownloading)
            return;
        IsDownloading = true;
        _download = new();
        try
        {
            await models.DownloadAsync(new Progress<WorkProgress>(p =>
            {
                ModelStatus = p.Message;
                DownloadProgress = p.Fraction * 100;
            }), _download.Token);
            ModelStatus = "Chargement des sessions ONNX…";
            await embeddings.InitializeAsync(_download.Token);
            await db.ConfigureVectorsAsync(models.Version, models.Manifest.Dimensions, _download.Token);
            ModelStatus = "Prêt · CPU · fichiers SHA-256 vérifiés";
            if (AfterModelReady != null)
                await AfterModelReady();
        }
        catch (OperationCanceledException)
        {
            ModelStatus = "Téléchargement annulé. Vous pouvez réessayer.";
        }
        catch (Exception e)
        {
            ModelStatus = "Modèle indisponible";
            Error?.Invoke(e.Message);
        }
        finally
        {
            IsDownloading = false;
            _download.Dispose();
            _download = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();
    [RelayCommand]
    private void OpenLogs() => DesktopActions.OpenFolder(paths.Logs);
    [RelayCommand]
    private void OpenData() => DesktopActions.OpenFolder(paths.Data);
    public void Stop() => _download?.Cancel();
}
