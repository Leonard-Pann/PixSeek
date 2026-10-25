using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.Services;
using PixSeek.Localization;

namespace PixSeek.ViewModels;

public sealed class CompressionCodecChoice(string id, bool available, string? unavailableReason) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public bool Available { get; } = available;
    public string? UnavailableReason { get; } = unavailableReason;
    public string Label => CodecLabelConverter.LabelFor(Id) + (Available ? "" : " · " + TranslationService.Text("EncoderUnavailable"));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new(nameof(Label)));
}

public partial class OptimizerViewModel(MediaCompressionService compression, SettingsStore settings, NativeTools tools, ICompressionCodecPolicy codecPolicy, LibraryDatabase db) : ObservableObject
{
    public Action<string>? Error { get; set; }
    public Action? RequestClose { get; set; }

    [ObservableProperty] private string _sourceDirectory = "";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private int _presetIndex = 1;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private double _imageQuality = 65;
    [ObservableProperty] private double _videoQuality = 65;
    [ObservableProperty] private string _imageCodec = "auto";
    [ObservableProperty] private string _videoCodec = "auto";
    [ObservableProperty] private CompressionCodecChoice? _selectedImageCodec;
    [ObservableProperty] private CompressionCodecChoice? _selectedVideoCodec;
    [ObservableProperty] private EncoderDescriptor? _selectedImageEncoder;
    [ObservableProperty] private EncoderDescriptor? _selectedVideoEncoder;
    [ObservableProperty] private bool _keepMetadata = true;
    [ObservableProperty] private bool _keepOnlySmaller = true;
    [ObservableProperty] private string _analysis = TranslationService.Text("ChooseMedia");
    [ObservableProperty] private string _progressText = TranslationService.Text("OriginalsSafe");
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _estimatedRemainingText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _canOptimize;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _commandPreview = "";
    [ObservableProperty] private string _validationError = "";
    [ObservableProperty] private LibraryFolder? _inputLibraryFolder;

    public ObservableCollection<EncodingResult> Results { get; } = [];
    public ObservableCollection<CompressionCodecChoice> ImageCodecs { get; } = [];
    public ObservableCollection<CompressionCodecChoice> VideoCodecs { get; } = [];
    public ObservableCollection<EncoderDescriptor> ImageEncoders { get; } = [];
    public ObservableCollection<EncoderDescriptor> VideoEncoders { get; } = [];
    public ObservableCollection<LibraryFolder> LibraryFolders { get; } = [];
    public Task PendingFolders { get; private set; } = Task.CompletedTask;
    private IReadOnlyList<string>? _selectedFiles;
    private JobControl? _control;
    private CancellationTokenSource? _analysisCancellation;
    private int _dialogVersion;
    private bool _customDestination;
    private bool _loading;
    private bool _translationSubscribed;
    private int _analyzedPhotos;
    private int _analyzedVideos;
    private double _imageAutoQuality = 65;
    private double _videoAutoQuality = 65;
    private readonly Dictionary<string, double> _nativeQualities = new(StringComparer.Ordinal);

