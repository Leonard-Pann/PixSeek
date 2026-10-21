using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.Services;
using PixSeek.Localization;

namespace PixSeek.ViewModels;
public partial class OptimizerViewModel(MediaCompressionService compression, SettingsStore settings, NativeTools tools, ICompressionCodecPolicy codecPolicy, LibraryDatabase db) : ObservableObject
{
    public Action<string>? Error { get; set; }
    public Action? RequestClose { get; set; }

    [ObservableProperty]
    private string _sourceDirectory = "";
    [ObservableProperty]
    private string _destination = "";
    [ObservableProperty]
    private bool _recursive = true;
    [ObservableProperty]
    private int _presetIndex = 1;
    [ObservableProperty]
    private bool _showAdvanced;
    [ObservableProperty]
    private double _imageQuality = 90;
    [ObservableProperty]
    private double _videoQuality = 67;
    [ObservableProperty]
    private string _imageCodec = "JPEG XL";
    [ObservableProperty]
    private string _videoCodec = "AV1";
    [ObservableProperty]
    private bool _lossless;
    [ObservableProperty]
    private bool _keepMetadata = true;
    [ObservableProperty]
    private bool _keepOnlySmaller = true;
    [ObservableProperty]
    private string _analysis = TranslationService.Text("ChooseMedia");
    [ObservableProperty]
    private string _progressText = TranslationService.Text("OriginalsSafe");
    [ObservableProperty]
    private double _progress;
    [ObservableProperty]
    private bool _isBusy;
    [ObservableProperty]
    private bool _isPaused;
    [ObservableProperty]
    private bool _canOptimize;
    [ObservableProperty]
    private string _resultSummary = "";
    [ObservableProperty]
    private string _commandPreview = "";
    [ObservableProperty]
    private string _validationError = "";
    public ObservableCollection<EncodingResult> Results { get; } = [];
    public ObservableCollection<string> ImageCodecs { get; } = [];
    public ObservableCollection<string> VideoCodecs { get; } = [];
    public ObservableCollection<LibraryFolder> LibraryFolders { get; } = [];
    [ObservableProperty] private LibraryFolder? _inputLibraryFolder;
    public Task PendingFolders { get; private set; } = Task.CompletedTask;
    partial void OnInputLibraryFolderChanged(LibraryFolder? value)
    {
        if (value != null) SourceDirectory = value.Path;
    }

    private IReadOnlyList<string>? _selectedFiles;
    private JobControl? _control;
    private CancellationTokenSource? _analysisCancellation;
    private int _dialogVersion;
    private bool _customDestination;
    public bool IsFolderInput => _selectedFiles == null;
    public string Scope => _selectedFiles == null ? TranslationService.Text("CompressFolder") : TranslationService.Text("SelectedCount", _selectedFiles.Count);
    public string SelectionDetails => _selectedFiles == null ? "" : string.Join(", ", _selectedFiles.Take(3).Select(Path.GetFileName)) + (_selectedFiles.Count > 3 ? "…" : "");
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public bool HasResults => !string.IsNullOrEmpty(ResultSummary);
    public bool CanValidate => !IsBusy && !HasResults && !string.IsNullOrWhiteSpace(Destination) && (_selectedFiles?.Count > 0 || Directory.Exists(SourceDirectory));
    public int ResolvedCrf => (int)Math.Round(50 - Math.Clamp(VideoQuality, 0, 100) * .30);
    public string ImageQualityLabel => Lossless ? TranslationService.Text("Lossless") : TranslationService.Text("Quality") + $" {(int)ImageQuality}";
    public string VideoQualityLabel => TranslationService.Text("Quality") + $" {(int)VideoQuality} · CRF {ResolvedCrf}";
    public string PresetDescription => PresetIndex switch
    {
        0 => TranslationService.Text("QualityHint"),
        2 => TranslationService.Text("MaximumHint"),
        _ => TranslationService.Text("BalancedDescription")};
    public CompressionOptions CurrentOptions => new()
    {
        Preset = PresetIndex == 2 ? CompressionPreset.Maximum : CompressionPreset.HighQuality,
        ImageCodec = ShowAdvanced ? ImageCodec : "Auto",
        VideoCodec = ShowAdvanced ? VideoCodec : "AV1",
        Lossless = ShowAdvanced && Lossless,
        ImageQuality = ShowAdvanced ? (int)Math.Round(ImageQuality) : PresetIndex == 0 ? 90 : PresetIndex == 2 ? 45 : 75,
        Distance = ShowAdvanced ? Math.Max(.1, .1 + (100 - Math.Clamp(ImageQuality, 1, 100)) * .09) : PresetIndex == 0 ? .5 : PresetIndex == 2 ? 3 : 1,
        Crf = ShowAdvanced ? ResolvedCrf : PresetIndex == 0 ? 24 : PresetIndex == 2 ? 38 : 30,
        Effort = 7,
        Speed = 8,
        Recursive = Recursive,
        KeepMetadata = KeepMetadata,
        KeepOnlySmaller = KeepOnlySmaller
    };

