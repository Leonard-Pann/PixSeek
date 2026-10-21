using System.Runtime.InteropServices;
using System.Text.Json;
using PixSeek.Core;
using Microsoft.Extensions.Logging;

namespace PixSeek.Infrastructure;
public sealed class PlatformPaths : IPlatformPaths
{
    public string Data { get; }
    public string Cache => Path.Combine(Data, "cache");
    public string Models => Path.Combine(AppContext.BaseDirectory, "models");
    public string Logs => Path.Combine(Data, "logs");
    public string Native { get; }

    public PlatformPaths(string? data = null, string? native = null)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        data = string.IsNullOrWhiteSpace(data) ? EnvironmentPath("PIXSEEK_DATA_DIR") : data;
        Data = data ?? (OperatingSystem.IsMacOS() ? Path.Combine(user, "Library", "Application Support", "PixSeek") : OperatingSystem.IsLinux() ? Path.Combine(EnvironmentPath("XDG_DATA_HOME") ?? Path.Combine(user, ".local", "share"), "PixSeek") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixSeek"));
        var rid = $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        Native = native ?? Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
        foreach (var path in new[]
        {
            Data,
            Cache,
            Logs
        }

        )
            Directory.CreateDirectory(path);
    }

    private static string? EnvironmentPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed record UserSettings
{
    public string Theme { get; init; } = "Système";
    public string Language { get; init; } = "en";
    public string ActiveModelId { get; init; } = "xlmr";
    public int FrameIntervalSeconds { get; init; } = 10;
    public int MaxVideoFrames { get; init; } = 120;
    public long MaxCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public CompressionOptions Compression { get; init; } = new();
}

public sealed class SettingsStore(IPlatformPaths paths)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private string FilePath => Path.Combine(paths.Data, "settings.json");
    public UserSettings Current { get; private set; } = Load(paths);

    private static UserSettings Load(IPlatformPaths p)
    {
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(p.Data, "settings.json"))) ?? new();
        }
        catch (Exception e)when (e is IOException or JsonException)
        {
            return new();
        }
    }

    public async Task SaveAsync(UserSettings settings)
    {
        await _saveGate.WaitAsync();
        try
        {
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
            Current = settings;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task UpdateAsync(Func<UserSettings, UserSettings> update)
    {
        await _saveGate.WaitAsync();
        try
        {
            var settings = update(Current);
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
            Current = settings;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

public sealed class FileLoggerProvider(IPlatformPaths paths) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _logs = paths.Logs;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level))
                return;
            lock (owner._gate)
            {
                try
                {
                    File.AppendAllText(Path.Combine(owner._logs, $"pixseek-{DateTime.Today:yyyy-MM-dd}.log"), $"{DateTimeOffset.Now:O} [{level}] {category}: {formatter(state, exception)} {exception}\n");
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
