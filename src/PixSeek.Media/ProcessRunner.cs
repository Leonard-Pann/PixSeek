using System.Diagnostics;
using System.Text;
using PixSeek.Core;
using Microsoft.Extensions.Logging;

namespace PixSeek.Media;
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed class ProcessRunner(ILogger<ProcessRunner> logger)
{
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct = default, Action<string>? onOutput = null, TimeSpan? timeout = null)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Outil embarqué absent : {executable}. Exécutez scripts/bootstrap-windows.ps1.", executable);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromHours(12));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var a in arguments)
            process.StartInfo.ArgumentList.Add(a);
        logger.LogInformation("Exécution {Tool} ({Count} arguments)", Path.GetFileName(executable), process.StartInfo.ArgumentList.Count);
        process.Start();
        using var registration = deadline.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        static void AppendBounded(StringBuilder b, string line)
        {
            if (b.Length > 2_000_000)
                b.Remove(0, 1_000_000);
            b.AppendLine(line);
        }

        async Task ReadAsync(StreamReader reader, StringBuilder target, Action<string>? handler)
        {
            while (await reader.ReadLineAsync(deadline.Token)is { } line)
            {
                AppendBounded(target, line);
                handler?.Invoke(line);
            }
        }

        try
        {
            await Task.WhenAll(ReadAsync(process.StandardOutput, stdout, onOutput), ReadAsync(process.StandardError, stderr, null), process.WaitForExitAsync(deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Délai dépassé : {Path.GetFileName(executable)}");
        }

        if (process.ExitCode != 0)
            logger.LogWarning("{Tool} code {Code}: {Error}", executable, process.ExitCode, stderr.ToString());
        return new(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<string> CheckedAsync(string exe, IEnumerable<string> args, CancellationToken ct = default, Action<string>? output = null, TimeSpan? timeout = null)
    {
        var r = await RunAsync(exe, args, ct, output, timeout);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} : {r.StandardError[^Math.Min(2200, r.StandardError.Length)..]}");
        return r.StandardOutput;
    }
}

public sealed class NativeTools(IPlatformPaths paths, ProcessRunner runner)
{
    public string PathFor(string name) => Path.Combine(paths.Native, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    public ToolCapabilities? Capabilities { get; private set; }

    public async Task<ToolCapabilities> DetectAsync(CancellationToken ct = default)
    {
        var ff = await runner.CheckedAsync(PathFor("ffmpeg"), ["-version"], ct, timeout: TimeSpan.FromSeconds(20));
        _ = await runner.CheckedAsync(PathFor("ffprobe"), ["-version"], ct, timeout: TimeSpan.FromSeconds(20));
        var enc = await runner.CheckedAsync(PathFor("ffmpeg"), ["-hide_banner", "-encoders"], ct);
        var im = await runner.CheckedAsync(PathFor("magick"), ["-version"], ct);
        var formats = await runner.CheckedAsync(PathFor("magick"), ["identify", "-list", "format"], ct);
        bool Writable(string format) => System.Text.RegularExpressions.Regex.IsMatch(formats, $@"(?m)^\s*{format}\*?\s+rw");
        return Capabilities = new(ff.Split('\n')[0], im.Split('\n')[0], enc.Contains("libsvtav1") ? "libsvtav1" : enc.Contains("libaom-av1") ? "libaom-av1" : null, Writable("JXL"), Writable("AVIF"), paths.Native, $"{ff}\n{im}\n{formats}");
    }
}
