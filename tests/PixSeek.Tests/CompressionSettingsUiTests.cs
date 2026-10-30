using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.ViewModels;
using PixSeek.Views;
using Xunit;

namespace PixSeek.Tests;

public sealed class CompressionSettingsTests
{
    private sealed class TestPaths : IPlatformPaths
    {
        public string Data { get; } = Path.Combine(TestEnvironment.FindRoot(), "artifacts", "tests", "compression-settings-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Data, "cache");
        public string Logs => Path.Combine(Data, "logs");
        public string Models => Path.Combine(Data, "models");
        public string Native => Path.Combine(Data, "native");
        public TestPaths() => Directory.CreateDirectory(Data);
    }

    [Theory]
    [InlineData(0, false, CompressionPreset.PerceptualLossless, 90)]
    [InlineData(1, false, CompressionPreset.Quality, 65)]
    [InlineData(2, false, CompressionPreset.BestCompression, 35)]
    [InlineData(2, true, CompressionPreset.PerceptualLossless, 90)]
    public async Task LegacySettings_ArchiveAmbiguousNativeValues_AndMigrateNumericPresets(int preset, bool lossless, CompressionPreset expected, double quality)
    {
        var paths = new TestPaths();
        var legacy = JsonSerializer.Serialize(new
        {
            Preset = preset, ImageCodec = "JPEG XL", VideoCodec = "AV1", Crf = 24, Distance = .5,
            ImageQuality = 90, Lossless = lossless, KeepMetadata = false, KeepOnlySmaller = false, Recursive = false
        });
        await File.WriteAllTextAsync(Path.Combine(paths.Data, "settings.json"), "{\"Theme\":\"Clair\",\"Compression\":" + legacy + "}");
        var store = new SettingsStore(paths);
        var options = store.Current.Compression;
        Assert.Equal(2, options.Version);
        Assert.Equal(expected, options.Preset);
        Assert.Equal("auto", options.ImageCodec);
        Assert.Equal("auto", options.VideoCodec);
        Assert.Equal(quality, options.ImageAutoQuality);
        Assert.Equal(quality, options.VideoAutoQuality);
        Assert.Null(options.Crf);
        Assert.Null(options.Distance);
        Assert.Null(options.ImageNativeQuality);
        Assert.Null(options.VideoEncoderId);
        Assert.False(options.KeepMetadata);
        Assert.False(options.KeepOnlySmaller);
        Assert.False(options.Recursive);
        Assert.Equal(legacy, options.LegacyOptionsJson);
        Assert.Equal("Clair", store.Current.Theme);
        await store.SaveAsync(store.Current);
        Assert.Equal(options, new SettingsStore(paths).Current.Compression);
    }

    [Fact]
    public async Task VersionTwoManualSettings_RetainBackendNativeScale_AndGeneralDefaults()
    {
        var paths = new TestPaths();
        var store = new SettingsStore(paths);
        Assert.True(store.Current.Compression.KeepMetadata);
        Assert.True(store.Current.Compression.KeepOnlySmaller);
        Assert.Equal(CompressionPreset.Quality, store.Current.Compression.Preset);
        await store.SaveAsync(store.Current with
        {
            Compression = new CompressionOptions
            {
                VideoCodec = "h264", VideoEncoderId = "libx264", VideoRateControl = "crf", VideoNativeQuality = 18,
                ImageCodec = "png", ImageEncoderId = "magick-png", ImageNativeQuality = 9
            }
        });
        var loaded = new SettingsStore(paths).Current.Compression;
        Assert.Equal("h264", loaded.VideoCodec);
        Assert.Equal("libx264", loaded.VideoEncoderId);
        Assert.Equal("crf", loaded.VideoRateControl);
        Assert.Equal(18, loaded.VideoNativeQuality);
        Assert.Null(loaded.LegacyOptionsJson);
    }