    public void Prepare(IReadOnlyList<string>? files = null)
    {
        if (IsBusy)
            return;
        _dialogVersion++;
        _selectedFiles = null;
        _customDestination = false;
        SourceDirectory = "";
        Destination = "";
        Results.Clear();
        ResultSummary = "";
        ValidationError = "";
        Progress = 0;
        Analysis = TranslationService.Text("ChooseMedia");
        ProgressText = TranslationService.Text("OriginalsSafe");
        CanOptimize = false;
        LoadSettings();
        ImageCodec = codecPolicy.ImageDefault(tools.Capabilities);
        VideoCodec = codecPolicy.VideoDefault(tools.Capabilities);
        OnPropertyChanged(nameof(ImageCodec));
        OnPropertyChanged(nameof(VideoCodec));
        InputLibraryFolder = null;
        PendingFolders = LoadFoldersAsync();
        if (files?.Count > 0)
            SelectFiles(files);
        NotifyScope();
    }

    private async Task LoadFoldersAsync()
    {
        try
        {
            var folders = await Task.Run(() => db.FoldersAsync());
            LibraryFolders.Clear();
            foreach (var folder in folders) LibraryFolders.Add(folder);
        }
        catch (Exception e) { Error?.Invoke(e.Message); }
    }

    public void SelectFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || IsBusy)
            return;
        SourceDirectory = Path.GetDirectoryName(files[0])!;
        _selectedFiles = files.Distinct().ToArray();
        Analysis = Scope;
        CanOptimize = true;
        NotifyScope();
    }

    private void NotifyScope()
    {
        foreach (var p in new[]
        {
            nameof(IsFolderInput),
            nameof(Scope),
            nameof(SelectionDetails),
            nameof(CanValidate)
        }

        )
            OnPropertyChanged(p);
    }

    public void LoadSettings()
    {
        var options = settings.Current.Compression;
        var imageChoices = new List<string> { "Auto" };
        if (tools.Capabilities?.Jxl == true) imageChoices.Add("JPEG XL");
        if (tools.Capabilities?.Avif == true) imageChoices.Add("AVIF");
        imageChoices.Add("Conserver");
        var videoChoices = new List<string>();
        if (tools.Capabilities?.Av1Encoder != null) videoChoices.Add("AV1");
        videoChoices.Add("Conserver");
        // Do not clear a bound ItemsSource: ComboBox can push null into SelectedItem during rebuild.
        SynchronizeChoices(ImageCodecs, imageChoices);
        SynchronizeChoices(VideoCodecs, videoChoices);
        ImageCodec = options.ImageCodec is not (null or "Auto") && imageChoices.Contains(options.ImageCodec) ? options.ImageCodec : codecPolicy.ImageDefault(tools.Capabilities);
        VideoCodec = options.VideoCodec != null && videoChoices.Contains(options.VideoCodec) ? options.VideoCodec : codecPolicy.VideoDefault(tools.Capabilities);
        PresetIndex = options.Preset == CompressionPreset.Maximum ? 2 : options.Crf is < 28 ? 0 : 1;
        KeepMetadata = options.KeepMetadata;
        KeepOnlySmaller = options.KeepOnlySmaller;
        Recursive = options.Recursive;
        ImageQuality = Math.Clamp(options.Distance.HasValue ? 100 - (options.Distance.Value - .1) / .09 : 90, 1, 100);
        VideoQuality = Math.Clamp((50 - (options.Crf ?? 30)) / .30, 0, 100);
        ShowAdvanced = false;
        Lossless = false;
    }

    private static void SynchronizeChoices(ObservableCollection<string> collection, List<string> desired)
    {
        if (collection.SequenceEqual(desired)) return;
        foreach (var value in desired.Where(v => !collection.Contains(v))) collection.Add(value);
        foreach (var value in collection.Where(v => !desired.Contains(v)).ToArray()) collection.Remove(value);
    }


    partial void OnSourceDirectoryChanged(string value)
    {
        if (InputLibraryFolder != null && InputLibraryFolder.Path != value) InputLibraryFolder = null;
        _selectedFiles = null;
        CanOptimize = false;
        if (!_customDestination || string.IsNullOrEmpty(Destination))
            SetDefaultDestination();
        NotifyScope();
    }

    private void SetDefaultDestination()
    {
        _customDestination = false;
        Destination = string.IsNullOrWhiteSpace(SourceDirectory) ? "" : Path.Combine(SourceDirectory, MediaFormats.OutputFolder);
        _customDestination = false;
    }

    partial void OnDestinationChanged(string value)
    {
        _customDestination = true;
        OnPropertyChanged(nameof(CanValidate));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanValidate));
    partial void OnResultSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanValidate));
    }

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));
    partial void OnPresetIndexChanged(int value)
    {
        ImageQuality = value == 0 ? 95 : value == 2 ? 70 : 90;
        VideoQuality = value == 0 ? 86 : value == 2 ? 40 : 67;
        OnPropertyChanged(nameof(PresetDescription));
    }

    partial void OnImageQualityChanged(double value) => OnPropertyChanged(nameof(ImageQualityLabel));
    partial void OnLosslessChanged(bool value) => OnPropertyChanged(nameof(ImageQualityLabel));
    partial void OnVideoQualityChanged(double value)
    {
        OnPropertyChanged(nameof(VideoQualityLabel));
        OnPropertyChanged(nameof(ResolvedCrf));
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var path = await DesktopActions.PickFolderAsync("Choisir le dossier de médias à compresser");
        if (path != null)
        {
            SourceDirectory = path;
            await AnalyzeAsync();
        }
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        var path = await DesktopActions.PickFolderAsync("Choisir le dossier de sortie");
        if (path != null)
            Destination = path;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy || !Directory.Exists(SourceDirectory))
            return;
        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        IsBusy = true;
        ValidationError = "";
        try
        {
            var analysis = _selectedFiles == null ? await compression.AnalyzeAsync(SourceDirectory, Recursive, cancellation.Token, Destination) : new FolderAnalysis(_selectedFiles.Count(f => MediaFormats.TypeOf(f) == MediaType.Image), _selectedFiles.Count(f => MediaFormats.TypeOf(f) == MediaType.Video), _selectedFiles.Sum(f => new FileInfo(f).Length), _selectedFiles.GroupBy(f => Path.GetExtension(f)).ToDictionary(g => g.Key, g => g.Count()));
            cancellation.Token.ThrowIfCancellationRequested();
            Analysis = $"{analysis.Photos} photos · {analysis.Videos} vidéos · {MediaFormats.Size(analysis.Bytes)}";
            CanOptimize = analysis.Photos + analysis.Videos > 0;
            if (!CanOptimize)
                ValidationError = TranslationService.Text("NoMedia");
        }
        catch (OperationCanceledException)
        {
            CanOptimize = false;
        }
        catch (Exception e)
        {
            ValidationError = e.Message;
            CanOptimize = false;
        }
        finally
        {
            _analysisCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        if (!CanValidate)
            return;
        var version = _dialogVersion;
        ValidationError = "";
        if (ShowAdvanced && Lossless && (ImageCodec == "AVIF" || tools.Capabilities?.Jxl != true))
        {
            ValidationError = "Choisissez JPEG XL disponible pour une compression sans perte.";
            return;
        }

        await AnalyzeAsync();
        if (version == _dialogVersion && CanOptimize)
            await OptimizeAsync();
    }

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (IsBusy || !CanOptimize)
            return;
        var source = SourceDirectory;
        var destination = Destination;
        var options = CurrentOptions;
        var files = _selectedFiles;
        IsBusy = true;
        Results.Clear();
        _control = new();
        try
        {
            await settings.UpdateAsync(s => s with { Compression = options });
            var report = await compression.RunAsync(source, options, _control, new Progress<WorkProgress>(p =>
            {
                Progress = p.Fraction * 100;
                ProgressText = p.Message + " · " + MediaFormats.Size(p.SavedBytes) + " économisés";
            }), files, destination);
            foreach (var item in report.Items)
                Results.Add(item);
            ResultSummary = TranslationService.Text("EncodingSummary", MediaFormats.Size(report.OriginalBytes), MediaFormats.Size(report.FinalBytes), MediaFormats.Size(report.SavedBytes), report.Items.Count(r => r.Status == "Converti"), report.Items.Count(r => r.Status is "Ignoré" or "Aucun gain"), report.Items.Count(r => r.Status == "Échec"));
            Progress = 100;
            ProgressText = TranslationService.Text("EncodingFinished");
        }
        catch (OperationCanceledException)
        {
            ProgressText = TranslationService.Text("EncodingCanceled");
        }
        catch (Exception e)
        {
            ValidationError = e.Message;
        }
        finally
        {
            _control.Dispose();
            _control = null;
            IsBusy = false;
            IsPaused = false;
        }
    }

    [RelayCommand]
    private void Pause()
    {
        if (_control == null)
            return;
        if (IsPaused)
            _control.Resume();
        else
            _control.Pause();
        IsPaused = !IsPaused;
        ProgressText = IsPaused ? "Pause après le fichier en cours" : "Reprise";
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialogVersion++;
        _analysisCancellation?.Cancel();
        _control?.Cancel();
        CanOptimize = false;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void OpenResult()
    {
        if (Directory.Exists(Destination))
            DesktopActions.OpenFolder(Destination);
    }

    public void Stop()
    {
        _dialogVersion++;
        _analysisCancellation?.Cancel();
        _control?.Cancel();
    }
}
