using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixSeek.Core;
using System.Security.Cryptography;
using System.Text;

namespace PixSeek.AI;

/// <summary>CPU YuNet detector and SFace recognizer. No names are inferred by the models.</summary>
public sealed class FaceEmbeddingService(FaceModelManager models, IImageTensorSource images, IPlatformPaths paths) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private InferenceSession? _detector, _recognizer;
    public string Version => models.Version;
    public const int DetectorSize = 640;
    public const double MatchThreshold = .45;
    public async Task<IReadOnlyList<DetectedFace>> DetectAsync(string imagePath, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            await models.EnsureAsync(null, ct);
            await _gate.WaitAsync(ct);
            try
            {
                using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                _detector ??= new(Path.Combine(models.DirectoryPath, models.Manifest.Files[0].Path), options);
                _recognizer ??= new(Path.Combine(models.DirectoryPath, models.Manifest.Files[1].Path), options);
                var size = _detector.InputMetadata.Values.First().Dimensions[^1];
                if (size != DetectorSize) throw new InvalidDataException("Unexpected YuNet input size: " + size);
                var rgb = await images.ReadFaceRgbAsync(imagePath, size, ct);
                var tensor = new float[size * size * 3];
                for (var i = 0; i < size * size; i++)
                    for (var c = 0; c < 3; c++) tensor[c * size * size + i] = rgb[i * 3 + 2 - c]; // YuNet BGR [0,255].
                using var output = _detector.Run([NamedOnnxValue.CreateFromTensor(_detector.InputMetadata.Keys.First(), new DenseTensor<float>(tensor, [1, 3, size, size]))]);
                var maps = output.ToDictionary(o => o.Name, o => o.AsTensor<float>().ToArray());
                var boxes = new List<Proposal>();
                foreach (var stride in new[] { 8, 16, 32 })
                {
                    var cls = maps["cls_" + stride]; var obj = maps["obj_" + stride];
                    var bbox = maps["bbox_" + stride]; var landmarks = maps["kps_" + stride];
                    var columns = size / stride;
                    for (var i = 0; i < cls.Length; i++)
                    {
                        var score = Math.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                        if (score < .85) continue;
                        var x = i % columns; var y = i / columns;
                        var width = Math.Exp(bbox[i * 4 + 2]) * stride;
                        var height = Math.Exp(bbox[i * 4 + 3]) * stride;
                        if (!double.IsFinite(width + height) || width < 12 || height < 12) continue;
                        var cx = (x + bbox[i * 4]) * stride; var cy = (y + bbox[i * 4 + 1]) * stride;
                        var points = Enumerable.Range(0, 5).Select(n => ((double)(landmarks[i * 10 + n * 2] + x) * stride,
                            (double)(landmarks[i * 10 + n * 2 + 1] + y) * stride)).ToArray();
                        boxes.Add(new(cx - width / 2, cy - height / 2, width, height, score, points));
                    }
                }
                var kept = new List<Proposal>();
                foreach (var proposal in boxes.OrderByDescending(b => b.Score).Take(5000))
                    if (kept.All(other => Intersection(proposal, other) < .3))
                    {
                        kept.Add(proposal);
                        if (kept.Count == 100) break;
                    }
                var faces = new List<DetectedFace>();
                foreach (var face in kept)
                {
                    ct.ThrowIfCancellationRequested();
                    var aligned = Align(rgb, size, face.Points);
                    var input = new float[112 * 112 * 3];
                    for (var i = 0; i < 112 * 112; i++)
                        for (var c = 0; c < 3; c++) input[c * 112 * 112 + i] = aligned[i * 3 + c];
                    using var encoded = _recognizer.Run([NamedOnnxValue.CreateFromTensor(_recognizer.InputMetadata.Keys.First(), new DenseTensor<float>(input, [1, 3, 112, 112]))]);
                    var vector = encoded.First().AsTensor<float>().ToArray();
                    var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
                    if (vector.Length != 128 || !double.IsFinite(norm) || norm < 1e-10) throw new InvalidDataException("Invalid SFace embedding.");
                    for (var i = 0; i < vector.Length; i++) vector[i] /= (float)norm;
                    var key = Convert.ToHexString(SHA256.HashData(aligned));
                    var thumbnail = Path.Combine(paths.Cache, "face-" + key + ".jpg");
                    if (!File.Exists(thumbnail))
                    {
                        await images.WriteFaceThumbnailAsync(aligned, 112, thumbnail, ct);
                    }
                    faces.Add(new(imagePath, thumbnail, vector, face.Score, face.X, face.Y, face.Width, face.Height));
                }
                return (IReadOnlyList<DetectedFace>)faces;
            }
            finally { _gate.Release(); }
        }, ct);
    }

    private sealed record Proposal(double X, double Y, double Width, double Height, double Score, (double X, double Y)[] Points);
    private static double Intersection(Proposal a, Proposal b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Max(1, a.Width * a.Height + b.Width * b.Height - intersection);
    }

    // Least-squares similarity transform from five landmarks; inverse bilinear sampling onto RGB112.
    private static byte[] Align(byte[] source, int size, (double X, double Y)[] points)
    {
        (double X, double Y)[] target = [(38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366), (41.5493, 92.3655), (70.7299, 92.2041)];
        var sx = points.Average(p => p.X); var sy = points.Average(p => p.Y);
        var dx = target.Average(p => p.X); var dy = target.Average(p => p.Y);
        double denominator = 0, real = 0, imaginary = 0;
        for (var i = 0; i < 5; i++)
        {
            var x = points[i].X - sx; var y = points[i].Y - sy;
            var u = target[i].X - dx; var v = target[i].Y - dy;
            denominator += x * x + y * y;
            real += x * u + y * v; imaginary += x * v - y * u;
        }
        if (denominator < 1e-8) throw new InvalidDataException("Degenerate facial landmarks.");
        var a = real / denominator; var b = imaginary / denominator;
        var tx = dx - a * sx + b * sy; var ty = dy - b * sx - a * sy;
        var magnitude = a * a + b * b;
        if (magnitude < 1e-10) throw new InvalidDataException("Degenerate face alignment.");
        var result = new byte[112 * 112 * 3];
        for (var y = 0; y < 112; y++)
            for (var x = 0; x < 112; x++)
            {
                var px = (a * (x - tx) + b * (y - ty)) / magnitude;
                var py = (a * (y - ty) - b * (x - tx)) / magnitude;
                var ix = (int)Math.Floor(px); var iy = (int)Math.Floor(py);
                var fx = px - ix; var fy = py - iy;
                for (var c = 0; c < 3; c++)
                {
                    double value = 0;
                    for (var oy = 0; oy < 2; oy++)
                        for (var ox = 0; ox < 2; ox++)
                        {
                            var xx = ix + ox; var yy = iy + oy;
                            if (xx >= 0 && xx < size && yy >= 0 && yy < size)
                                value += source[(yy * size + xx) * 3 + c] * (ox == 0 ? 1 - fx : fx) * (oy == 0 ? 1 - fy : fy);
                        }
                    result[(y * 112 + x) * 3 + c] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                }
            }
        return result;
    }
    public void Dispose() { _detector?.Dispose(); _recognizer?.Dispose(); _gate.Dispose(); }
}
