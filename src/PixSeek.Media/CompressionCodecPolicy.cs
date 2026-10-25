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
    public string ImageDefault(ToolCapabilities? capabilities) => CompressionCodecs.Auto;
    public string VideoDefault(ToolCapabilities? capabilities) => CompressionCodecs.Auto;
}
