using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace PixSeek.Services;

public interface IImagePreviewDecoder
{
    Bitmap Decode(string path);
}

public sealed class ImagePreviewDecoder : IImagePreviewDecoder
{
    public Bitmap Decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unsupported image.");
        if ((long)codec.Info.Width * codec.Info.Height > 64_000_000)
            throw new InvalidDataException("Image exceeds the direct preview memory budget.");
        var scale = Math.Min(1f, 4096f / Math.Max(codec.Info.Width, codec.Info.Height));
        var dimensions = codec.GetScaledDimensions(scale);
        using var decoded = new SKBitmap(new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException(result.ToString());
        var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft && Math.Max(decoded.Width, decoded.Height) <= 4096)
            return Copy(decoded);
        // PNG and several other codecs ignore GetScaledDimensions: cap the actual result as well.
        var width = swap ? decoded.Height : decoded.Width;
        var height = swap ? decoded.Width : decoded.Height;
        var outputScale = Math.Min(1d, 4096d / Math.Max(width, height));
        using var oriented = new SKBitmap(Math.Max(1, (int)Math.Round(width * outputScale)), Math.Max(1, (int)Math.Round(height * outputScale)), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(oriented);
        canvas.Scale((float)oriented.Width / width, (float)oriented.Height / height);
        switch (codec.EncodedOrigin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(decoded.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(decoded.Width, decoded.Height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, decoded.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(decoded.Height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(decoded.Height, decoded.Width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, decoded.Width);
                canvas.RotateDegrees(270);
                break;
        }

        using var image = SKImage.FromBitmap(decoded);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        return Copy(oriented);
    }

    private static Bitmap Copy(SKBitmap bitmap) => new(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);

}
