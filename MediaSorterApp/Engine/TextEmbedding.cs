using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MediaSorter.Engine;

/// <summary>
/// Similarity model opened for one query (the typed show name).
/// <see cref="Score"/> compares an arbitrary text against the query,
/// <see cref="Pair"/> compares two texts with each other (used for clustering).
/// Both return 0..1 where 1 means "identical meaning".
/// </summary>
public interface ISimilarityModel
{
    double Score(string text);
    double Pair(string a, string b);
}

public interface ISimilarityFactory
{
    /// <summary>Human-readable model description (shown in the chat when a fallback is used).</summary>
    string Name { get; }

    ISimilarityModel Open(string query);
}

/// <summary>Entry point for the app: loads the local embedding model, falls back to fuzzy matching.</summary>
public static class Similarity
{
    public const string ModelFileName = "encoder.onnx";
    public const string VocabFileName = "vocab.txt";

    /// <summary>Loads the MiniLM model from <paramref name="modelsDirectory"/>; fuzzy fallback when unavailable.</summary>
    public static (ISimilarityFactory Factory, string? Warning) Load(string modelsDirectory)
    {
        var modelPath = Path.Combine(modelsDirectory, ModelFileName);
        var vocabPath = Path.Combine(modelsDirectory, VocabFileName);

        var factory = MiniLmSimilarityFactory.TryLoad(modelPath, vocabPath, out var error);
        if (factory is not null)
            return (factory, null);

        return (new FuzzySimilarityFactory(), $"Embedding model unavailable ({error}). Using plain fuzzy matching instead.");
    }
}

/// <summary>
/// Sentence embedding with a quantized MiniLM-L6 model (ONNX Runtime, CPU, fully offline).
/// Mean-pools the transformer output and L2-normalizes it, so similarity is a plain dot product.
/// </summary>
public sealed class MiniLmSimilarityFactory : ISimilarityFactory, IDisposable
{
    /// <summary>WordPiece budget including [CLS]/[SEP]; show titles are far shorter than this.</summary>
    public const int MaxTokens = 128;

    private const string Cls = "[CLS]";
    private const string Sep = "[SEP]";
    private const string Unk = "[UNK]";

    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _vocab;
    private readonly string _inputIdsName;
    private readonly string _attentionName;
    private readonly string? _tokenTypeName;
    private readonly string _outputName;

    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    public string Name { get; }

