using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PixSeek.ViewModels;

namespace PixSeek.Services;
public static class AppServices
{
    public static ServiceProvider Create()
    {
        var s = new ServiceCollection();
        s.AddSingleton<IPlatformPaths>(_ => new PlatformPaths());
        s.AddSingleton<ILoggerProvider, FileLoggerProvider>();
        s.AddLogging();
        s.AddSingleton<LibraryDatabase>();
        s.AddSingleton<SettingsStore>();
        s.AddSingleton<ProcessRunner>();
        s.AddSingleton<NativeTools>();
        s.AddSingleton<ImageMagickService>();
        s.AddSingleton<FfprobeService>();
        s.AddSingleton<FfmpegService>();
        s.AddSingleton<ThumbnailService>();
        s.AddSingleton<ModelManager>();
        s.AddSingleton<IExecutionProviderFactory, CpuExecutionProviderFactory>();
        s.AddSingleton<OnnxEmbeddingService>();
        s.AddSingleton<LibraryIndexer>();
        s.AddSingleton<CompressionProfileResolver>();
        s.AddSingleton<CompressionVerificationService>();
        s.AddSingleton<MediaEncoder>();
        s.AddSingleton<MediaCompressionService>();
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<LibraryViewModel>();
        s.AddSingleton<OptimizerViewModel>();
        s.AddSingleton<SettingsViewModel>();
        return s.BuildServiceProvider();
    }
}
