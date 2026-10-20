using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Single decision point for defaults. A future hardware probe can replace this policy in DI.</summary>
public interface ICompressionCodecPolicy
{
    string ImageDefault(ToolCapabilities? capabilities);
    string VideoDefault(ToolCapabilities? capabilities);
}

public sealed class CompressionCodecPolicy : ICompressionCodecPolicy
{
    public string ImageDefault(ToolCapabilities? capabilities) => capabilities?.Jxl == true
        ? "JPEG XL" : capabilities?.Avif == true ? "AVIF" : "Conserver";
    public string VideoDefault(ToolCapabilities? capabilities) => capabilities?.Av1Encoder != null ? "AV1" : "Conserver";
}