    public bool IsFolderInput => _selectedFiles == null;
    public string Scope => _selectedFiles == null ? TranslationService.Text("CompressFolder") : TranslationService.Text("SelectedCount", _selectedFiles.Count);
    public string SelectionDetails => _selectedFiles == null ? "" : string.Join(", ", _selectedFiles.Take(3).Select(Path.GetFileName)) + (_selectedFiles.Count > 3 ? "…" : "");
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public bool HasResults => !string.IsNullOrEmpty(ResultSummary);
    public bool CanValidate => !IsBusy && !HasResults && !string.IsNullOrWhiteSpace(Destination) && (_selectedFiles?.Count > 0 || Directory.Exists(SourceDirectory));
    public bool ImageBackendVisible => ImageCodec is not ("auto" or "original") && ImageEncoders.Count > 0;
    public bool VideoBackendVisible => VideoCodec is not ("auto" or "original") && VideoEncoders.Count > 0;
    public bool ImageQualityEnabled => ImageCodec == "auto" || (ImageCodec != "original" && SelectedImageEncoder?.Available == true);
    public bool VideoQualityEnabled => VideoCodec == "auto" || (VideoCodec != "original" && SelectedVideoEncoder?.Available == true);
    public double ImageQualityMinimum => ImageCodec == "auto" ? 0 : SelectedImageEncoder?.Minimum ?? 0;
    public double ImageQualityMaximum => ImageCodec == "auto" ? 100 : SelectedImageEncoder?.Maximum ?? 100;
    public double ImageQualityStep => ImageCodec == "auto" ? 1 : SelectedImageEncoder?.Step ?? 1;
    public double VideoQualityMinimum => VideoCodec == "auto" ? 0 : SelectedVideoEncoder?.Minimum ?? 0;
    public double VideoQualityMaximum => VideoCodec == "auto" ? 100 : SelectedVideoEncoder?.Maximum ?? 100;
    public double VideoQualityStep => VideoCodec == "auto" ? 1 : SelectedVideoEncoder?.Step ?? 1;
    public string ImageQualityLabel => QualityLabel(ImageCodec, SelectedImageEncoder, ImageQuality);
    public string VideoQualityLabel => QualityLabel(VideoCodec, SelectedVideoEncoder, VideoQuality);
    public string ImageQualityLeft => QualityDirection(ImageCodec, SelectedImageEncoder, true);
    public string ImageQualityRight => QualityDirection(ImageCodec, SelectedImageEncoder, false);
    public string VideoQualityLeft => QualityDirection(VideoCodec, SelectedVideoEncoder, true);
    public string VideoQualityRight => QualityDirection(VideoCodec, SelectedVideoEncoder, false);
    public string ImageCodecStatus => CodecStatus(ImageCodec, ImageCodecs, SelectedImageEncoder);
    public string VideoCodecStatus => CodecStatus(VideoCodec, VideoCodecs, SelectedVideoEncoder);
    public string ImageContainerHint => ImageCodec == "auto" ? TranslationService.Text("AutomaticContainer") : ImageCodec == "original" ? TranslationService.Text("KeepOriginalHint") : TranslationService.Text("ImageContainer", "." + ImageCodec);
    public string VideoContainerHint => VideoCodec == "original" ? TranslationService.Text("KeepOriginalHint") : TranslationService.Text("VideoContainer");
    public string PresetDescription => TranslationService.Text(PresetIndex switch { 0 => "PerceptualLosslessHint", 2 => "MaximumHint", _ => "QualityHint" });
    public CompressionOptions CurrentOptions => new()
    {
        Version = CompressionOptions.CurrentVersion,
        Preset = PresetIndex switch { 0 => CompressionPreset.PerceptualLossless, 2 => CompressionPreset.BestCompression, _ => CompressionPreset.Quality },
        ImageCodec = CompressionCodecs.Normalize(ImageCodec),
        VideoCodec = CompressionCodecs.Normalize(VideoCodec),
        ImageAutoQuality = ImageCodec == "auto" ? ImageQuality : _imageAutoQuality,
        VideoAutoQuality = VideoCodec == "auto" ? VideoQuality : _videoAutoQuality,
        ImageEncoderId = ImageBackendVisible ? SelectedImageEncoder?.Id : null,
        VideoEncoderId = VideoBackendVisible ? SelectedVideoEncoder?.Id : null,
        ImageRateControl = ImageBackendVisible ? SelectedImageEncoder?.RateControl : null,
        VideoRateControl = VideoBackendVisible ? SelectedVideoEncoder?.RateControl : null,
        ImageNativeQuality = ImageBackendVisible ? ImageQuality : null,
        VideoNativeQuality = VideoBackendVisible ? VideoQuality : null,
        Recursive = Recursive,
        KeepMetadata = KeepMetadata,
        KeepOnlySmaller = KeepOnlySmaller,
        LegacyOptionsJson = settings.Current.Compression.LegacyOptionsJson
    };

