using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MediaSorter.Engine;

/// <summary>Turns parsed files into a plan of moves, applies the plan, and can undo it.</summary>
public static class OrganizationEngine
{
    /// <summary>
    /// Episode/season codes anywhere in a name: "S03E03", "3x05", "E03", "Season 3",
    /// "Episode 3".
    /// </summary>
    private static readonly Regex EpisodeOrSeasonCode = new(
        @"\bS\d{1,2}\s*[ ._-]?\s*E\d{1,4}\b" +     // S03E03 / S03.E03 / s03_e03
        @"|\b\d{1,2}\s*[xX]\s*\d{1,4}\b" +         // 3x05
        @"|\bE\d{1,4}\b" +                         // E03
        @"|\bSeason\s+\d{1,2}\b" +                 // Season 3
        @"|\b(?:Episode|Ep)\.?\s+\d{1,4}\b",       // Episode 3 / Ep 3
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Finds the folder the show should live in. If the target directory already contains a
    /// folder that token-set fuzzy matches the show name, that folder is reused — except for
    /// release/season folders: token-set scoring ignores extra tokens, so a download folder
    /// like "The Legend of Vox Machina S03E03 Vexations 1080p …-FLUX[TGx]" would score ~100
    /// against the show name and get picked as the show root. Folders carrying an episode or
    /// season code are never reused.
    /// </summary>
    public static string ResolveShowDirectory(string targetDirectory, string showName, out string? reusedFolder, out int score)
    {
        reusedFolder = null;
        score = 0;

        if (!Directory.Exists(targetDirectory))
            return Path.Combine(targetDirectory, showName);

        var names = Directory.GetDirectories(targetDirectory)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => !EpisodeOrSeasonCode.IsMatch(n!))
            .Cast<string>()
            .ToList();

        var match = FolderMatcher.BestMatch(showName, names);
        if (match.Folder is not null)
        {
            reusedFolder = match.Folder;
            score = match.Score;
            return Path.Combine(targetDirectory, match.Folder);
        }

        return Path.Combine(targetDirectory, Sanitize(showName));
    }

    /// <summary>Builds the ordered list of file operations. Never touches the disk.</summary>
    public static List<PlanEntry> BuildPlan(
        IReadOnlyList<ParseResult> files,
        string showDirectory,
        string showName,
        bool copy,
        out List<string> warnings)
    {
        warnings = new List<string>();
        var plan = new List<PlanEntry>();

        var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var seasonDirectory = Path.Combine(showDirectory, file.SeasonFolder);
            var baseName = Sanitize(showName) + " - " + file.EpisodeKey + file.Extension;
            var destination = Path.Combine(seasonDirectory, baseName);

            // Already exactly where it should be.
            if (SamePath(file.SourcePath, destination))
            {
                plan.Add(new PlanEntry { File = file, DestinationPath = destination, Action = PlanAction.Skip, Note = "already organized" });
                continue;
            }

            // An identical copy is already sitting there.
            if (File.Exists(destination) && IsSameSize(file.SourcePath, destination))
            {
                plan.Add(new PlanEntry { File = file, DestinationPath = destination, Action = PlanAction.Skip, Note = "identical file already present" });
                continue;
            }

            var finalDestination = Uniquify(destination, claimed);
            claimed[finalDestination] = 1;


            if (!string.Equals(finalDestination, destination, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{file.FileName} and another file both map to {file.EpisodeKey}. The second one becomes \"{Path.GetFileName(finalDestination)}\".");

            plan.Add(new PlanEntry
            {
                File = file,
                DestinationPath = finalDestination,
                Action = copy ? PlanAction.Copy : PlanAction.Move
            });
        }

        return plan;
    }

