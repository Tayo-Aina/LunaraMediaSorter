using System.Collections.Generic;
using System.IO;
using FuzzySharp;

namespace MediaSorter.Engine;

/// <summary>Whether a scanned file belongs to the show name the user typed.</summary>
public enum FileVerdict
{
    /// <summary>Strong (or corroborated) match — goes into the plan.</summary>
    Include,

    /// <summary>Ambiguous — a single grouped prompt asks the human.</summary>
    Review,

    /// <summary>Looks like a different show — left untouched, listed with a reason.</summary>
    Exclude
}

/// <summary>Per-file outcome of the selection gate.</summary>
public sealed class FileSelection
{
    public required ParseResult File { get; init; }

    /// <summary>Best similarity between the show name and any text of this file (0..1).</summary>
    public required double Score { get; init; }

    /// <summary>The candidate text that produced <see cref="Score"/> (stem / parsed title / parent folder).</summary>
    public required string MatchedText { get; init; }

    public FileVerdict Verdict { get; set; } = FileVerdict.Exclude;

    /// <summary>Human-readable explanation shown next to skipped files.</summary>
    public string? Reason { get; set; }

    /// <summary>The show name's words appear (fuzzily) in the file's name/title/parent folder.</summary>
    public bool LexicalMatch { get; set; }

    /// <summary>The file's key is directly adjacent to the best-matching file's key.</summary>
    public bool AdjacentToBest { get; set; }

    /// <summary>The file is also closely tied to a name that is NOT part of the best match (conflict).</summary>
    public bool CompetingClaim { get; set; }

    public int Percent => (int)Math.Round(Score * 100);
}

/// <summary>Outcome of running the selection gate over a scanned folder.</summary>
public sealed class SelectionResult
{
    public List<FileSelection> Files { get; } = new();

    /// <summary>Best score across all files — used to detect "nothing here matches".</summary>
    public double BestScore { get; set; }

    public string BestMatchText { get; set; } = "";

    public IEnumerable<FileSelection> Included => Files.Where(f => f.Verdict == FileVerdict.Include);
    public IEnumerable<FileSelection> Reviews => Files.Where(f => f.Verdict == FileVerdict.Review);
    public IEnumerable<FileSelection> Excluded => Files.Where(f => f.Verdict == FileVerdict.Exclude);
}

/// <summary>
/// The selection gate: decides which scanned files actually belong to the typed show name.
/// Three independent signals are combined:
///
///   1. semantic   — embedding similarity of the show name vs filename / parsed title / parent folder.
///   2. lexical    — the show name's words appear (fuzzy, ≥ 80%) in the file's text. This rescues
///                   leetspeak and typo names the embedder scores low ("th3 s1mpsons"), and blocks
///                   famous-but-unrelated shows the embedder scores high ("Futurama" ≈ "The Simpsons").
///   3. conflict   — the file is closely tied to another name in the folder that is not part of the
///                   best match (e.g. "Family Guy - The Simpsons Guy"), which means two shows claim it.
///
/// Semantic-only decisions are never trusted in the middle band: anything uncertain goes to the
/// human as one grouped prompt instead of being silently moved or silently dropped.
/// </summary>
public static class ShowSelector
{
    /// <summary>At or above this (with cluster adjacency), the file belongs to the show without asking.</summary>
    public const double IncludeThreshold = 0.45;

    /// <summary>At or above this (but below include), the human gets asked — one grouped prompt.</summary>
    public const double ReviewThreshold = 0.35;

    /// <summary>Direct key-to-key similarity above which two names count as the same show.</summary>
    public const double ClusterThreshold = 0.70;

    /// <summary>Fuzzy word-match percentage (FuzzySharp) required to count as lexical evidence.</summary>
    public const int LexicalThreshold = 80;

