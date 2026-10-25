namespace PixSeek.Core;

public static class CompressionCodecs
{
    public const string Auto = "auto", Original = "original", Jxl = "jxl", Avif = "avif", Heic = "heic", Png = "png",
        Av1 = "av1", Hevc = "hevc", H264 = "h264", Vp9 = "vp9";

    public static string Normalize(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => Auto,
        "conserver" or "keep original" or "keeporiginal" or "original" => Original,
        "jpeg xl" or "jpegxl" or "jxl" => Jxl,
        "h.265" or "h.265/hevc" or "h265" or "hevc" => Hevc,
        "h.264" or "h.264/avc" or "h264" or "avc" => H264,
        "heif" or "heic" => Heic,
        var value => value
    };
}

public sealed record EncoderDescriptor
{
    public string Id { get; init; } = "";
    public string CodecId { get; init; } = "";
    public string Tool { get; init; } = "ffmpeg";
    public string QualityName { get; init; } = "CRF";
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 63;
    public double Step { get; init; } = 1;
    public bool LowerIsBetter { get; init; } = true;
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public bool Hardware { get; init; }
    public string RateControl { get; init; } = "crf";
    public IReadOnlyList<string> PixelFormats { get; init; } = [];
    public double DefaultQuality { get; init; } = 28;
    public string Version { get; init; } = "";
    public override string ToString() => Id;
}

public sealed record MediaTechnicalMetadata
{
    public string Container { get; init; } = "";
    public string PixelFormat { get; init; } = "";
    public int BitDepth { get; init; } = 8;
    public string ChromaSubsampling { get; init; } = "";
    public string ColorPrimaries { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string ColorSpace { get; init; } = "";
    public string ColorRange { get; init; } = "";
    public string FrameRate { get; init; } = "";
    public string AverageFrameRate { get; init; } = "";
    public string TimeBase { get; init; } = "";
    public string SampleAspectRatio { get; init; } = "";
    public string DisplayAspectRatio { get; init; } = "";
    public int Rotation { get; init; }
    public int Orientation { get; init; } = 1;
    public bool HasGainMap { get; init; }
    public bool HasDynamicHdr { get; init; }
    public bool HasAuxiliaryImages { get; init; }
    public bool Interlaced { get; init; }
    public IReadOnlyList<MediaStreamMetadata> Streams { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public string? IccHash { get; init; }
}

public sealed record MediaStreamMetadata
{
    public int Index { get; init; }
    public string CodecType { get; init; } = "";
    public string Codec { get; init; } = "";
    public string PixelFormat { get; init; } = "";
    public int BitDepth { get; init; }
    public string ChannelLayout { get; init; } = "";
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public long? BitRate { get; init; }
    public string Language { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyDictionary<string, int> Disposition { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public bool AttachedPicture { get; init; }
    public string TimeBase { get; init; } = "";
    public double StartTimeSeconds { get; init; }
    public double? DurationSeconds { get; init; }
}

public sealed record PerceptualQualityReport
{
    public string Metric { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Passed { get; init; }
    public bool Sampled { get; init; }
    public double Score { get; init; }
    public double MinimumWindowScore { get; init; }
    public double? Percentile5 { get; init; }
    public double? Butteraugli { get; init; }
    public string Details { get; init; } = "";
    public IReadOnlyList<QualityWindowResult> Windows { get; init; } = [];
}

public sealed record QualityWindowResult(double StartSeconds, double DurationSeconds, double Mean, double Percentile5);

public sealed record CompressionDecision
{
    public string AlgorithmVersion { get; init; } = "adaptive-v2";
    public string CodecId { get; init; } = "";
    public string SourceCodecId { get; init; } = "";
    public string EncoderId { get; init; } = "";
    public string RateControl { get; init; } = "";
    public double NativeQuality { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Automatic { get; init; }
    public bool VideoCopied { get; init; }
    public string Reason { get; init; } = "";
    public string SourceFingerprint { get; init; } = "";
    public string CapabilitiesFingerprint { get; init; } = "";
    public double ElapsedSeconds { get; init; }
    public double EstimatedSeconds { get; init; }
    public PerceptualQualityReport? Quality { get; init; }
}