    /// <summary>Applies a plan. Returns per-file progress through <paramref name="progress"/>.</summary>
    public static ExecutionResult Execute(
        IReadOnlyList<PlanEntry> plan,
        IProgress<ProgressInfo>? progress,
        CancellationToken token,
        List<MoveRecord>? undoBatch)
    {
        var result = new ExecutionResult();
        var stopwatch = Stopwatch.StartNew();
        var done = 0;

        foreach (var entry in plan)
        {
            token.ThrowIfCancellationRequested();
            done++;

            var percent = plan.Count == 0 ? 100 : done * 100.0 / plan.Count;

            if (entry.Action == PlanAction.Skip)
            {
                result.Skipped++;
                progress?.Report(new ProgressInfo($"skip   {entry.File.FileName}: {entry.Note ?? "skipped"}", percent));
                continue;
            }

            try
            {
                var destination = entry.DestinationPath;
                var destinationDirectory = Path.GetDirectoryName(destination)!;
                var created = EnsureDirectory(destinationDirectory);

                if (File.Exists(destination))
                    destination = Uniquify(destination, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

                if (entry.Action == PlanAction.Copy)
                {
                    File.Copy(entry.File.SourcePath, destination, false);
                    result.Copied++;
                    progress?.Report(new ProgressInfo($"copy   {Path.GetFileName(destination)}", percent));
                }
                else
                {
                    MoveFile(entry.File.SourcePath, destination);
                    result.Moved++;
                    progress?.Report(new ProgressInfo($"move   {Path.GetFileName(destination)}", percent));

                    if (undoBatch is not null)
                    {
                        var record = new MoveRecord { Source = entry.File.SourcePath, Destination = destination };
                        record.CreatedDirectories.AddRange(created);
                        undoBatch.Add(record);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{entry.File.FileName}: {ex.Message}");
                progress?.Report(new ProgressInfo($"FAIL   {entry.File.FileName}: {ex.Message}", percent, IsError: true));
            }
        }

        stopwatch.Stop();
        result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        return result;
    }

    /// <summary>Reverses the most recent batch of moves.</summary>
    public static (int restored, List<string> errors) Undo(IReadOnlyList<MoveRecord> records)
    {
        var restored = 0;
        var errors = new List<string>();

        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];

            try
            {
                if (!File.Exists(record.Destination))
                {
                    errors.Add($"{Path.GetFileName(record.Destination)}: the moved file is gone.");
                    continue;
                }

                if (File.Exists(record.Source))
                {
                    errors.Add($"{Path.GetFileName(record.Source)} already exists. The file was left where it is.");
                    continue;
                }

                var sourceDirectory = Path.GetDirectoryName(record.Source);
                if (!string.IsNullOrEmpty(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                MoveFile(record.Destination, record.Source);
                restored++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(record.Destination)}: {ex.Message}");
            }
        }

        // Remove empty folders this app created, deepest first.
        foreach (var record in records)
        {
            foreach (var directory in record.CreatedDirectories.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch
                {
                    // A folder that could not be removed is not worth failing over.
                }
            }
        }

        return (restored, errors);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Creates the directory and every missing parent. Returns the directories it created.</summary>
    public static List<string> EnsureDirectory(string path)
    {
        var created = new List<string>();
        var missing = new List<string>();

        var current = Path.TrimEndingDirectorySeparator(path);
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current) is { Length: > 0 } parent
                ? Path.TrimEndingDirectorySeparator(parent)
                : "";
        }

        missing.Reverse();
        foreach (var directory in missing)
        {
            Directory.CreateDirectory(directory);
            created.Add(directory);
        }

        return created;
    }

    /// <summary>Appends " (2)", " (3)" … until the destination is free.</summary>
    public static string Uniquify(string destination, Dictionary<string, int> claimed)
    {
        if (!File.Exists(destination) && !claimed.ContainsKey(destination))
            return destination;

        var directory = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !claimed.ContainsKey(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>Move that also works when the source and destination are on different volumes.</summary>
    public static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination, false);
        }
        catch (IOException)
        {
            File.Copy(source, destination, false);
            File.Delete(source);
        }
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        cleaned = RegexReplaceMultiSpace(cleaned);
        return cleaned.Trim().Length == 0 ? "Show" : cleaned.Trim();
    }

    private static string RegexReplaceMultiSpace(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s{2,}", " ");

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    private static bool IsSameSize(string a, string b)
    {
        try
        {
            return new FileInfo(a).Length == new FileInfo(b).Length;
        }
        catch
        {
            return false;
        }
    }
}
