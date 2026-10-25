using System.Collections.Concurrent;
using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.Media;

public sealed record EncodingTiming(double SecondsPerUnit, int Observations);
/// <summary>Measured EWMA, invalidated by the capability fingerprint. Initial estimates are conservative heuristics.</summary>
public sealed class EncodingPerformanceEstimator
{
    private readonly ConcurrentDictionary<string, EncodingTiming> _timings = new();
    private readonly string? _cachePath;
    private readonly SemaphoreSlim _save = new(1, 1);
    public EncodingPerformanceEstimator(string? cachePath = null)
    {
        _cachePath = cachePath;
        try
        {
            if (cachePath != null && File.Exists(cachePath))
                foreach (var item in JsonSerializer.Deserialize<Dictionary<string, EncodingTiming>>(File.ReadAllText(cachePath)) ?? []) _timings[item.Key] = item.Value;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public string Key(EncoderDescriptor encoder, MediaMetadata metadata, int width, int height, string capabilities) =>
        $"{AdaptiveCompressionPolicy.Version}|{capabilities}|{encoder.Id}|{encoder.RateControl}|{metadata.Codec}|{metadata.Technical.BitDepth}|{metadata.Technical.PixelFormat}|{width}x{height}|fast";
    public double Estimate(EncoderDescriptor encoder, MediaMetadata metadata, MediaType type, int width, int height, string capabilities)
    {
        var key = Key(encoder, metadata, width, height, capabilities);
        var units = Units(type, metadata.DurationMilliseconds / 1000d, width, height);
        if (_timings.TryGetValue(key, out var timing)) return timing.SecondsPerUnit * units * (timing.Observations < 3 ? 1.35 : 1.15) + .15;
        var megapixels = width * (double)height / 1_000_000;
        return type == MediaType.Image ? Math.Max(.25, megapixels * (encoder.CodecId == "avif" ? .5 : .25))
            : Math.Max(.5, metadata.DurationMilliseconds / 1000d * (encoder.Hardware ? .12 : encoder.CodecId == "h264" ? .3 : encoder.CodecId == "hevc" ? .7 : 1.5) * Math.Max(.25, megapixels / 2));
    }
    public void Observe(EncoderDescriptor encoder, MediaMetadata metadata, MediaType type, int width, int height, string capabilities, double elapsedSeconds, double mediaSeconds)
    {
        var units = Units(type, mediaSeconds, width, height);
        if (elapsedSeconds <= 0 || units <= 0) return;
        var measured = elapsedSeconds / units;
        _timings.AddOrUpdate(Key(encoder, metadata, width, height, capabilities), new EncodingTiming(measured, 1), (_, old) => new EncodingTiming(old.SecondsPerUnit * .65 + measured * .35, old.Observations + 1));
    }
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_cachePath == null) return;
        await _save.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_timings.TakeLast(512).ToDictionary(p => p.Key, p => p.Value)), ct);
                File.Move(temporary, _cachePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { _save.Release(); }
    }
    private static double Units(MediaType type, double seconds, int width, int height) => Math.Max(.000001, width * (double)height / 1_000_000) * (type == MediaType.Video ? Math.Max(.01, seconds) : 1);
}
