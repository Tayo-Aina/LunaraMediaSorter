using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MediaSorter.Engine;

/// <summary>Output of <see cref="FilenameCleaner.Clean"/>.</summary>
public sealed class CleanResult
{
    public string Text { get; set; } = "";
    public List<string> Removed { get; } = new();
}

/// <summary>
/// Deterministic, regex-only stripping of everything that is not part of a show title:
/// quality tags (1080p, x264, 60fps), release group brackets and CRC hashes.
/// </summary>
public static class FilenameCleaner
{
    /// <summary>Tokens that mark a bracketed group as purely technical.</summary>
    private static readonly HashSet<string> TechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // resolution
        "1080p", "1080i", "720p", "480p", "576p", "1440p", "2160p", "360p", "4k", "8k", "fhd", "uhd", "480i",
        // video codecs
        "x264", "x265", "x266", "h264", "h265", "h266", "h.264", "h.265", "h.266", "hevc", "avc", "av1",
        "xvid", "divx", "vp9", "vc1", "mpeg2", "10bit", "8bit", "hi10p",
        // hdr
        "hdr", "hdr10", "dv", "dovi", "sdr", "hlg",
        // audio
        "aac", "aaclc", "ac3", "eac3", "dd", "dd+", "ddp", "dts", "dts-hd", "dtshd", "truehd", "flac",
        "opus", "mp3", "vorbis", "atmos", "2ch", "5.1", "7.1", "2.0",
        // source
        "web-dl", "webdl", "webrip", "bluray", "bdrip", "brrip", "hdtv", "dvdrip", "remux", "bdremux",
        "bd", "dvd", "hdrip", "cam", "hdtvrip",
        // containers / misc
        "mkv", "mp4", "avi", "webm", "flv", "ts", "m2ts", "wmv",
        "multi", "multisub", "subtitle", "subtitles", "multiple", "dual", "batch", "repack", "proper", "internal",
        "60fps", "30fps", "24fps", "23.976fps"
    };

    /// <summary>Bare (non-bracketed) technical tokens that are always safe to drop.</summary>
    private static readonly Regex BareTech = new(
        @"\b\d{3,4}[pq]\b" +
        @"|\b(?:4k|8k|fhd|uhd)\b" +
        @"|\b(?:x264|x265|x266|h264|h265|h266|h\.264|h\.265|hevc|avc|av1|xvid|divx|vp9|hi10p)\b" +
        @"|\b\d{1,2}bit\b" +
        @"|\b\d+(?:\.\d+)?fps\b" +
        @"|\b(?:hdr10\+|hdr10|hdr|dovi|sdr|hlg)(?![a-z0-9])" +
        @"|\b(?:aaclc|aac|ac3|eac3|ddp|dd|dts-hd|dtshd|dts|truehd|flac|opus|mp3|vorbis|atmos)(?![a-z0-9])" +
        @"|\b(?:web-?dl|webrip|bluray|bdrip|brrip|hdtv|dvdrip|remux|bdremux|hdrip)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A bracket group whose whole content is just an episode reference.</summary>
    private static readonly Regex EpisodeOnly = new(
        @"^\s*(?:S\d{1,2}\s*E\s*\d{1,4}|\d{1,4}(?:\s*[-–—,]\s*\d{1,4})*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Split a bracket contents into candidate tokens.</summary>
    private static readonly Regex TokenSplit = new(@"[\s\-–—_.,]+", RegexOptions.Compiled);

    private static readonly Regex Crc32 = new("^[0-9A-Fa-f]{8}$", RegexOptions.Compiled);
    private static readonly Regex Resolution = new(@"^\d{3,4}[pq]$", RegexOptions.Compiled);
    private static readonly Regex BitDepth = new(@"^\d{1,2}bit$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Fps = new(@"^\d+(?:\.\d+)?fps$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Dimension = new(@"^\d{3,4}\s*[xX×]\s*\d{3,4}$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^v\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AudioChannels = new(@"^\d\.\d(?:ch)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DigitRun = new(@"^\d{1,4}$", RegexOptions.Compiled);

    /// <summary>
    /// Strips release-group brackets, CRC hashes and quality tags from a file name (no extension).
    /// </summary>
    public static CleanResult Clean(string nameWithoutExtension)
    {
        var result = new CleanResult();
        var text = nameWithoutExtension;

        // "My_Show_Name" -> "My Show Name"
        text = text.Replace('_', ' ');

        // "Show.Name.1x05.1080p" -> "Show Name 1x05 1080p" (but keep "Mr. Robot" alone)
        if (Regex.Matches(text, @"\.").Count > 1)
            text = text.Replace('.', ' ');

        // [ ... ]  -> release group, CRC, quality stack. Episode-only groups are kept.
        text = Regex.Replace(text, @"\[[^\]]*\]", match =>
        {
            var inner = match.Value.Substring(1, match.Value.Length - 2);
            if (EpisodeOnly.IsMatch(inner))
                return match.Value;

            Remember(result, inner);
            return " ";
        });

        // ( ... )  -> only when the whole group is technical, e.g. "(1080p x264)" or "(The Final Season)".
        text = Regex.Replace(text, @"\([^()]*\)", match =>
        {
            var inner = match.Value.Substring(1, match.Value.Length - 2);
            if (!IsTechnical(inner))
                return match.Value;

            Remember(result, inner);
            return " ";
        });

        // leftover bare quality tokens
        foreach (Match m in BareTech.Matches(text))
            Remember(result, m.Value);

        text = BareTech.Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"\s+([-–—])\s+$", " $1");

        result.Text = text;
        return result;
    }

    private static void Remember(CleanResult result, string inner)
    {
        foreach (var token in TokenSplit.Split(inner))
        {
            if (token.Length == 0)
                continue;

            if (!result.Removed.Contains(token, StringComparer.OrdinalIgnoreCase))
                result.Removed.Add(token);
        }
    }

    /// <summary>True when every token inside a bracket group is a known quality/codec/source tag.</summary>
    public static bool IsTechnical(string inner)
    {
        var tokens = TokenSplit.Split(inner)
            .Where(t => t.Length > 0 && t != "-" && t != "+" && t != "&")
            .ToList();

        if (tokens.Count == 0)
            return false;

        return tokens.All(IsTechToken);
    }

    private static bool IsTechToken(string token)
    {
        if (TechWords.Contains(token))
            return true;

        if (Crc32.IsMatch(token) || Resolution.IsMatch(token) || BitDepth.IsMatch(token))
            return true;

        if (Fps.IsMatch(token) || Dimension.IsMatch(token) || Version.IsMatch(token))
            return true;

        if (AudioChannels.IsMatch(token) || DigitRun.IsMatch(token))
            return true;

        // "x264-Group", "DD+5.1"
        var dash = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (dash.Length == 2 && IsTechToken(dash[0]) && TechWords.Contains(dash[1]))
            return true;

        return false;
    }
}
