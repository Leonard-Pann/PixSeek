namespace PixSeek.Core;

/// <summary>Platform decoder for semantic and face inference. Buffers are RGB, 8 bits per channel.</summary>
public interface IImageTensorSource
{
    Task<byte[]> ReadModelRgbAsync(string file, CancellationToken ct, bool squash = false);
    Task<byte[]> ReadFaceRgbAsync(string file, int size, CancellationToken ct);
    Task WriteFaceThumbnailAsync(byte[] rgb, int size, string destination, CancellationToken ct);
}