    private static string QualityLabel(string codec, EncoderDescriptor? encoder, double quality) => codec == "original"
        ? TranslationService.Text("Retain")
        : (codec == "auto" ? TranslationService.Text("Quality") : encoder?.QualityName ?? TranslationService.Text("EncodingQuality")) + " " + quality.ToString("0.##");
    private static string QualityDirection(string codec, EncoderDescriptor? encoder, bool left)
    {
        if (codec == "png") return TranslationService.Text(left ? "LessEffort" : "MoreEffort");
        var lowerIsBetter = codec != "auto" && encoder?.LowerIsBetter == true;
        return TranslationService.Text(left == lowerIsBetter ? "MoreFaithful" : "Smaller");
    }
    private static string CodecStatus(string codec, IEnumerable<CompressionCodecChoice> choices, EncoderDescriptor? encoder)
    {
        if (codec == "original") return TranslationService.Text("KeepOriginalHint");
        if (codec == "auto") return TranslationService.Text("AutoQualityHint");
        var choice = choices.FirstOrDefault(c => c.Id == codec);
        if (choice?.Available != true) return choice?.UnavailableReason ?? TranslationService.Text("EncoderUnavailable");
        if (encoder?.Available != true) return encoder?.UnavailableReason ?? TranslationService.Text("EncoderUnavailable");
        return codec == "png" ? TranslationService.Text("PngQualityHint") : TranslationService.Text("NativeQualityHint");
    }

    public void Prepare(IReadOnlyList<string>? files = null)
    {
        if (IsBusy) return;
        _dialogVersion++;
        _selectedFiles = null;
        _customDestination = false;
        _analyzedPhotos = _analyzedVideos = 0;
        SourceDirectory = "";
        Destination = "";
        Results.Clear();
        ResultSummary = "";
        ValidationError = "";
        Progress = 0;
        PhaseText = EstimatedRemainingText = "";
        Analysis = TranslationService.Text("ChooseMedia");
        ProgressText = TranslationService.Text("OriginalsSafe");
        CanOptimize = false;
        LoadSettings();
        InputLibraryFolder = null;
        PendingFolders = LoadFoldersAsync();
        if (files?.Count > 0) SelectFiles(files);
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
        if (files.Count == 0 || IsBusy) return;
        SourceDirectory = Path.GetDirectoryName(files[0])!;
        _selectedFiles = files.Distinct().ToArray();
        Analysis = Scope;
        CanOptimize = true;
        NotifyScope();
    }
    private void NotifyScope()
    {
        foreach (var property in new[] { nameof(IsFolderInput), nameof(Scope), nameof(SelectionDetails), nameof(CanValidate) })
            OnPropertyChanged(property);
    }

