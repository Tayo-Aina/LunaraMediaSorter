using System.Collections.Generic;
using FuzzySharp;

namespace MediaSorter.Engine;

/// <summary>
/// Lightweight token-set fuzzy matching (FuzzySharp) used to line parsed titles up with
/// folders that already exist on disk, so we reuse them instead of creating duplicates.
/// </summary>
public static class FolderMatcher
{
    /// <summary>Below this, two names are treated as different shows.</summary>
    public const int MatchThreshold = 85;

    /// <summary>Below this, a parsed title is considered suspicious against the target show name.</summary>
    public const int SanityThreshold = 55;

    public readonly record struct Match(string? Folder, int Score);

    /// <summary>
    /// Best token-set match for <paramref name="candidate"/> among <paramref name="folders"/>.
    /// Requires both a high token-set score and that the tokens actually overlap, which stops
    /// "Show" from being matched to an unrelated folder such as "Showdown".
    /// </summary>
    public static Match BestMatch(string candidate, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return new Match(null, 0);

        var candidateTokens = Tokenize(candidate);
        string? best = null;
        var bestScore = 0;

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            // Case-insensitive: Windows folders are ("bluey" and "Bluey" are the same
            // folder), and FuzzySharp's raw ratio charges for a case difference — enough
            // to push short names ("Bluey" vs "bluey" = 80) below the threshold.
            var score = Fuzz.TokenSetRatio(candidate.ToLowerInvariant(), folder.ToLowerInvariant());
            if (score < MatchThreshold || score <= bestScore)
                continue;

            if (SharedTokenRatio(candidateTokens, Tokenize(folder)) < 0.6)
                continue;

            bestScore = score;
            best = folder;
        }

        return new Match(best, bestScore);
    }

    /// <summary>Raw token-set score between two labels (0-100). Used for sanity warnings.</summary>
    public static int Score(string candidate, string other)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(other))
            return 0;

        // Guard the degenerate case where the two labels share no words at all.
        if (SharedTokenRatio(Tokenize(candidate), Tokenize(other)) == 0)
            return 0;

        return Fuzz.TokenSetRatio(candidate.ToLowerInvariant(), other.ToLowerInvariant());
    }

    private static double SharedTokenRatio(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var shared = 0;
        foreach (var token in a)
        {
            if (b.Contains(token))
                shared++;
        }

        var smallest = Math.Min(a.Count, b.Count);
        return (double)shared / smallest;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in value.Split(new[] { ' ', '.', '_', '-', '(', ')', '[', ']', ',', ':', '/' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 0)
                tokens.Add(token);
        }

        return tokens;
    }
}