    [Fact]
    public void NativeBackendChanges_UseIndependentScales_AndCollapsedAdvancedKeepsSelections()
    {
        var paths = new TestPaths();
        var settings = new SettingsStore(paths);
        var tools = new NativeTools(paths, new ProcessRunner(NullLogger<ProcessRunner>.Instance));
        var vm = new OptimizerViewModel(null!, settings, tools, new CompressionCodecPolicy(), null!);
        vm.LoadSettings();
        Assert.Equal("auto", vm.ImageCodec);
        Assert.Equal("auto", vm.VideoCodec);
        Assert.Equal(new[] { "auto", "jxl", "avif", "heic", "png", "original" }, vm.ImageCodecs.Select(c => c.Id));
        Assert.False(vm.ImageCodecs.Single(c => c.Id == "heic").Available);
        vm.PresetIndex = 0;
        Assert.Equal(90, vm.CurrentOptions.ImageAutoQuality);
        vm.PresetIndex = 2;
        Assert.Equal(35, vm.CurrentOptions.VideoAutoQuality);
        vm.VideoCodec = "h264";
        var crf = new EncoderDescriptor { Id = "software-test", CodecId = "h264", Available = true, Minimum = 0, Maximum = 51, DefaultQuality = 23, QualityName = "CRF", RateControl = "crf" };
        var cq = new EncoderDescriptor { Id = "hardware-test", CodecId = "h264", Available = true, Minimum = 1, Maximum = 63, DefaultQuality = 31, QualityName = "CQ", RateControl = "cq" };
        vm.VideoEncoders.Add(crf);
        vm.VideoEncoders.Add(cq);
        vm.SelectedVideoEncoder = crf;
        vm.VideoQuality = 18;
        vm.SelectedVideoEncoder = cq;
        Assert.Equal(31, vm.VideoQuality);
        Assert.Equal(63, vm.VideoQualityMaximum);
        Assert.Contains("CQ", vm.VideoQualityLabel);
        vm.ShowAdvanced = false;
        Assert.Equal("h264", vm.CurrentOptions.VideoCodec);
        Assert.Equal("hardware-test", vm.CurrentOptions.VideoEncoderId);
        Assert.Equal("cq", vm.CurrentOptions.VideoRateControl);
        vm.SelectedVideoEncoder = crf;
        Assert.Equal(18, vm.VideoQuality);
        vm.VideoCodec = "original";
        Assert.False(vm.VideoQualityEnabled);
        Assert.Null(vm.CurrentOptions.VideoEncoderId);
        vm.VideoCodec = "auto";
        Assert.Equal(35, vm.VideoQuality);
        Assert.Equal(100, vm.VideoQualityMaximum);
    }
}

public sealed partial class UiTests
{
    [AvaloniaFact]
    public async Task CompressionGeneralOptions_AreVisibleWithoutAdvanced_AndAutoDefaultsSurviveReopening()
    {
        var vm = await PrepareAsync();
        var store = PixSeek.App.Services.GetRequiredService<SettingsStore>();
        await store.UpdateAsync(s => s with { Compression = new() });
        var window = Open(vm);
        vm.ShowCompression([]);
        await vm.PendingCompressionOpen;
        await vm.Optimizer.PendingFolders;
        window.UpdateLayout();
        var dialog = window.GetVisualDescendants().OfType<CompressionDialogView>().Single();
        Assert.False(vm.Optimizer.ShowAdvanced);
        Assert.Equal(1, dialog.FindControl<ListBox>("CompressionModes")!.SelectedIndex);
        var metadata = dialog.FindControl<CheckBox>("PreserveMetadataOption")!;
        var smaller = dialog.FindControl<CheckBox>("KeepSmallerOption")!;
        Assert.True(metadata.IsEffectivelyVisible);
        Assert.True(smaller.IsEffectivelyVisible);
        Assert.True(metadata.IsChecked);
        Assert.True(smaller.IsChecked);
        Assert.Equal("auto", vm.Optimizer.CurrentOptions.ImageCodec);
        Assert.Equal("auto", vm.Optimizer.CurrentOptions.VideoCodec);
        vm.Optimizer.ShowAdvanced = true;
        window.UpdateLayout();
        Assert.Equal(0, dialog.FindControl<Slider>("ImageQualitySlider")!.Minimum);
        Assert.Equal(100, dialog.FindControl<Slider>("VideoQualitySlider")!.Maximum);
        vm.Optimizer.CancelCommand.Execute(null);
        vm.ShowCompression([]);
        await vm.PendingCompressionOpen;
        await vm.Optimizer.PendingFolders;
        Assert.Equal("auto", vm.Optimizer.ImageCodec);
        Assert.Equal("auto", vm.Optimizer.VideoCodec);
        vm.Stop();
        window.Close();
    }
}