    /// <summary>Even with word-level evidence, semantic similarity below this is too weak to select.</summary>
    public const double LexicalScoreFloor = 0.10;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and"
    };

    public static SelectionResult Select(
        IReadOnlyList<ParseResult> files,
        string showName,
        ISimilarityModel model,
        IProgress<double>? progress = null)
    {
        var result = new SelectionResult();
        if (files.Count == 0)
            return result;

        // ------------------------------------------------- 1. score every file
        var scores = new double[files.Count];
        var matched = new string[files.Count];
        var lexical = new bool[files.Count];

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var stem = Path.GetFileNameWithoutExtension(file.FileName);
            var parent = Path.GetFileName(Path.GetDirectoryName(file.SourcePath) ?? "");

            double best = 0;
            var bestText = stem;
            var allText = stem;

            foreach (var candidate in new[] { stem, file.Title ?? "", parent })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                allText += " " + candidate;
                var score = model.Score(candidate);
                if (score > best)
                {
                    best = score;
                    bestText = candidate;
                }
            }

            scores[i] = best;
            matched[i] = bestText;
            lexical[i] = PassesLexical(showName, allText);

            if (best > result.BestScore)
            {
                result.BestScore = best;
                result.BestMatchText = bestText;
            }

            progress?.Report((i + 1) * 60.0 / files.Count);
        }

        // ------------------------------------------------------ 2. keys + best
        var keys = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var stem = Path.GetFileNameWithoutExtension(files[i].FileName);
            keys[i] = string.IsNullOrWhiteSpace(files[i].Title) ? stem : files[i].Title!;
        }

        var bestIndex = 0;
        for (var i = 1; i < files.Count; i++)
        {
            if (scores[i] > scores[bestIndex])
                bestIndex = i;
        }

        var bestKey = keys[bestIndex];
        progress?.Report(65);

        // -------------------------------------------------- 3. final verdicts
        for (var i = 0; i < files.Count; i++)
        {
            var adjacent = model.Pair(keys[i], bestKey) >= ClusterThreshold;

            // Competing claim: some other name-key is close to this file, but that name
            // itself is not part of the best match — so two different shows want this file.
            var competing = false;
            if (scores[i] >= ReviewThreshold)
            {
                for (var j = 0; j < files.Count && !competing; j++)
                {
                    if (j == i || !keys[j].Any(char.IsLetter))
                        continue;
                    if (model.Pair(keys[i], keys[j]) < ClusterThreshold)
                        continue;
                    if (model.Pair(keys[j], bestKey) >= ClusterThreshold)
                        continue;

                    competing = true;
                }
            }

            var selection = new FileSelection
            {
                File = files[i],
                Score = scores[i],
                MatchedText = matched[i],
                LexicalMatch = lexical[i],
                AdjacentToBest = adjacent,
                CompetingClaim = competing
            };

            var label = string.IsNullOrWhiteSpace(files[i].Title)
                ? Path.GetFileNameWithoutExtension(files[i].FileName)
                : files[i].Title!;

            if (competing && scores[i] >= ReviewThreshold)
            {
                // Two shows claim this file (e.g. a crossover episode) — never decide silently.
                selection.Verdict = FileVerdict.Review;
                selection.Reason = "name matches both this show and another one in the folder";
            }
            else if (adjacent && scores[i] >= IncludeThreshold)
            {
                selection.Verdict = FileVerdict.Include;
            }
            else if (lexical[i] && scores[i] >= LexicalScoreFloor)
            {
                // The show name is literally (fuzzily) in the file's name/title/parent folder.
                selection.Verdict = FileVerdict.Include;
            }
            else if (scores[i] >= ReviewThreshold)
            {
                selection.Verdict = FileVerdict.Review;
            }
            else
            {
                selection.Verdict = FileVerdict.Exclude;
                selection.Reason = $"looks like \"{label}\" ({selection.Percent}%)";
            }

            result.Files.Add(selection);

            if (i % 4 == 0 || i == files.Count - 1)
                progress?.Report(65 + (i + 1) * 35.0 / files.Count);
        }

        progress?.Report(100);
        return result;
    }

    /// <summary>
    /// True when every word of the show name (minus stopwords) appears in <paramref name="text"/>,
    /// matched fuzzily so "kaisen" catches "kaizen" and "simpsons" catches "s1mpsons".
    /// </summary>
    private static bool PassesLexical(string showName, string text)
    {
        var showTokens = Tokenize(showName);
        var required = showTokens.Where(t => !StopWords.Contains(t)).ToList();
        if (required.Count == 0)
            required = showTokens;

        if (required.Count == 0)
            return false;

        var textTokens = Tokenize(text);
        if (textTokens.Count == 0)
            return false;

        foreach (var token in required)
        {
            var found = false;

            foreach (var candidate in textTokens)
            {
                // A tiny token must not match a big word (or vice versa): "s" is a
                // perfect substring of "simpsons", which would make every junk title
                // like "S E" look like a match. Require comparable lengths.
                if (candidate.Length * 4 < token.Length * 3)
                    continue;

                if (Fuzz.PartialRatio(token, candidate) >= LexicalThreshold)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }

    private static List<string> Tokenize(string value)
    {
        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split(new[] { '.', '_', '-', '(', ')', '[', ']', ',', ':', '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.ToLowerInvariant())
            .Where(part => part.Length > 0)
            .ToList();
    }
}
