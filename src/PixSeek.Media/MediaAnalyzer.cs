using System.Globalization;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Builds the compression input description without changing the source file.</summary>
public sealed class MediaAnalyzer(ImageMagickService images, FfprobeService probe, PhotoMetadataService metadata)
{
    public async Task<MediaMetadata> AnalyzeAsync(string source, MediaType type, CancellationToken ct = default)
    {
        if (type == MediaType.Video) return await probe.ProbeAsync(source, ct);
        var basic = await images.IdentifyAsync(source, ct);
        var snapshot = await metadata.ReadAsync(source, ct);
        var values = new Dictionary<string, string>(snapshot.Values, StringComparer.OrdinalIgnoreCase)
        {
            ["pixseek:metadata_complete"] = snapshot.Complete ? "true" : "false"
        };
        if (snapshot.Issue != null) values["pixseek:metadata_issue"] = snapshot.Issue;
        var hasGainMap = values.Any(p => p.Key.Contains("GainMap", StringComparison.OrdinalIgnoreCase) || p.Value.Contains("gainmap", StringComparison.OrdinalIgnoreCase));
        var hasAuxiliary = hasGainMap || values.Any(p => p.Key.Contains("Auxiliary", StringComparison.OrdinalIgnoreCase) ||
            p.Key.Contains("DepthMap", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("MPImage2", StringComparison.OrdinalIgnoreCase) ||
            (p.Key.EndsWith(":NumberOfImages", StringComparison.OrdinalIgnoreCase) && int.TryParse(p.Value, out var count) && count > 1));
        var transfer = Find(values, "TransferCharacteristics");
        var hdr = transfer is "16" or "18" || transfer.Contains("2084", StringComparison.OrdinalIgnoreCase) || transfer.Contains("HLG", StringComparison.OrdinalIgnoreCase);
        return basic with
        {
            Hdr = hdr,
            Technical = basic.Technical with
            {
                Orientation = snapshot.Values.ContainsKey("IFD0:Orientation") ? snapshot.Orientation : basic.Technical.Orientation,
                IccHash = snapshot.IccHash, Metadata = values, HasGainMap = hasGainMap, HasAuxiliaryImages = hasAuxiliary,
                HasDynamicHdr = values.Keys.Any(k => k.Contains("DolbyVision", StringComparison.OrdinalIgnoreCase) || k.Contains("HDR10Plus", StringComparison.OrdinalIgnoreCase)),
                ColorPrimaries = Find(values, "ColorPrimaries"), ColorTransfer = transfer,
                ColorRange = Find(values, "VideoFullRangeFlag")
            }
        };
    }

    private static string Find(IReadOnlyDictionary<string, string> values, string tag) =>
        values.FirstOrDefault(p => p.Key.EndsWith(":" + tag, StringComparison.OrdinalIgnoreCase)).Value ?? "";
}
