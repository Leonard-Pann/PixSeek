using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed class CapabilityCacheTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task WarmCapabilityCache_ReturnsRealCapabilities_WithoutLaunchingAnyTool()
    {
        // Produce a disk cache from real encode/decode probes, independently of the process cache.
        var expected = await new EncoderCapabilityService(e.Tools, e.Runner).DetectAsync();
        var logger = new CountingLogger();
        var runner = new ProcessRunner(logger);
        var cached = await new EncoderCapabilityService(e.Tools, runner).DetectAsync();
        Assert.Equal(expected.Fingerprint, cached.Fingerprint);
        Assert.Equal(expected.Encoders.Select(e => (e.Id, e.Available, e.Version, e.UnavailableReason)),
            cached.Encoders.Select(e => (e.Id, e.Available, e.Version, e.UnavailableReason)));
        Assert.Equal(0, logger.Calls);
    }

    private sealed class CountingLogger : ILogger<ProcessRunner>
    {
        public int Calls;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Interlocked.Increment(ref Calls);
    }
}
