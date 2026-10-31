using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Checks descriptive container metadata that FFprobe does not always expose (QuickTime keys, XMP, GPS, maker data).</summary>
public sealed class ContainerMetadataGuard(NativeTools tools, ProcessRunner runner)
{
    // Container layout and encoder bookkeeping are regenerated. Descriptive and unknown fields are never excluded implicitly.
    private static readonly HashSet<string> Technical = new(StringComparer.OrdinalIgnoreCase)
    {
        "EBMLVersion", "EBMLReadVersion", "EBMLMaxIDLength", "EBMLMaxSizeLength", "DocType", "DocTypeVersion", "DocTypeReadVersion",
        "SeekID", "SeekPosition", "CRC-32", "TimecodeScale", "MuxingApp", "WritingApp", "SegmentUID", "Duration", "Encoder",
        "TrackNumber", "TrackUID", "TagTrackUID", "TrackLacing", "CodecID", "CodecPrivate", "VideoFrameRate", "ImageWidth", "ImageHeight",
        // These semantic fields are compared per stream by CompressionVerificationService, despite container-specific names.
        "TrackLanguage", "MediaLanguageCode", "TrackName", "TrackDefault", "TrackForced", "TrackType",
        "VideoScanType", "MaxBlockAdditionID", "AudioChannels", "AudioSampleRate", "AudioBitsPerSample", "CodecDelay", "SeekPreRoll",
        "Matroska_0x15b0", "Matroska_0x15af", "Matroska_0x16aa", "AttachedFileUID", "ChapterUID", "BlockDuration",
        "TimeCode", "CueTime", "CueTrack", "CueClusterPosition", "Matroska_0x0070", "Matroska_0x0032",
        "MajorBrand", "MinorVersion", "CompatibleBrands", "Free", "MovieHeaderVersion", "TimeScale", "NextTrackID",
        "TrackHeaderVersion", "TrackID", "TrackDuration", "MediaHeaderVersion", "MediaTimeScale", "MediaDuration", "HandlerType",
        "CompressorID", "SourceImageWidth", "SourceImageHeight", "CompressorName", "BitDepth", "AVCConfiguration", "HEVCConfiguration",
        "AV1Configuration", "VPCodecConfiguration", "PixelAspectRatio", "BufferSize", "MaxBitrate", "AverageBitrate", "SyncSampleTable",
        "CompositionTimeToSample", "SampleToChunk", "SampleSizes", "ChunkOffset", "ChunkOffset64", "TimeToSampleTable", "SampleCount",
        "MediaDataSize", "MediaDataOffset", "MediaData", "Unknown_edts", "Unknown_esds", "SampleGroupDescription", "SampleToGroup", "HandlerVendorID", "AudioFormat", "AudioSampleRate", "AudioBitsPerSample",
        "AudioChannels", "AudioFrameCount", "AudioBytesPerFrame", "AudioSamplesPerPacket", "AudioBytesPerPacket", "AudioBytesPerSample",
        "AudioFormatFlags", "AudioSampleRate", "SoundVersion", "Balance", "OpusHead", "OpusTags", "ESDescriptor"
    };

    public async Task VerifyAsync(string source, string destination, CancellationToken ct)
    {
        if (!File.Exists(tools.PathFor("exiftool")))
            throw new InvalidDataException("ExifTool absent : l'inventaire des métadonnées du conteneur ne peut pas être vérifié intégralement.");
        var inventory = await ReadAsync(source, ["-all"], false, ct);
        var required = inventory.Where(p => Required(p.Key, p.Value)).Select(p => p.Key).ToArray();
        var before = required.Length == 0 ? new Dictionary<string, string>() : await ReadAsync(source, required.Select(k => "-" + k), true, ct);
        if (required.Any(key => !before.ContainsKey(key)))
            throw new InvalidDataException("Une métadonnée du conteneur ne peut pas être relue intégralement ; conservation de l'original requise.");
        if (before.Count == 0) return;
        var after = await ReadAsync(destination, required.Select(k => "-" + k[(k.IndexOf(':') + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase), true, ct);
        var missing = before.Where(tag => !after.Any(p => p.Key[(p.Key.IndexOf(':') + 1)..].Equals(tag.Key[(tag.Key.IndexOf(':') + 1)..], StringComparison.OrdinalIgnoreCase) && p.Value == tag.Value)).Select(p => p.Key).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("Métadonnées du conteneur non conservées : " + string.Join(", ", missing) + ". L'original doit être exporté sans réencodage.");
    }
    private static bool Required(string key, string value)
    {
        var group = key.Split(':')[0]; var name = key[(key.IndexOf(':') + 1)..];
        if (group is "QuickTime" || group.StartsWith("Track", StringComparison.Ordinal) || group is "Matroska" or "Info" || group.StartsWith("Chapter", StringComparison.Ordinal))
        {
            if (Technical.Contains(name)) return false;
            // Unset dates/default presentation flags carry no user information; non-default values must survive.
            if (name.EndsWith("Date", StringComparison.Ordinal) && value.StartsWith("0000:", StringComparison.Ordinal)) return false;
            if (name is "PreviewTime" or "PreviewDuration" or "PosterTime" or "SelectionTime" or "SelectionDuration" or "CurrentTime" or "TrackLayer" or "TrackVolume" or "GraphicsMode" && value == "0") return false;
            if (name is "PreferredRate" or "PreferredVolume" && value == "1") return false;
            if (name == "TrackVolume" && value == "1") return false;
            if (name == "OpColor" && value == "0 0 0") return false;
            if (name is "XResolution" or "YResolution" && value == "72") return false;
            if (name == "MatrixStructure" && value == "1 0 0 0 1 0 0 0 1") return false;
            if (name == "HandlerDescription" && value is "VideoHandler" or "SoundHandler") return false;
        }
        return true;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadAsync(string file, IEnumerable<string> selection, bool binary, CancellationToken ct)
    {
        var args = new List<string> { "-charset", "filename=UTF8", "-json", "-G1", "-s", "-n", "-struct", "-a", "-U" };
        if (binary) args.Add("-b");
        args.AddRange(selection);
        args.Add(file);
        var json = await ExifToolCommand.CheckedAsync(tools, runner, args, ct);
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>();
        foreach (var property in doc.RootElement[0].EnumerateObject())
        {
            var group = property.Name.Split(':')[0];
            if (property.Name == "SourceFile" || group is "File" or "System" or "Composite") continue;
            if (group == "ExifTool")
            {
                if (property.Name.EndsWith(":Warning", StringComparison.Ordinal) || property.Name.EndsWith(":Error", StringComparison.Ordinal))
                    throw new InvalidDataException("Inventaire des métadonnées du conteneur incomplet : " + property.Value);
                continue;
            }
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
            if (!result.TryAdd(property.Name, value) && Required(property.Name, value))
                throw new InvalidDataException("Métadonnée de conteneur répétée sans identité distincte : " + property.Name + ". Conservation de l'original requise.");
        }
        return result;
    }
}
