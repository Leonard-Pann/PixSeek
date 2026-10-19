using PixSeek.Core;
using PixSeek.Media;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

namespace PixSeek.AI;
public interface IExecutionProviderFactory
{
    SessionOptions Create();
    string Name { get; }
}

public sealed class CpuExecutionProviderFactory : IExecutionProviderFactory
{
    public string Name => "CPU · ONNX Runtime";

    public SessionOptions Create() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        InterOpNumThreads = 1
    };
}

public sealed class OnnxEmbeddingService(ModelManager models, ImageMagickService images, IExecutionProviderFactory provider) : IEmbeddingService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _image, _text;
    private Tokenizer? _tokenizer;
    public bool IsReady => _image != null && _text != null && _tokenizer != null;
    public string Backend => provider.Name;
    public string InputDescription => IsReady ? "Image: " + Describe(_image!) + "; Texte: " + Describe(_text!) : "Non chargé";

    private static string Describe(InferenceSession s) => string.Join(", ", s.InputMetadata.Select(x => x.Key + "[" + string.Join(',', x.Value.Dimensions) + "] " + x.Value.ElementType.Name));
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsReady)
                return;
            await models.ValidateAsync(ct);
            await Task.Run(() =>
            {
                using var opts = provider.Create();
                InferenceSession? image = null, text = null;
                Tokenizer? tokenizer = null;
                try
                {
                    image = new(Path.Combine(models.DirectoryPath, "visual", "model.onnx"), opts);
                    ct.ThrowIfCancellationRequested();
                    text = new(Path.Combine(models.DirectoryPath, "textual", "model.onnx"), opts);
                    ct.ThrowIfCancellationRequested();
                    tokenizer = Tokenizer.FromFile(Path.Combine(models.DirectoryPath, "textual", "tokenizer.json"));
                    if (!image.OutputMetadata.Values.Any(x => x.Dimensions.LastOrDefault() == models.Manifest.Dimensions) || !text.OutputMetadata.Values.Any(x => x.Dimensions.LastOrDefault() == models.Manifest.Dimensions))
                        throw new InvalidDataException("Dimensions ONNX incompatibles avec le manifeste.");
                    _image = image;
                    _text = text;
                    _tokenizer = tokenizer;
                }
                catch
                {
                    image?.Dispose();
                    text?.Dispose();
                    tokenizer?.Dispose();
                    throw;
                }
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static float[] PreprocessRgb(ReadOnlySpan<byte> rgb)
    {
        const int pixels = 224 * 224;
        if (rgb.Length != pixels * 3)
            throw new ArgumentException("Une image RGB 224 × 224 est requise.");
        float[] mean = [.48145466f, .4578275f, .40821073f], std = [.26862954f, .26130258f, .27577711f];
        var tensor = new float[pixels * 3];
        for (var i = 0; i < pixels; i++)
            for (var c = 0; c < 3; c++)
                tensor[c * pixels + i] = (rgb[i * 3 + c] / 255f - mean[c]) / std[c];
        return tensor;
    }

    public async Task<float[]> GenerateImageEmbeddingAsync(string imagePath, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var rgb = await images.ReadModelRgbAsync(imagePath, cancellationToken);
        var data = PreprocessRgb(rgb);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Run(_image!, [NamedOnnxValue.CreateFromTensor(_image!.InputMetadata.Keys.First(), new DenseTensor<float>(data, [1, 3, 224, 224]))], cancellationToken), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<float[]> GenerateTextEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Saisissez une recherche.");
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                // The exact upstream XLM-R Unigram tokenizer includes normalization and special tokens.
                var encoded = _tokenizer!.Encode(text, true).First();
                var ids = encoded.Ids.Select(x => (long)x).ToArray();
                var fixedLength = _text!.InputMetadata.Values.First().Dimensions.Last();
                var length = fixedLength > 0 ? fixedLength : Math.Min(77, ids.Length);
                if (ids.Length > length)
                {
                    ids = ids[..length];
                    ids[^1] = 2;
                }

                var tokens = Enumerable.Repeat(1L, length).ToArray();
                Array.Copy(ids, tokens, ids.Length);
                var mask = Enumerable.Range(0, length).Select(i => i < ids.Length ? 1L : 0L).ToArray();
                var inputs = new List<NamedOnnxValue>();
                foreach (var name in _text.InputMetadata.Keys)
                {
                    var values = name.Contains("mask", StringComparison.Ordinal) ? mask : name.Contains("token_type", StringComparison.Ordinal) ? new long[length] : tokens;
                    inputs.Add(_text.InputMetadata[name].ElementType == typeof(int) ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(values.Select(x => (int)x).ToArray(), [1, length])) : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, [1, length])));
                }

                return Run(_text, inputs, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private float[] Run(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken ct)
    {
        using var options = new RunOptions();
        using var cancel = ct.Register(() => options.Terminate = true);
        using var result = session.Run(inputs, session.OutputMetadata.Keys.ToArray(), options);
        ct.ThrowIfCancellationRequested();
        var output = result.First(x => x.AsTensor<float>().Length == models.Manifest.Dimensions).AsTensor<float>().ToArray();
        return Normalize(output);
    }

    public static float[] Normalize(float[] values)
    {
        var norm = Math.Sqrt(values.Sum(x => (double)x * x));
        if (!double.IsFinite(norm) || norm < 1e-10)
            throw new InvalidDataException("L'encodeur a produit un vecteur invalide.");
        for (var i = 0; i < values.Length; i++)
            values[i] = (float)(values[i] / norm);
        return values;
    }

    public void Dispose()
    {
        _image?.Dispose();
        _text?.Dispose();
        _tokenizer?.Dispose();
        _gate.Dispose();
    }
}