    public void LoadSettings()
    {
        if (!_translationSubscribed)
        {
            TranslationService.Current.Changed += RefreshTranslations;
            _translationSubscribed = true;
        }
        _loading = true;
        try
        {
            var options = settings.Current.Compression;
            PopulateCodecs(ImageCodecs, ["auto", "jxl", "avif", "heic", "png", "original"]);
            PopulateCodecs(VideoCodecs, ["auto", "av1", "hevc", "h264", "vp9", "original"]);
            PresetIndex = Math.Clamp((int)options.Preset, 0, 2);
            ImageCodec = CompressionCodecs.Normalize(string.IsNullOrWhiteSpace(options.ImageCodec) ? codecPolicy.ImageDefault(tools.Capabilities) : options.ImageCodec);
            VideoCodec = CompressionCodecs.Normalize(string.IsNullOrWhiteSpace(options.VideoCodec) ? codecPolicy.VideoDefault(tools.Capabilities) : options.VideoCodec);
            if (!ImageCodecs.Any(c => c.Id == ImageCodec)) ImageCodec = "auto";
            if (!VideoCodecs.Any(c => c.Id == VideoCodec)) VideoCodec = "auto";
            SelectedImageCodec = ImageCodecs.Single(c => c.Id == ImageCodec);
            SelectedVideoCodec = VideoCodecs.Single(c => c.Id == VideoCodec);
            KeepMetadata = options.KeepMetadata;
            KeepOnlySmaller = options.KeepOnlySmaller;
            Recursive = options.Recursive;
            _imageAutoQuality = Math.Clamp(options.ImageAutoQuality, 0, 100);
            _videoAutoQuality = Math.Clamp(options.VideoAutoQuality, 0, 100);
            PopulateEncoders(ImageEncoders, ImageCodec);
            PopulateEncoders(VideoEncoders, VideoCodec);
            SelectedImageEncoder = ChooseEncoder(ImageEncoders, options.ImageEncoderId);
            SelectedVideoEncoder = ChooseEncoder(VideoEncoders, options.VideoEncoderId);
            ImageQuality = ImageCodec == "auto" ? _imageAutoQuality : NativeQuality(SelectedImageEncoder, options.ImageNativeQuality);
            VideoQuality = VideoCodec == "auto" ? _videoAutoQuality : NativeQuality(SelectedVideoEncoder, options.VideoNativeQuality);
            ShowAdvanced = ImageCodec != "auto" || VideoCodec != "auto";
        }
        finally { _loading = false; }
        NotifyCodecProperties();
    }
    private void RefreshTranslations()
    {
        foreach (var choice in ImageCodecs.Concat(VideoCodecs)) choice.RefreshLabel();
        NotifyCodecProperties();
    }
    private void PopulateCodecs(ObservableCollection<CompressionCodecChoice> choices, IEnumerable<string> ids)
    {
        var desired = ids.Select(id =>
        {
            var encoders = tools.Capabilities?.Encoders.Where(e => e.CodecId == id).ToArray() ?? [];
            var available = id is "auto" or "original" || encoders.Any(e => e.Available);
            var reason = available ? null : string.Join("; ", encoders.Select(e => e.UnavailableReason).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
            return new CompressionCodecChoice(id, available, string.IsNullOrWhiteSpace(reason) && !available ? TranslationService.Text("EncoderUnavailable") : reason);
        }).ToArray();
        // Retain bound collections: ComboBox may send null while an unavailable option is replaced.
        static bool Same(CompressionCodecChoice a, CompressionCodecChoice b) => a.Id == b.Id && a.Available == b.Available && a.UnavailableReason == b.UnavailableReason;
        foreach (var choice in desired.Where(c => !choices.Any(existing => Same(c, existing)))) choices.Add(choice);
        foreach (var choice in choices.Where(c => !desired.Any(d => Same(c, d))).ToArray()) choices.Remove(choice);
    }
    private void PopulateEncoders(ObservableCollection<EncoderDescriptor> choices, string codec)
    {
        var desired = tools.Capabilities?.Encoders.Where(e => e.CodecId == codec).ToArray() ?? [];
        foreach (var encoder in desired.Where(e => !choices.Contains(e))) choices.Add(encoder);
        foreach (var encoder in choices.Where(e => !desired.Contains(e)).ToArray()) choices.Remove(encoder);
    }
    private static EncoderDescriptor? ChooseEncoder(IEnumerable<EncoderDescriptor> encoders, string? id) =>
        encoders.FirstOrDefault(e => e.Id == id) ?? encoders.FirstOrDefault(e => e.Available) ?? encoders.FirstOrDefault();
    private static double NativeQuality(EncoderDescriptor? encoder, double? value) =>
        encoder == null ? 0 : Math.Clamp(value ?? encoder.DefaultQuality, encoder.Minimum, encoder.Maximum);
    private void NotifyCodecProperties()
    {
        foreach (var property in new[] { nameof(ImageBackendVisible), nameof(VideoBackendVisible), nameof(ImageQualityEnabled), nameof(VideoQualityEnabled),
            nameof(ImageQualityMinimum), nameof(ImageQualityMaximum), nameof(ImageQualityStep), nameof(VideoQualityMinimum), nameof(VideoQualityMaximum), nameof(VideoQualityStep),
            nameof(ImageQualityLabel), nameof(VideoQualityLabel), nameof(ImageQualityLeft), nameof(ImageQualityRight), nameof(VideoQualityLeft), nameof(VideoQualityRight),
            nameof(ImageCodecStatus), nameof(VideoCodecStatus), nameof(ImageContainerHint), nameof(VideoContainerHint), nameof(PresetDescription) })
            OnPropertyChanged(property);
    }
    partial void OnSelectedImageCodecChanged(CompressionCodecChoice? value) { if (!_loading && value != null) ImageCodec = value.Id; }
    partial void OnSelectedVideoCodecChanged(CompressionCodecChoice? value) { if (!_loading && value != null) VideoCodec = value.Id; }
    partial void OnImageCodecChanged(string value) => ChangeCodec(true, value);
    partial void OnVideoCodecChanged(string value) => ChangeCodec(false, value);
    private void ChangeCodec(bool image, string value)
    {
        if (_loading) return;
        var normalized = CompressionCodecs.Normalize(value);
        if (normalized != value) { if (image) ImageCodec = normalized; else VideoCodec = normalized; return; }
        _loading = true;
        try
        {
            var encoders = image ? ImageEncoders : VideoEncoders;
            var previous = image ? SelectedImageEncoder : SelectedVideoEncoder;
            if (previous != null) _nativeQualities[previous.Id] = image ? ImageQuality : VideoQuality;
            PopulateEncoders(encoders, normalized);
            var encoder = ChooseEncoder(encoders, null);
            var native = NativeQuality(encoder, encoder != null && _nativeQualities.TryGetValue(encoder.Id, out var remembered) ? remembered : null);
            if (image)
            {
                SelectedImageCodec = ImageCodecs.FirstOrDefault(c => c.Id == normalized);
                SelectedImageEncoder = encoder;
                ImageQuality = normalized == "auto" ? _imageAutoQuality : native;
            }
            else
            {
                SelectedVideoCodec = VideoCodecs.FirstOrDefault(c => c.Id == normalized);
                SelectedVideoEncoder = encoder;
                VideoQuality = normalized == "auto" ? _videoAutoQuality : native;
            }
        }
        finally { _loading = false; }
        NotifyCodecProperties();
    }
    partial void OnSelectedImageEncoderChanging(EncoderDescriptor? value) { if (!_loading && SelectedImageEncoder != null) _nativeQualities[SelectedImageEncoder.Id] = ImageQuality; }
    partial void OnSelectedVideoEncoderChanging(EncoderDescriptor? value) { if (!_loading && SelectedVideoEncoder != null) _nativeQualities[SelectedVideoEncoder.Id] = VideoQuality; }
    partial void OnSelectedImageEncoderChanged(EncoderDescriptor? value)
    {
        if (_loading) return;
        ImageQuality = NativeQuality(value, value != null && _nativeQualities.TryGetValue(value.Id, out var quality) ? quality : null);
        NotifyCodecProperties();
    }
    partial void OnSelectedVideoEncoderChanged(EncoderDescriptor? value)
    {
        if (_loading) return;
        VideoQuality = NativeQuality(value, value != null && _nativeQualities.TryGetValue(value.Id, out var quality) ? quality : null);
        NotifyCodecProperties();
    }
    partial void OnImageQualityChanged(double value) { if (!_loading && ImageCodec == "auto") _imageAutoQuality = Math.Clamp(value, 0, 100); OnPropertyChanged(nameof(ImageQualityLabel)); }
    partial void OnVideoQualityChanged(double value) { if (!_loading && VideoCodec == "auto") _videoAutoQuality = Math.Clamp(value, 0, 100); OnPropertyChanged(nameof(VideoQualityLabel)); }
    partial void OnInputLibraryFolderChanged(LibraryFolder? value) { if (value != null) SourceDirectory = value.Path; }
    partial void OnSourceDirectoryChanged(string value)
    {
        if (InputLibraryFolder != null && InputLibraryFolder.Path != value) InputLibraryFolder = null;
        _selectedFiles = null;
        CanOptimize = false;
        if (!_customDestination || string.IsNullOrEmpty(Destination)) SetDefaultDestination();
        NotifyScope();
    }
    private void SetDefaultDestination()
    {
        Destination = string.IsNullOrWhiteSpace(SourceDirectory) ? "" : Path.Combine(SourceDirectory, MediaFormats.OutputFolder);
        _customDestination = false;
    }
    partial void OnDestinationChanged(string value) { _customDestination = true; OnPropertyChanged(nameof(CanValidate)); }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanValidate));
    partial void OnResultSummaryChanged(string value) { OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(CanValidate)); }
    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));
    partial void OnPresetIndexChanged(int value)
    {
        if (_loading) return;
        _imageAutoQuality = _videoAutoQuality = value == 0 ? 90 : value == 2 ? 35 : 65;
        if (ImageCodec == "auto") ImageQuality = _imageAutoQuality;
        if (VideoCodec == "auto") VideoQuality = _videoAutoQuality;
        OnPropertyChanged(nameof(PresetDescription));
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
            _analyzedPhotos = analysis.Photos;
            _analyzedVideos = analysis.Videos;
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
        await AnalyzeAsync();
        if ((_analyzedPhotos > 0 && ImageCodec is not ("auto" or "original") && SelectedImageEncoder?.Available != true) ||
            (_analyzedVideos > 0 && VideoCodec is not ("auto" or "original") && SelectedVideoEncoder?.Available != true))
        {
            ValidationError = TranslationService.Text("SelectedEncoderUnavailable");
            return;
        }
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
        PhaseText = TranslationService.Text("CompressionPhaseAnalysis");
        EstimatedRemainingText = TranslationService.Text("EtaPending");
        _control = new();
        try
        {
            await settings.UpdateAsync(s => s with { Compression = options });
            var report = await compression.RunAsync(source, options, _control, new Progress<WorkProgress>(p =>
            {
                Progress = p.Fraction * 100;
                ProgressText = p.Message + " · " + TranslationService.Text("SavedSpace", MediaFormats.Size(p.SavedBytes));
                PhaseText = PhaseLabel(p.Phase);
                EstimatedRemainingText = p.EstimatedRemaining is { } remaining
                    ? TranslationService.Text("EtaEstimate", remaining.TotalHours >= 1 ? remaining.ToString(@"hh\:mm\:ss") : remaining.ToString(@"mm\:ss"))
                    : TranslationService.Text("EtaPending");
            }), files, destination);
            foreach (var item in report.Items)
                Results.Add(item);
            ResultSummary = TranslationService.Text("EncodingSummary", MediaFormats.Size(report.OriginalBytes), MediaFormats.Size(report.FinalBytes), MediaFormats.Size(report.SavedBytes), report.Items.Count(r => r.Status == "Converti"), report.Items.Count(r => r.Status is "Ignoré" or "Aucun gain"), report.Items.Count(r => r.Status == "Échec"));
            Progress = 100;
            PhaseText = EstimatedRemainingText = "";
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

    private static string PhaseLabel(string? phase) => phase?.ToLowerInvariant() switch
    {
        "analysis" or "analyse" => TranslationService.Text("CompressionPhaseAnalysis"),
        "capabilities" or "benchmark" => TranslationService.Text("CompressionPhaseBenchmark"),
        "trial" or "trials" or "sampling" or "metrics" => TranslationService.Text("CompressionPhaseTrials"),
        "encoding" or "encode" => TranslationService.Text("CompressionPhaseEncoding"),
        "verification" or "verify" => TranslationService.Text("CompressionPhaseVerification"),
        "publish" or "publishing" => TranslationService.Text("CompressionPhasePublish"),
        _ => phase ?? ""
    };

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
