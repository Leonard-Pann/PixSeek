using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using PixSeek.AI;
using PixSeek.Core;

namespace PixSeek.Mobile;

/// <summary>The same multilingual CLIP space, with a fully managed SentencePiece Unigram tokenizer.</summary>
public sealed class MobileEmbeddingService(ModelManager models, IImageTensorSource images) : ILocalEmbeddingService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private InferenceSession? _visual, _textual;
    private SentencePieceTokenizer? _tokenizer;
    public bool IsReady => _visual != null && _textual != null && _tokenizer != null;
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsReady) return;
            OrtEnv.Instance().DisableTelemetryEvents();
            await models.ValidateAsync(ct);
            await Task.Run(() =>
            {
                using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 3), InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                try
                {
                    _visual = new(Path.Combine(models.DirectoryPath, "visual/model.onnx"), options);
                    _textual = new(Path.Combine(models.DirectoryPath, "textual/model.onnx"), options);
                    using var input = File.OpenRead(Path.Combine(models.DirectoryPath, "textual/sentencepiece.model"));
                    _tokenizer = SentencePieceTokenizer.Create(input, true, true, new Dictionary<string, int> { ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3, ["<mask>"] = 250001 });
                    if (!_visual.OutputMetadata.Values.Any(x => x.Dimensions.LastOrDefault() == models.Manifest.Dimensions)) throw new InvalidDataException("Invalid visual model dimensions.");
                }
                catch { Release(); throw; }
            }, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task<float[]> GenerateImageEmbeddingAsync(string uri, CancellationToken ct)
    {
        await InitializeAsync(ct);
        var rgb = await images.ReadModelRgbAsync(uri, ct);
        var values = EmbeddingMath.PreprocessRgb(rgb);
        await _gate.WaitAsync(ct);
        try { return await Task.Run(() => Run(_visual!, [NamedOnnxValue.CreateFromTensor(_visual!.InputMetadata.Keys.First(), new DenseTensor<float>(values, [1, 3, 224, 224]))], ct), ct); }
        finally { _gate.Release(); }
    }
    public async Task<float[]> GenerateTextEmbeddingAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Empty search.");
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var ids = _tokenizer!.EncodeToIds(text).Take(77).Select(x => (long)x).ToArray();
                ids[^1] = 2;
                var fixedLength = _textual!.InputMetadata.Values.First().Dimensions.Last();
                var length = fixedLength > 0 ? fixedLength : ids.Length;
                var tokens = Enumerable.Repeat(1L, length).ToArray();
                Array.Copy(ids, tokens, Math.Min(length, ids.Length));
                var mask = Enumerable.Range(0, length).Select(i => i < ids.Length ? 1L : 0L).ToArray();
                var inputs = _textual.InputMetadata.Keys.Select(name =>
                {
                    var values = name.Contains("mask", StringComparison.Ordinal) ? mask : tokens;
                    return _textual.InputMetadata[name].ElementType == typeof(int)
                        ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(values.Select(v => (int)v).ToArray(), [1, length]))
                        : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, [1, length]));
                }).ToArray();
                return Run(_textual, inputs, ct);
            }, ct);
        }
        finally { _gate.Release(); }
    }
    private float[] Run(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken ct)
    {
        using var options = new RunOptions();
        using var cancel = ct.Register(() => options.Terminate = true);
        using var result = session.Run(inputs, session.OutputMetadata.Keys.ToArray(), options);
        ct.ThrowIfCancellationRequested();
        return EmbeddingMath.Normalize(result.First(x => x.AsTensor<float>().Length == models.Manifest.Dimensions).AsTensor<float>().ToArray());
    }
    private void Release() { _visual?.Dispose(); _textual?.Dispose(); _visual = _textual = null; _tokenizer = null; }
    public void Dispose() { Release(); _gate.Dispose(); }
}