    private MiniLmSimilarityFactory(InferenceSession session, Dictionary<string, int> vocab, string name)
    {
        _session = session;
        _vocab = vocab;
        Name = name;

        _inputIdsName = session.InputNames.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase))
                        ?? session.InputNames[0];
        _attentionName = session.InputNames.FirstOrDefault(n => n.Contains("attention_mask", StringComparison.OrdinalIgnoreCase))
                         ?? session.InputNames[^1];
        _tokenTypeName = session.InputNames.FirstOrDefault(n => n.Contains("token_type", StringComparison.OrdinalIgnoreCase));
        _outputName = session.OutputNames.FirstOrDefault(n => n.Equals("last_hidden_state", StringComparison.OrdinalIgnoreCase))
                      ?? session.OutputNames[0];
    }

    public static MiniLmSimilarityFactory? TryLoad(string modelPath, string vocabPath, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(modelPath))
            {
                error = $"model not found: {modelPath}";
                return null;
            }

            if (!File.Exists(vocabPath))
            {
                error = $"vocab not found: {vocabPath}";
                return null;
            }

            var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            foreach (var line in File.ReadLines(vocabPath))
            {
                if (!vocab.ContainsKey(line))
                    vocab[line] = index;
                index++;
            }

            var options = new SessionOptions
            {
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            var session = new InferenceSession(modelPath, options);

            var size = new FileInfo(modelPath).Length;
            return new MiniLmSimilarityFactory(session, vocab,
                $"local MiniLM embedder ({size / (1024.0 * 1024.0):0.0} MB, offline)");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public ISimilarityModel Open(string query) => new Session(this, Encode(query));

    public void Dispose()
    {
        _session.Dispose();
        _cache.Clear();
    }

    // ----------------------------------------------------------------- encoding

    /// <summary>Encodes text to an L2-normalized 384-dimension vector.</summary>
    public float[] Encode(string text) => _cache.GetOrAdd(text ?? "", EncodeCore);

    private float[] EncodeCore(string text)
    {
        var ids = ToIds(text);

        var inputIds = new DenseTensor<long>(new[] { 1, ids.Count });
        var mask = new DenseTensor<long>(new[] { 1, ids.Count });

        for (var i = 0; i < ids.Count; i++)
        {
            inputIds[0, i] = ids[i];
            mask[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionName, mask)
        };

        if (_tokenTypeName is not null)
        {
            var types = new DenseTensor<long>(new[] { 1, ids.Count });
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeName, types));
        }

        float[] vector;

        using (var results = _session.Run(inputs))
        {
            var hidden = results.First(r => r.Name == _outputName).AsTensor<float>();
            var hiddenSize = hidden.Dimensions[^1];
            var tokens = hidden.Dimensions[1];

            vector = new float[hiddenSize];

            // Mean pooling over the real tokens (batch is always 1, no padding to skip).
            for (var t = 0; t < tokens; t++)
            {
                for (var d = 0; d < hiddenSize; d++)
                    vector[d] += hidden[0, t, d];
            }

            var denominator = Math.Max(1, tokens);
            for (var d = 0; d < hiddenSize; d++)
                vector[d] /= denominator;
        }

        Normalize(vector);
        return vector;
    }

    /// <summary>Greedy longest-match WordPiece with "##" continuations; unknown words become [UNK].</summary>
    private List<string> WordPiece(List<string> tokens)
    {
        var result = new List<string>(tokens.Count);

        foreach (var token in tokens)
        {
            if (token.Length == 0)
                continue;

            var pieces = new List<string>();
            var start = 0;
            var outOfVocab = false;

            while (start < token.Length)
            {
                var end = token.Length;
                string? found = null;

                while (start < end)
                {
                    var candidate = token.Substring(start, end - start);
                    if (start > 0)
                        candidate = "##" + candidate;

                    if (_vocab.ContainsKey(candidate))
                    {
                        found = candidate;
                        break;
                    }

                    end--;
                }

                if (found is null)
                {
                    outOfVocab = true;
                    break;
                }

                pieces.Add(found);
                start = end;
            }

            if (outOfVocab || pieces.Count == 0)
                result.Add(Unk);
            else
                result.AddRange(pieces);
        }

        return result;
    }

    private List<int> ToIds(string text)
    {
        var tokens = WordPiece(BasicTokenize(text));

        if (tokens.Count > MaxTokens - 2)
            tokens.RemoveRange(MaxTokens - 2, tokens.Count - (MaxTokens - 2));

        var ids = new List<int>(tokens.Count + 2) { Id(Cls) };

        foreach (var token in tokens)
            ids.Add(Id(token));

        ids.Add(Id(Sep));
        return ids;

        int Id(string token) => _vocab.TryGetValue(token, out var i) ? i : _vocab[Unk];
    }

    /// <summary>BERT uncased basic tokenization: CJK padding, lowercase, accent stripping, punctuation splitting.</summary>
    private static List<string> BasicTokenize(string text)
    {
        var padded = PadCjk(text);
        var result = new List<string>(padded.Length / 4 + 4);

        foreach (var rawToken in padded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var lowered = StripAccents(rawToken.ToLowerInvariant());

            foreach (var piece in SplitPunctuation(lowered))
            {
                if (piece.Length > 0)
                    result.Add(piece);
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitPunctuation(string token)
    {
        var buffer = new System.Text.StringBuilder();

        foreach (var c in token)
        {
            if (IsBertPunctuation(c))
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                yield return c.ToString();
            }
            else
            {
                buffer.Append(c);
            }
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    private static bool IsBertPunctuation(char c)
    {
        // ASCII symbol ranges, exactly like the reference BERT tokenizer.
        if (c >= 33 && c <= 47 || c >= 58 && c <= 64 || c >= 91 && c <= 96 || c >= 123 && c <= 126)
            return true;

        return CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.ClosePunctuation
            or UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.OpenPunctuation;
    }

    private static string StripAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Surrounds CJK ideographs with spaces so they tokenize one character at a time.</summary>
    private static string PadCjk(string text)
    {
        if (!text.Any(IsCjkIdeograph))
            return text;

        var sb = new System.Text.StringBuilder(text.Length + 8);

        foreach (var c in text)
            sb.Append(IsCjkIdeograph(c) ? $" {c} " : c.ToString());

        return sb.ToString();
    }

    private static bool IsCjkIdeograph(char c)
    {
        int cp = c;
        return cp is (>= 0x4E00 and <= 0x9FFF)   // CJK unified ideographs
            or (>= 0x3400 and <= 0x4DBF)          // extension A
            or (>= 0xF900 and <= 0xFAFF);         // compatibility ideographs
    }

    // ------------------------------------------------------------------ helpers

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
            sum += v * v;

        if (sum <= 1e-12)
            return;

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
            vector[i] /= norm;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        var count = Math.Min(a.Length, b.Length);

        for (var i = 0; i < count; i++)
            sum += a[i] * b[i];

        return sum;
    }

    private sealed class Session : ISimilarityModel
    {
        private readonly MiniLmSimilarityFactory _factory;
        private readonly float[] _query;

        public Session(MiniLmSimilarityFactory factory, float[] query)
        {
            _factory = factory;
            _query = query;
        }

        public double Score(string text) => Similarity01(Dot(_query, _factory.Encode(text ?? "")));

        public double Pair(string a, string b) =>
            Similarity01(Dot(_factory.Encode(a ?? ""), _factory.Encode(b ?? "")));

        private static double Similarity01(double dot) => Math.Clamp(dot, 0.0, 1.0);
    }
}

/// <summary>
/// Fallback used when the model files are missing or unloadable: token-set fuzzy scores
/// remapped onto roughly the same 0..1 range as the embedder, so the selector thresholds
/// stay meaningful (raw 72 ≈ include, raw 67 ≈ review, below 50 ≈ exclude).
/// </summary>
public sealed class FuzzySimilarityFactory : ISimilarityFactory
{
    public string Name => "token-set fuzzy fallback (embedding model not loaded)";

    public ISimilarityModel Open(string query) => new Session(query);

    private sealed class Session(string query) : ISimilarityModel
    {
        public double Score(string text) => Scale(FolderMatcher.Score(query, text));

        public double Pair(string a, string b) => Scale(FolderMatcher.Score(a, b));

        private static double Scale(int raw) => Math.Clamp((raw - 50) / 50.0, 0.0, 1.0);
    }
}
