using System.Collections.Generic;
using System.IO;

namespace MediaSorter.Engine;

/// <summary>How sure we are that a season/episode was read correctly from a filename.</summary>
public enum ParseConfidence
{
    /// <summary>An explicit SxxEyy / 1x05 / "Season 2 Episode 4" pattern was found.</summary>
    High,

    /// <summary>Anitomy found an episode number together with a title, or an episode on its own.</summary>
    Medium,

    /// <summary>Only a heuristic guess (or nothing at all) — ask the human.</summary>
    Low
}

public enum PlanAction
{
    Move,
    Copy,
    Skip
}

/// <summary>Result of parsing a single video filename.</summary>
public sealed class ParseResult
{
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";

    /// <summary>Cleaned show title as read from the filename; null when nothing usable was found.</summary>
    public string? Title { get; set; }

    public int Season { get; set; } = 1;
    public string? EpisodeToken { get; set; }
    public int? EpisodeNumber { get; set; }

    /// <summary>Display form of the episode, e.g. "05" or "17.5".</summary>
    public string EpisodeDisplay { get; set; } = "";

    public ParseConfidence Confidence { get; set; } = ParseConfidence.Low;
    public string? GuessLabel { get; set; }
    public string? ReleaseGroup { get; set; }
    public List<string> Notes { get; } = new();
    public List<string> StrippedTags { get; } = new();

    public bool NeedsConfirmation => Confidence == ParseConfidence.Low;

    public string SeasonFolder => $"Season {Season:D2}";

    public string EpisodeKey => $"S{Season:D2}E{EpisodeDisplay}";

    /// <summary>Short single-line description used in progress / plan output.</summary>
    public string Describe() => EpisodeDisplay.Length == 0 ? "(no episode)" : EpisodeKey;
}

/// <summary>One planned file operation.</summary>
public sealed class PlanEntry
{
    public required ParseResult File { get; init; }
    public required string DestinationPath { get; init; }
    public PlanAction Action { get; set; } = PlanAction.Move;
    public string? Note { get; set; }
}

/// <summary>Enough detail to reverse a successful move.</summary>
public sealed class MoveRecord
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public List<string> CreatedDirectories { get; } = new();
}

public sealed class ExecutionResult
{
    public int Moved { get; set; }
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
    public double ElapsedSeconds { get; set; }
}

public readonly record struct ProgressInfo(string Message, double Percent, bool IsError = false);

/// <summary>Enumerates video files inside a source directory.</summary>
public static class VideoScanner
{
    public static readonly string[] DefaultExtensions = { ".mkv", ".mp4", ".avi", ".webm" };

    private static readonly HashSet<string> Extensions =
        new(DefaultExtensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsVideoFile(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <param name="excludeDirs">Directories that must not be reported (typically the target tree).</param>
    /// <param name="onProgress">Called with a running count while scanning (already marshalled to the UI by the caller).</param>
    public static List<string> Scan(string directory, bool recursive, IEnumerable<string>? excludeDirs = null, Action<int>? onProgress = null)
    {
        var excludes = (excludeDirs ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .ToList();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true
        };

        var results = new List<string>();
        var found = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            if (!Extensions.Contains(Path.GetExtension(file)))
                continue;

            if (IsUnderAny(file, excludes))
                continue;

            results.Add(file);
            found++;

            if (onProgress is not null && (found <= 10 || found % 50 == 0))
                onProgress(found);
        }

        onProgress?.Invoke(found);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static bool IsUnderAny(string file, List<string> dirs)
    {
        foreach (var dir in dirs)
        {
            if (file.Length <= dir.Length)
                continue;

            if (!file.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                continue;

            var next = file[dir.Length];
            if (next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar)
                return true;
        }

        return false;
    }
}
