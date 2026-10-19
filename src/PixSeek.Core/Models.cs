namespace PixSeek.Core;
public enum MediaType
{
    Image,
    Video
}

public enum CompressionPreset
{
    Preserve,
    HighQuality,
    Maximum
}

public sealed record LibraryFolder(long Id, string Path, string DisplayName, bool Recursive, string Status = "Prêt");
public sealed record MediaFile
{
    public long Id { get; init; }
    public long LibraryFolderId { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
    public MediaType MediaType { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastModifiedAt { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long DurationMilliseconds { get; init; }
    public string Codec { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public string Status { get; init; } = "Découvert";
    public string? ErrorMessage { get; init; }
    public string? IndexedVersion { get; init; }
    public bool HasAlpha { get; init; }
    public int FrameCount { get; init; } = 1;
}

public sealed record SearchResult(MediaFile Media, double Similarity, long? TimestampMilliseconds, string ThumbnailPath);
public sealed record LibraryStats(long Photos, long Videos, long Folders, long Indexed, long Pending);
public sealed record WorkProgress(string Message, int Completed = 0, int Total = 0, double Fraction = 0, long SavedBytes = 0, bool IsIndeterminate = false);
public sealed record MediaMetadata(int Width, int Height, long DurationMilliseconds, string Codec, bool HasAlpha = false, int FrameCount = 1, int AudioStreams = 0, int SubtitleStreams = 0, int TotalStreams = 0, bool Hdr = false, string? RawJson = null);
public sealed record ToolCapabilities(string FfmpegVersion, string MagickVersion, string? Av1Encoder, bool Jxl, bool Avif, string NativeDirectory, string Details);
public sealed record CompressionOptions
{
    public CompressionPreset Preset { get; init; } = CompressionPreset.HighQuality;
    public string ImageCodec { get; init; } = "Auto";
    public string VideoCodec { get; init; } = "AV1";
    public int? ImageQuality { get; init; }
    public bool Lossless { get; init; }
    public double? Distance { get; init; }
    public int Effort { get; init; } = 7;
    public int? Crf { get; init; }
    public int Speed { get; init; } = 8;
    public bool KeepMetadata { get; init; } = true;
    public bool KeepOnlySmaller { get; init; } = true;
    public bool Recursive { get; init; } = true;
}

public sealed record EncodingRequest(string SourcePath, string DestinationPath, MediaType MediaType, MediaMetadata Metadata, CompressionOptions Options);
public sealed record EncodingProgress(double Fraction, string Message);
public sealed record EncodingResult(string SourcePath, string? DestinationPath, long OriginalBytes, long OutputBytes, string Status, string Verification, string? Error = null)
{
    public long SavedBytes => Status == "Converti" ? OriginalBytes - OutputBytes : 0;
}

public sealed record CompressionReport(long JobId, string Destination, IReadOnlyList<EncodingResult> Items)
{
    public long OriginalBytes => Items.Sum(x => x.OriginalBytes);
    public long FinalBytes => Items.Sum(x => x.Status == "Converti" ? x.OutputBytes : x.OriginalBytes);
    public long SavedBytes => OriginalBytes - FinalBytes;
}

public interface IEmbeddingService
{
    Task<float[]> GenerateImageEmbeddingAsync(string imagePath, CancellationToken cancellationToken);
    Task<float[]> GenerateTextEmbeddingAsync(string text, CancellationToken cancellationToken);
}

public interface IVectorSearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(ReadOnlyMemory<float> queryEmbedding, int maxResults, CancellationToken cancellationToken);
}

public interface IMediaEncoder
{
    string Name { get; }

    Task<EncodingResult> EncodeAsync(EncodingRequest request, IProgress<EncodingProgress> progress, CancellationToken cancellationToken);
}

public interface IPlatformPaths
{
    string Data { get; }

    string Cache { get; }

    string Models { get; }

    string Logs { get; }

    string Native { get; }
}

public static class MediaFormats
{
    public const string OutputFolder = "PixSeek Optimized";
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".tif",
        ".tiff",
        ".bmp",
        ".gif",
        ".heic",
        ".heif",
        ".avif",
        ".jxl"
    };
    public static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".mov",
        ".avi",
        ".webm",
        ".m4v",
        ".mts",
        ".m2ts",
        ".wmv"
    };
    public static bool IsMedia(string path) => Images.Contains(Path.GetExtension(path)) || Videos.Contains(Path.GetExtension(path));
    public static MediaType TypeOf(string path) => Videos.Contains(Path.GetExtension(path)) ? MediaType.Video : MediaType.Image;
    public static string Size(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:N2} Go" : bytes >= 1_048_576 ? $"{bytes / 1_048_576d:N1} Mo" : $"{bytes / 1024d:N0} Ko";
}
