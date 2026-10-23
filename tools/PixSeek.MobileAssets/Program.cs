using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using HfTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

if (args.Length != 2) throw new ArgumentException("tokenizer.json sentencepiece.model");
using var json = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = json.RootElement;
if (root.GetProperty("model").GetProperty("type").GetString() != "Unigram") throw new InvalidDataException("Expected XLM-R Unigram.");
using var model = new MemoryStream();
var vocab = root.GetProperty("model").GetProperty("vocab").EnumerateArray().ToArray();
for (var i = 0; i < vocab.Length; i++)
{
    using var piece = new MemoryStream();
    Bytes(piece, 1, Encoding.UTF8.GetBytes(vocab[i][0].GetString()!));
    Varint(piece, (2 << 3) | 5);
    piece.Write(BitConverter.GetBytes(vocab[i][1].GetSingle()));
    Number(piece, 3, i == 3 ? 2u : i < 4 ? 3u : i == vocab.Length - 1 ? 4u : 1u);
    Bytes(model, 1, piece.ToArray());
}
using (var trainer = new MemoryStream())
{
    Number(trainer, 3, 1); Number(trainer, 4, (uint)vocab.Length);
    Number(trainer, 40, 3); Number(trainer, 41, 0); Number(trainer, 42, 2); Number(trainer, 43, 1);
    Bytes(model, 2, trainer.ToArray());
}
using (var normalizer = new MemoryStream())
{
    Bytes(normalizer, 1, Encoding.UTF8.GetBytes("precompiled-xlmr"));
    Bytes(normalizer, 2, Convert.FromBase64String(root.GetProperty("normalizer").GetProperty("precompiled_charsmap").GetString()!));
    Number(normalizer, 3, 1); Number(normalizer, 4, 1); Number(normalizer, 5, 1);
    Bytes(model, 3, normalizer.ToArray());
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllBytes(args[1], model.ToArray());
model.Position = 0;
var special = root.GetProperty("added_tokens").EnumerateArray().ToDictionary(t => t.GetProperty("content").GetString()!, t => t.GetProperty("id").GetInt32());
var managed = SentencePieceTokenizer.Create(model, true, true, special);
using var reference = HfTokenizer.FromFile(args[0]);
string[] texts = ["un chien", "une tartiflette", "un chien dans la neige", "des personnes autour d'une table", "vacances à la plage", "café été Noël", "  un  chat \n dans\tla forêt  ", "a dog on the beach", "una persona in montagna", "ein Hund im Schnee", "un perro en la playa", "一只狗在雪地里", "Красная машина", "👩🏽‍💻 coucher de soleil", "１２３ ＡＢＣ", "hello\u00a0world", "<s> dog </s>", "photo <mask>", "image_2026-10-22.jpg", "👨‍👩‍👧‍👦", "\u0301é e\u0301"];
foreach (var text in texts)
{
    var expected = reference.Encode(text, true).First().Ids.Select(x => (int)x).ToArray();
    var actual = managed.EncodeToIds(text).ToArray();
    if (!expected.SequenceEqual(actual)) throw new InvalidDataException($"Tokenizer mismatch '{text}': HF={string.Join(',', expected)}; managed={string.Join(',', actual)}");
}
Console.WriteLine($"Managed Unigram parity: {texts.Length} multilingual/normalization cases, {vocab.Length} tokens.");

static void Varint(Stream s, uint value) { while (value >= 128) { s.WriteByte((byte)(value | 128)); value >>= 7; } s.WriteByte((byte)value); }
static void Number(Stream s, int field, uint value) { Varint(s, (uint)(field << 3)); Varint(s, value); }
static void Bytes(Stream s, int field, byte[] data) { Varint(s, (uint)((field << 3) | 2)); Varint(s, (uint)data.Length); s.Write(data); }
