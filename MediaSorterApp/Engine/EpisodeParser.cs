using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AnitomyParser = global::AnitomySharp.AnitomySharp;
using AnitomyElement = global::AnitomySharp.Element;
using AnitomyCat = global::AnitomySharp.Element.ElementCategory;

namespace MediaSorter.Engine;

/// <summary>
/// Extracts season/episode/title from a video filename.
/// Two deterministic passes: precise C# regex first, Anitomy second, heuristic guess last.
/// </summary>
public static class EpisodeParser
{
    // ---- pass 1: high precision patterns ---------------------------------

    private static readonly Regex SxxEyy =
        new(@"\bS(\d{1,2})\s*[ ._-]?\s*E\s*(\d{1,4})(?:v(\d))?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NxNN =
        new(@"\b(\d{1,2})\s*[xX]\s*(\d{1,4})\b", RegexOptions.Compiled);

    private static readonly Regex SeasonEpisodeWords =
        new(@"\bSeason\s*(\d{1,2})\s*(?:[-–—:.]?\s*)(?:Episode|Ep)\.?\s*(\d{1,4})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EpisodeWord =
        new(@"\b(?:Episode|Ep)\.?\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SingleE =
        new(@"\bE\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---- pass 3: standalone number heuristic -----------------------------

    private static readonly Regex StandaloneNumber =
        new(@"(?<![A-Za-z0-9])(\d{1,4})(?![A-Za-z0-9])", RegexOptions.Compiled);

    private static readonly Regex StripEpisodeSpan =
        new(@"\b(?:S\d{1,2}\s*E\s*\d{1,4}|\d{1,2}\s*[xX]\s*\d{1,4}|Season\s*\d{1,2}|\d{1,4})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OnlyNoise = new(@"^(?:episode|episodes?|video|movie|file|untitled|title|new|final)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParseResult Parse(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        var result = new ParseResult
        {
            SourcePath = sourcePath,
            FileName = fileName,
            Extension = Path.GetExtension(fileName)
        };

        // ---------------------------------------------------------------- pass 1
        var cleaned = FilenameCleaner.Clean(stem);
        foreach (var tag in cleaned.Removed)
            result.StrippedTags.Add(tag);

        int? rxSeason = null, rxEpisode = null;
        string? rxEpisodeToken = null;
        string rxPattern = "";

        var m = SxxEyy.Match(stem);
        if (m.Success)
        {
            rxSeason = ToInt(m.Groups[1].Value);
            rxEpisode = ToInt(m.Groups[2].Value);
            rxEpisodeToken = m.Groups[2].Value;
            rxPattern = "SxxEyy";
        }

        if (!m.Success && (m = NxNN.Match(stem)).Success)
        {
            rxSeason = ToInt(m.Groups[1].Value);
            rxEpisode = ToInt(m.Groups[2].Value);
            rxEpisodeToken = m.Groups[2].Value;
            rxPattern = "1x05";
        }

        if (!m.Success && (m = SeasonEpisodeWords.Match(stem)).Success)
        {
            rxSeason = ToInt(m.Groups[1].Value);
            rxEpisode = ToInt(m.Groups[2].Value);
            rxEpisodeToken = m.Groups[2].Value;
            rxPattern = "SeasonEpisode";
        }

        if (!m.Success && (m = EpisodeWord.Match(stem)).Success)
        {
            rxEpisode = ToInt(m.Groups[1].Value);
            rxEpisodeToken = m.Groups[1].Value;
            rxPattern = "EpisodeWord";
        }

        if (!m.Success && (m = SingleE.Match(stem)).Success)
        {
            rxEpisode = ToInt(m.Groups[1].Value);
            rxEpisodeToken = m.Groups[1].Value;
            rxPattern = "E-prefix";
        }

        // ---------------------------------------------------------------- pass 2
        List<AnitomyElement> elements;
        try
        {
            elements = AnitomyParser.Parse(fileName).ToList();
        }
        catch
        {
            elements = new List<AnitomyElement>();
        }

        string? First(AnitomyCat cat) =>
            elements.FirstOrDefault(e => e.Category == cat)?.Value;

        var anitomyEpisode = elements
            .Where(e => e.Category == AnitomyCat.ElementEpisodeNumber && !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        var anitomyTitle = First(AnitomyCat.ElementAnimeTitle);
        var anitomySeason = First(AnitomyCat.ElementAnimeSeason);
        result.ReleaseGroup = First(AnitomyCat.ElementReleaseGroup);
        if (!string.IsNullOrEmpty(result.ReleaseGroup))
            result.StrippedTags.Add(result.ReleaseGroup!);

        var checksum = First(AnitomyCat.ElementFileChecksum);
        if (!string.IsNullOrEmpty(checksum))
            result.StrippedTags.Add(checksum);

        // ------------------------------------------------------------- assemble
        var season = rxSeason ?? ParseSeason(anitomySeason);

        string? episodeToken = rxEpisodeToken;
        if (episodeToken is null && !string.IsNullOrEmpty(anitomyEpisode))
            episodeToken = anitomyEpisode;

        if (episodeToken is not null)
        {
            result.EpisodeToken = episodeToken;
            result.EpisodeDisplay = FormatEpisode(episodeToken);
            result.EpisodeNumber = ToInt(episodeToken);
        }

        result.Season = season ?? 1;
        if (season is null)
            result.Notes.Add("season defaulted to 1");

        if (rxPattern.Length > 0)
            result.Notes.Add($"matched {rxPattern}");

        // ---------------------------------------------------------- confidence
        if (rxEpisode is not null)
        {
            result.Confidence = ParseConfidence.High;
        }
        else if (!string.IsNullOrEmpty(anitomyEpisode) && HasUsableTitle(anitomyTitle))
        {
            result.Confidence = ParseConfidence.High;
        }
        else if (!string.IsNullOrEmpty(anitomyEpisode))
        {
            result.Confidence = ParseConfidence.Medium;
        }
        else
        {
            // Pass 3: fall back to a standalone number in the (cleaned) name.
            var guess = FindStandaloneEpisode(cleaned.Text);
            if (guess is not null)
            {
                result.EpisodeToken = guess;
                result.EpisodeDisplay = FormatEpisode(guess);
                result.EpisodeNumber = ToInt(guess);
                result.Notes.Add("episode read heuristically");
            }

            result.Confidence = ParseConfidence.Low;
        }

        // --------------------------------------------------------------- title
        result.Title = HasUsableTitle(anitomyTitle)
            ? anitomyTitle!.Trim()
            : DeriveTitle(cleaned.Text, episodeToken);

        if (result.NeedsConfirmation)
            result.GuessLabel = result.EpisodeDisplay.Length == 0
                ? null
                : $"Season {result.Season} Episode {result.EpisodeDisplay}";

        return result;
    }

    /// <summary>Called by the chat flow once the human confirmed the automatic guess.</summary>
    public static void Confirm(ParseResult result)
    {
        result.Confidence = ParseConfidence.High;
        result.GuessLabel = null;
        result.Notes.Add("confirmed by user");
    }

    /// <summary>Called by the chat flow once a file has been confirmed / specified by the human.</summary>
    public static void ApplyUserDecision(ParseResult result, int season, int episode)
    {
        result.Season = season;
        result.EpisodeNumber = episode;
        result.EpisodeToken = episode.ToString();
        result.EpisodeDisplay = FormatEpisode(episode.ToString());
        result.GuessLabel = null;
        result.Confidence = ParseConfidence.High;
        result.Notes.Add("confirmed by user");
    }

    /// <summary>Parses "2x5", "S02E05", "2 5", "Season 2 Episode 5" or a bare episode number.</summary>
    public static bool TryParseSeasonEpisode(string input, out int season, out int episode)
    {
        season = 1;
        episode = 0;

        var s = input.Trim().Trim(',', ';', '.');

        (string Pattern, bool HasSeason)[] patterns =
        {
            (@"^\s*Season\s*(\d{1,2})\s*(?:Episode|Ep)\.?\s*(\d{1,4})\s*$", true),
            (@"^\s*S(\d{1,2})\s*E\s*(\d{1,4})\s*$", true),
            (@"^\s*(\d{1,2})\s*[xX]\s*(\d{1,4})\s*$", true),
            (@"^\s*(\d{1,2})\s*[-–—/,]\s*(\d{1,4})\s*$", true),
            (@"^\s*(\d{1,2})\s+(\d{1,4})\s*$", true),
            (@"^\s*E?\s*(\d{1,4})\s*$", false)   // bare episode number -> season 1
        };

        foreach (var (pattern, hasSeason) in patterns)
        {
            var m = Regex.Match(s, pattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                continue;

            season = hasSeason ? ToInt(m.Groups[1].Value) ?? 1 : 1;
            episode = hasSeason
                ? ToInt(m.Groups[2].Value) ?? 0
                : ToInt(m.Groups[1].Value) ?? 0;

            return season is >= 1 and <= 99 && episode is >= 1 and <= 9999;
        }

        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static bool HasUsableTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.Any(char.IsLetter);

    private static int? ToInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();

        // "17.5" -> 17
        if (value.Contains('.') && double.TryParse(value, out var d))
            return (int)Math.Floor(d);

        return int.TryParse(value, out var n) ? n : null;
    }

    private static int? ParseSeason(string? value)
    {
        var n = ToInt(value);

        // Guard against a year being mistaken for a season.
        if (n is null || n < 1 || n > 99)
            return null;

        return n;
    }

    private static string FormatEpisode(string token)
    {
        token = token.Trim();

        if (token.Contains('.'))
            return token;

        return int.TryParse(token, out var n) ? n.ToString("D2") : token;
    }

    /// <summary>Heuristic used only when both regex and Anitomy came up empty.</summary>
    private static string? FindStandaloneEpisode(string cleanedText)
    {
        string? last = null;

        foreach (Match m in StandaloneNumber.Matches(cleanedText))
        {
            var value = m.Groups[1].Value;

            // 1900-2099 is a year, never an episode.
            if (value.Length == 4 && int.TryParse(value, out var y) && y is >= 1900 and <= 2099)
                continue;

            last = value;
        }

        return last;
    }

    /// <summary>Fallback title when Anitomy could not identify one.</summary>
    private static string? DeriveTitle(string cleanedText, string? episodeToken)
    {
        var text = cleanedText;

        if (!string.IsNullOrWhiteSpace(episodeToken))
            text = Regex.Replace(text, Regex.Escape(episodeToken), " ", RegexOptions.IgnoreCase);

        text = StripEpisodeSpan.Replace(text, " ");
        text = Regex.Replace(text, @"^\s*(?:[-–—:.]+|[-–—])\s*", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('-', '–', '—', ':', ',', '.');

        if (text.Length == 0)
            return null;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.All(w => OnlyNoise.IsMatch(w) || w.All(char.IsDigit)))
            return null;

        return text;
    }
}
