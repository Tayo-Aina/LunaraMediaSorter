using System.Text;
using MediaSorter.Engine;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static void Check(bool condition, string label, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            return;
        }

        Failures.Add(detail is null ? label : $"{label}\n      {detail}");
    }

    private static void Eq(string expected, string actual, string label) =>
        Check(expected == actual, label, $"expected '{expected}', got '{actual}'");

    private static void Eq(int expected, int actual, string label) =>
        Check(expected == actual, label, $"expected {expected}, got {actual}");

    private static void Eq<T>(T expected, T actual, string label, string? detail = null) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), label,
            detail is null ? $"expected {expected}, got {actual}" : $"{detail} — expected {expected}, got {actual}");

    private static int Main()
    {
        TestCleaner();
        TestParser();
        TestSeasonEpisodeInput();
        TestFolderMatcher();
        TestPlannerAndExecutor();
        TestScanner();
        TestSelection();
        TestSupportInfo();

        Console.WriteLine();
        Console.WriteLine($"passed: {_passed}, failed: {Failures.Count}");

        foreach (var failure in Failures)
            Console.WriteLine("  FAIL " + failure);

        return Failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ cleaner

    private static void TestCleaner()
    {
        Console.WriteLine("FilenameCleaner");

        var r = FilenameCleaner.Clean("[SubsPlease] Jujutsu Kaisen - 05 (1080p) [ABCD1234]");
        Eq("Jujutsu Kaisen - 05", r.Text, "strips group, CRC and 1080p");
        Check(r.Removed.Contains("SubsPlease"), "records the release group", string.Join(",", r.Removed));
        Check(r.Removed.Contains("1080p"), "records 1080p");

        r = FilenameCleaner.Clean("Show.Name.1x05.1080p.WEB-DL.x264-GROUP");
        Check(!r.Text.Contains("1080p") && !r.Text.Contains("x264"), "strips bare quality tokens", r.Text);
        Eq("Show Name 1x05 -GROUP", r.Text, "dotted name becomes spaced, quality stripped");

        r = FilenameCleaner.Clean("[Judas] One Piece - 1047 [1080p][HEVC][BD]");
        Eq("One Piece - 1047", r.Text, "strips a quality bracket stack");

        r = FilenameCleaner.Clean("[Erai-raws] Shingeki no Kyojin - 12 [1080p][Multiple Subtitle]");
        Check(!r.Text.Contains("Erai") && !r.Text.Contains("Multiple"), "strips 'Multiple Subtitle'", r.Text);
        Eq("Shingeki no Kyojin - 12", r.Text, "erai-raws filename");

        r = FilenameCleaner.Clean("[Group] Show Name - 05 [FHD][10bit][x265]");
        Eq("Show Name - 05", r.Text, "FHD/10bit/x265 stack");

        r = FilenameCleaner.Clean("ShowName - 03v2 (BD 1080p x265 10bit)");
        Eq("ShowName - 03v2", r.Text, "parenthesised tech group");
        Check(!r.Text.Contains("10bit"), "10bit removed from parens");

        r = FilenameCleaner.Clean("Show (The Final Season)");
        Eq("Show (The Final Season)", r.Text, "keeps a meaningful parenthesised title");

        r = FilenameCleaner.Clean("[05] Show");
        Eq("[05] Show", r.Text, "keeps an episode-only bracket");

        r = FilenameCleaner.Clean("My_Show_Name");
        Eq("My Show Name", r.Text, "underscores become spaces");
    }

    // ------------------------------------------------------------------- parser

    private static void TestParser()
    {
        Console.WriteLine("EpisodeParser");

        // 1. release-group anime style
        var p = EpisodeParser.Parse(@"C:\dl\[SubsPlease] Jujutsu Kaisen - 05 (1080p) [ABCD1234].mkv");
        Eq("05", p.EpisodeDisplay, "anitomy episode");
        Eq(1, p.Season, "default season 1");
        Eq("Jujutsu Kaisen", p.Title ?? "", "anitomy title");
        Eq("S01E05", p.EpisodeKey, "episode key");
        Check(!p.NeedsConfirmation, "release-group file is confident");
        Check(p.StrippedTags.Contains("ABCD1234") && p.StrippedTags.Contains("SubsPlease"), "CRC + group recorded as stripped");

        // 2. explicit SxxEyy
        p = EpisodeParser.Parse("Some.Show.S02E07.720p.HDTV.x264-LOL.avi");
        Eq(2, p.Season, "S02 season");
        Eq("07", p.EpisodeDisplay, "S07 episode");
        Eq("Some Show", p.Title ?? "", "SxxEyy title");
        Check(!p.NeedsConfirmation, "SxxEyy is high confidence");
        Eq("S02E07", p.EpisodeKey, "S02E07 key");

        // 3. 1x05
        p = EpisodeParser.Parse("Show.Name.1x05.1080p.WEB-DL.x264-GROUP.mkv");
        Eq(1, p.Season, "1x05 season");
        Eq("05", p.EpisodeDisplay, "1x05 episode");
        Check(!p.NeedsConfirmation, "1x05 is confident");

        // 4. Season 2 Episode 4 wording
        p = EpisodeParser.Parse("Show Name Season 2 Episode 4.mp4");
        Eq(2, p.Season, "season word season");
        Eq("04", p.EpisodeDisplay, "season word episode");
        Check(!p.NeedsConfirmation, "season/episode words are confident");

        // 5. E05
        p = EpisodeParser.Parse("My Show - E05 - Title Here.webm");
        Eq("05", p.EpisodeDisplay, "E05 episode");
        Eq("My Show", p.Title ?? "", "E05 title");
        Check(!p.NeedsConfirmation, "E05 is confident");

        // 6. decimal episode
        p = EpisodeParser.Parse("Jujutsu Kaisen - 17.5 (1080p) [VRV].mkv");
        Eq("17.5", p.EpisodeDisplay, "decimal episode kept");
        Eq("S01E17.5", p.EpisodeKey, "decimal key");

        // 7. high absolute number
        p = EpisodeParser.Parse("[Judas] One Piece - 1047 [1080p][HEVC][BD].mkv");
        Eq("1047", p.EpisodeDisplay, "four digit episode");
        Check(!p.NeedsConfirmation, "title + episode is confident");

        // 8. version suffix
        p = EpisodeParser.Parse("[SubsPlease] Spy x Family - 05v2 (1080p) [1234ABCD].mkv");
        Eq("05", p.EpisodeDisplay, "05v2 -> 05");

        // 9. bare number -> low confidence, with a guess
        p = EpisodeParser.Parse(@"C:\dl\05.mkv");
        Check(p.NeedsConfirmation, "bare number needs confirmation");
        Eq("Season 1 Episode 05", p.GuessLabel ?? "", "guess label matches the spec wording");
        Eq(1, p.Season, "guess season 1");

        // 10. no number at all -> low confidence, no guess
        p = EpisodeParser.Parse(@"C:\dl\random_download_final_v2.mp4");
        Check(p.NeedsConfirmation, "v2 suffix is not mistaken for an episode");
        Check(p.GuessLabel is null, "no bogus guess for a nameless file", p.GuessLabel ?? "null");

        // 11. word only
        p = EpisodeParser.Parse(@"C:\dl\video.mkv");
        Check(p.NeedsConfirmation, "video.mkv needs confirmation");
        Check(p.GuessLabel is null, "video.mkv has no guess");
        Check(p.Season == 1, "video.mkv defaults to season 1");

        // 12. episode word without title
        p = EpisodeParser.Parse("Episode 5.mp4");
        Eq("05", p.EpisodeDisplay, "'Episode 5' parses");
        Check(!p.NeedsConfirmation, "'Episode 5' is confident");

        // 13. resolution is never read as an episode
        p = EpisodeParser.Parse("Some Show - 12 [1080p][JPSC].mkv");
        Eq("12", p.EpisodeDisplay, "1080p ignored");

        // 14. year is never read as an episode
        p = EpisodeParser.Parse("Cool Show 2019 - 03.mkv");
        Eq("03", p.EpisodeDisplay, "year not mistaken for episode");

        // 15. user decisions
        p = EpisodeParser.Parse(@"C:\dl\05.mkv");
        EpisodeParser.ApplyUserDecision(p, 3, 12);
        Eq(3, p.Season, "user season applied");
        Eq("12", p.EpisodeDisplay, "user episode applied");
        Check(!p.NeedsConfirmation, "user decision clears the prompt");
        Eq("S03E12", p.EpisodeKey, "user key");

        p = EpisodeParser.Parse("Show - 05.mkv");
        EpisodeParser.Confirm(p);
        Check(!p.NeedsConfirmation, "confirm clears the prompt");
        Check(p.GuessLabel is null, "confirm clears the guess");
    }

    private static void TestSeasonEpisodeInput()
    {
        Console.WriteLine("TryParseSeasonEpisode");

        var cases = new (string Input, int Season, int Episode)[]
        {
            ("2x5", 2, 5),
            ("S02E05", 2, 5),
            ("s2e5", 2, 5),
            ("2 5", 2, 5),
            ("2-5", 2, 5),
            ("Season 2 Episode 5", 2, 5),
            ("5", 1, 5),
            ("E7", 1, 7),
            ("  3x10  ", 3, 10),
            ("101", 1, 101)
        };

        foreach (var (input, season, episode) in cases)
        {
            var ok = EpisodeParser.TryParseSeasonEpisode(input, out var s, out var e);
            Check(ok, $"'{input}' should parse");
            Check(s == season && e == episode, $"'{input}' values", $"got {s}x{e}, wanted {season}x{episode}");
        }

        foreach (var bad in new[] { "", "abc", "0x5", "-1", "12x0", "999x99999" })
        {
            var ok = EpisodeParser.TryParseSeasonEpisode(bad, out _, out _);
            Check(!ok, $"'{bad}' should be rejected");
        }
    }

    // ------------------------------------------------------------------ matcher

    private static void TestFolderMatcher()
    {
        Console.WriteLine("FolderMatcher");

        var existing = new[] { "Jujutsu Kaisen (2020)", "Showdown", "One Piece", "Attack on Titan" };

        var match = FolderMatcher.BestMatch("Jujutsu Kaisen", existing);
        Eq("Jujutsu Kaisen (2020)", match.Folder ?? "", "reuses a year-suffixed folder");
        Check(match.Score >= FolderMatcher.MatchThreshold, "score clears the threshold", match.Score.ToString());

        match = FolderMatcher.BestMatch("Show", existing);
        Check(match.Folder is null, "must not match 'Show' to 'Showdown'", match.Folder ?? "null");

        match = FolderMatcher.BestMatch("Nothing Like These", existing);
        Check(match.Folder is null, "unrelated name matches nothing", match.Folder ?? "null");

        Check(FolderMatcher.Score("Random Download Final", "Jujutsu Kaisen") < FolderMatcher.SanityThreshold,
            "junk title scores below the sanity threshold");

        Check(FolderMatcher.Score("Jujutsu Kaisen", "Jujutsu Kaisen") == 100, "identical titles score 100");

        // Folder names are compared case-insensitively — on Windows "bluey" and "Bluey"
        // are the same folder, and FuzzySharp's raw ratio drops case-only pairs below
        // the threshold for short names ("Bluey" vs "bluey" = 80).
        match = FolderMatcher.BestMatch("Bluey", new[] { "bluey" });
        Eq("bluey", match.Folder ?? "", "case-only folder name is matched");
        Check(match.Score >= FolderMatcher.MatchThreshold, "case-only short name clears the threshold", match.Score.ToString());
        Check(FolderMatcher.Score("Jujutsu Kaisen", "jujutsu kaisen") == 100, "case-only titles score 100");
    }

    // ------------------------------------------------------------- plan / move

    private static void TestPlannerAndExecutor()
    {
        Console.WriteLine("OrganizationEngine");

        var root = Path.Combine(Path.GetTempPath(), "sfc-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        try
        {
            // an existing folder that fuzzy matches the show name
            var existingShow = Path.Combine(target, "Jujutsu Kaisen (2020)");
            Directory.CreateDirectory(existingShow);

            var showDir = OrganizationEngine.ResolveShowDirectory(target, "Jujutsu Kaisen", out var reused, out var score);
            Eq(existingShow, showDir, "reuses the fuzzy-matched folder");
            Eq("Jujutsu Kaisen (2020)", reused ?? "", "reports the reused folder");
            Check(score >= FolderMatcher.MatchThreshold, "reported score", score.ToString());

            var freshDir = OrganizationEngine.ResolveShowDirectory(Path.Combine(root, "empty"), "Solo Show", out var noReuse, out _);
            Check(noReuse is null, "no folder to reuse");
            Eq(Path.Combine(root, "empty", "Solo Show"), freshDir, "creates a fresh show folder");

            // case-only reuse: a lowercase "bluey" folder must be reused for typed "Bluey"
            // (the reuse-decision prompt in the chat flow hangs off this match)
            var caseTarget = Path.Combine(root, "target-case");
            Directory.CreateDirectory(Path.Combine(caseTarget, "bluey"));
            var caseDir = OrganizationEngine.ResolveShowDirectory(caseTarget, "Bluey", out var caseReuse, out var caseScore);
            Eq("bluey", caseReuse ?? "", "case-only folder name is reused");
            Check(caseScore >= FolderMatcher.MatchThreshold, "case-only reuse score clears the threshold", caseScore.ToString());
            Eq(Path.Combine(caseTarget, "bluey"), caseDir, "resolves to the existing folder, not a fresh one");

            // release / season folders are never reused as the show root, no matter how
            // well they token-set match (reported bug: Season 03 was created inside an
            // empty "... S03E03 Vexations 1080p ...-FLUX[TGx]" downloads folder)
            var release = Path.Combine(target,
                "The Legend of Vox Machina S03E03 Vexations 1080p AMZN WEB-DL DDP5 1 H 264-FLUX[TGx]");
            Directory.CreateDirectory(release);
            Check(FolderMatcher.Score("The Legend Of Vox Machina", Path.GetFileName(release)) >= FolderMatcher.MatchThreshold,
                "the release folder really does match the show name (why it was picked)");

            var releaseDir = OrganizationEngine.ResolveShowDirectory(
                target, "The Legend Of Vox Machina", out var releaseReuse, out _);
            Check(releaseReuse is null, "release-named folder is not reused", releaseReuse);
            Eq(Path.Combine(target, "The Legend Of Vox Machina"), releaseDir,
                "fresh show folder is named exactly as typed");

            var seasonFolder = Path.Combine(root, "target2");
            Directory.CreateDirectory(Path.Combine(seasonFolder, "Solo Show Season 1"));
            var seasonDir = OrganizationEngine.ResolveShowDirectory(
                seasonFolder, "Solo Show", out var seasonReuse, out _);
            Check(seasonReuse is null, "season-named folder is not reused as the show root", seasonReuse);
            Eq(Path.Combine(seasonFolder, "Solo Show"), seasonDir, "fresh folder instead of a season folder");

            // the clean "(2020)" folder in target/ is still picked over nothing (re-checked
            // now that the release folder sits beside it)
            var showDir2 = OrganizationEngine.ResolveShowDirectory(target, "Jujutsu Kaisen", out var reused2, out _);
            Eq(existingShow, showDir2, "clean show folder still wins with a release folder present");
            Eq("Jujutsu Kaisen (2020)", reused2 ?? "", "reuse still reported");

            // files
            var names = new[]
            {
                "[SubsPlease] Jujutsu Kaisen - 01 (1080p) [AAAA0001].mkv",
                "Jujutsu Kaisen - 02.mkv",
                "Jujutsu Kaisen - 02 (1080p x265).mkv",   // duplicate episode
                "05.mkv"                                    // unsure -> confirmed below
            };

            var paths = new List<string>();
            foreach (var name in names)
            {
                var path = Path.Combine(source, name);
                File.WriteAllText(path, "x" + name.Length);
                paths.Add(path);
            }

            var parsed = paths.Select(EpisodeParser.Parse).ToList();
            EpisodeParser.ApplyUserDecision(parsed[3], 2, 5);

            var plan = OrganizationEngine.BuildPlan(parsed, showDir, "Jujutsu Kaisen", copy: false, out var warnings);

            Eq(4, plan.Count, "plan has every file");
            Check(warnings.Count >= 1, "duplicate episode is warned about", string.Join(" | ", warnings));

            var first = plan.First(p => p.File.EpisodeDisplay == "01");
            Eq("Jujutsu Kaisen - S01E01.mkv", Path.GetFileName(first.DestinationPath), "clean name format");
            Eq(Path.Combine(existingShow, "Season 01"), Path.GetDirectoryName(first.DestinationPath) ?? "", "Season [XX] layout");

            var duplicates = plan.Where(p => p.File.EpisodeDisplay == "02").ToList();
            Check(duplicates.Any(d => Path.GetFileName(d.DestinationPath).Contains(" (2)")),
                "second duplicate gets a suffix", string.Join(", ", duplicates.Select(d => Path.GetFileName(d.DestinationPath))));

            // move it for real
            var undo = new List<MoveRecord>();
            var progress = new List<ProgressInfo>();
            var reporter = new SyncProgress(progress);
            var result = OrganizationEngine.Execute(plan, reporter, CancellationToken.None, undo);

            Eq(4, result.Moved, "moved every file");
            Eq(0, result.Failed, "no failures");
            Check(File.Exists(Path.Combine(existingShow, "Season 01", "Jujutsu Kaisen - S01E01.mkv")), "file landed in Season 01");
            Check(File.Exists(Path.Combine(existingShow, "Season 02", "Jujutsu Kaisen - S02E05.mkv")), "confirmed file landed in Season 02");
            Check(!File.Exists(paths[0]), "source file is gone after a move");
            Check(Directory.Exists(Path.Combine(existingShow, "Season 02")), "Season 02 folder created for the confirmed file");
            Check(progress.Count >= 4, "progress reported per file", progress.Count.ToString());

            // undo
            var (restored, errors) = OrganizationEngine.Undo(undo);
            Eq(4, restored, "undo restored every file");
            Check(errors.Count == 0, "undo reported no errors", string.Join(" | ", errors));
            Check(File.Exists(paths[0]), "undo put the file back");
            Check(File.Exists(paths[3]), "undo put the confirmed file back");
            Check(!Directory.Exists(Path.Combine(existingShow, "Season 02")), "undo removed a folder it created");

            // skip an already-organized file
            var alreadyDir = Path.Combine(existingShow, "Season 01");
            Directory.CreateDirectory(alreadyDir);
            var alreadyPath = Path.Combine(alreadyDir, "Jujutsu Kaisen - S01E01.mkv");
            File.WriteAllText(alreadyPath, "same-size-content");
            var sameSize = new ParseResult { SourcePath = alreadyPath, FileName = Path.GetFileName(alreadyPath), Extension = ".mkv", Season = 1, EpisodeDisplay = "01" };
            var skipPlan = OrganizationEngine.BuildPlan(new[] { sameSize }, showDir, "Jujutsu Kaisen", false, out _);
            Eq((int)PlanAction.Skip, (int)skipPlan[0].Action, "identical destination is skipped");

            // copy mode leaves the original alone
            var copySource = Path.Combine(source, "Copy Me - 01.mkv");
            File.WriteAllText(copySource, "copy");
            var copyParsed = EpisodeParser.Parse(copySource);
            var copyPlan = OrganizationEngine.BuildPlan(new[] { copyParsed }, showDir, "Jujutsu Kaisen", copy: true, out _);
            var copyResult = OrganizationEngine.Execute(copyPlan, null, CancellationToken.None, null);
            Eq(1, copyResult.Copied, "copy mode copies");
            Check(File.Exists(copySource), "copy mode keeps the original");

            // sanitize
            Eq("Bad Name Here", OrganizationEngine.Sanitize("Bad:Name/Here"), "invalid filename characters removed");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* test residue */ }
        }
    }

    // ------------------------------------------------------------------ scanner

    private static void TestScanner()
    {
        Console.WriteLine("VideoScanner");

        var root = Path.Combine(Path.GetTempPath(), "sfc-scan-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        var excluded = Path.Combine(root, "target");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(excluded);

        try
        {
            File.WriteAllText(Path.Combine(root, "a.mkv"), "1");
            File.WriteAllText(Path.Combine(root, "b.mp4"), "1");
            File.WriteAllText(Path.Combine(root, "c.avi"), "1");
            File.WriteAllText(Path.Combine(root, "d.webm"), "1");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "1");
            File.WriteAllText(Path.Combine(root, "cover.jpg"), "1");
            File.WriteAllText(Path.Combine(nested, "e.mkv"), "1");
            File.WriteAllText(Path.Combine(excluded, "already - S01E01.mkv"), "1");

            var flat = VideoScanner.Scan(root, recursive: false);
            Eq(4, flat.Count, "top-level scan finds only videos");

            var deep = VideoScanner.Scan(root, recursive: true, excludeDirs: new[] { excluded });
            Check(deep.Count == 5, "recursive scan includes nested, excludes the target",
                $"expected 5, got {deep.Count}: {string.Join(", ", deep)}");

            var counts = new List<int>();
            VideoScanner.Scan(root, recursive: true, excludeDirs: new[] { excluded }, p => counts.Add(p));
            Check(counts.Count >= 1, "scan reports progress");

            Check(VideoScanner.IsVideoFile("x.mkv") && !VideoScanner.IsVideoFile("x.srt"), "extension check");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* test residue */ }
        }
    }

    private sealed class SyncProgress : IProgress<ProgressInfo>
    {
        private readonly List<ProgressInfo> _sink;
        public SyncProgress(List<ProgressInfo> sink) => _sink = sink;
        public void Report(ProgressInfo value) => _sink.Add(value);
    }

    // ---------------------------------------------------------------- selection

    private static void TestSelection()
    {
        Console.WriteLine("ShowSelector + embedder");

        var models = FindModelsDirectory();
        if (models is null)
        {
            Check(false, "Models folder with encoder.onnx found in the repo");
            return;
        }

        var factory = MiniLmSimilarityFactory.TryLoad(
            Path.Combine(models, "encoder.onnx"),
            Path.Combine(models, "vocab.txt"),
            out var error);

        Check(factory is not null, "embedding model loads", error);

        if (factory is null)
            return;

        using (factory)
        {
            var model = factory.Open("Jujutsu Kaisen");

            // --------------------------------------------------- raw score margins
            Check(model.Score("[SubsPlease] Jujutsu Kaisen - S01E01 (1080p) [AAAA1111]") >= ShowSelector.IncludeThreshold,
                "same show (release name) scores above include", model.Score("[SubsPlease] Jujutsu Kaisen - S01E01 (1080p) [AAAA1111]").ToString("0.000"));

            Check(model.Score("Jujutsu Kaisen - 05") >= ShowSelector.IncludeThreshold,
                "same show (simple name) scores above include");

            Check(model.Score("Juju Kaisen S2E05") >= ShowSelector.IncludeThreshold,
                "abbreviated same show scores above include", model.Score("Juju Kaisen S2E05").ToString("0.000"));

            Check(model.Score("[SubsPlease] Steins;Gate - S01E05 (1080p) [EFGH5678]") < ShowSelector.ReviewThreshold,
                "different show scores below review", model.Score("[SubsPlease] Steins;Gate - S01E05 (1080p) [EFGH5678]").ToString("0.000"));

            Check(model.Score("Attack on Titan S03E12") < ShowSelector.ReviewThreshold,
                "different show (AoT) scores below review", model.Score("Attack on Titan S03E12").ToString("0.000"));

            Check(model.Score("Christmas Special") < ShowSelector.ReviewThreshold,
                "junk name scores below review");

            // ------------------------------------------------------- mixed folder
            var files = new List<ParseResult>
            {
                Parse(@"C:\dl\[SubsPlease] Jujutsu Kaisen - S01E01 (1080p) [AAAA1111].mkv"),
                Parse(@"C:\dl\[SubsPlease] Jujutsu Kaisen - S01E02 (1080p) [BBBB2222].mkv"),
                Parse(@"C:\dl\Jujutsu Kaisen - 03.mkv"),
                Parse(@"C:\dl\[SubsPlease] Steins;Gate - S01E05 (1080p) [CCCC3333].mkv"),
                Parse(@"C:\dl\Attack on Titan S03E12.mkv"),
                Parse(@"C:\dl\Christmas Special.mkv")
            };

            var result = ShowSelector.Select(files, "Jujutsu Kaisen", model);
            var byName = result.Files.ToDictionary(f => f.File.FileName, f => f);

            Eq(FileVerdict.Include, byName["[SubsPlease] Jujutsu Kaisen - S01E01 (1080p) [AAAA1111].mkv"].Verdict, "ep01 included");
            Eq(FileVerdict.Include, byName["[SubsPlease] Jujutsu Kaisen - S01E02 (1080p) [BBBB2222].mkv"].Verdict, "ep02 included");
            Eq(FileVerdict.Include, byName["Jujutsu Kaisen - 03.mkv"].Verdict, "ep03 included");
            Eq(FileVerdict.Exclude, byName["[SubsPlease] Steins;Gate - S01E05 (1080p) [CCCC3333].mkv"].Verdict, "Steins;Gate excluded");
            Eq(FileVerdict.Exclude, byName["Attack on Titan S03E12.mkv"].Verdict, "Attack on Titan excluded");
            Eq(FileVerdict.Exclude, byName["Christmas Special.mkv"].Verdict, "junk excluded");

            Check(byName["[SubsPlease] Steins;Gate - S01E05 (1080p) [CCCC3333].mkv"].Reason?.Contains("looks like") == true,
                "excluded file carries a reason",
                byName["[SubsPlease] Steins;Gate - S01E05 (1080p) [CCCC3333].mkv"].Reason);

            Eq(3, result.Included.Count(), "exactly the three same-show files selected");
            Check(result.BestScore >= ShowSelector.IncludeThreshold, "best match is confident",
                result.BestScore.ToString("0.000"));

            // ------------------------------------------------------ review band
            var borderline = new[] { Parse(@"C:\dl\Kimetsu no Yaiba - 19.mkv") };
            var review = ShowSelector.Select(borderline, "Jujutsu Kaisen", model);
            Eq(FileVerdict.Review, review.Files[0].Verdict, "borderline other show lands in the review band",
                review.Files[0].Score.ToString("0.000"));

            // ----------------------------------------------------- nothing matches
            var junk = new[]
            {
                Parse(@"C:\dl\Christmas Special.mkv"),
                Parse(@"C:\dl\[BD 1920x1080].mkv")
            };
            var none = ShowSelector.Select(junk, "Jujutsu Kaisen", model);
            Check(none.BestScore < ShowSelector.ReviewThreshold, "junk folder best score below review",
                none.BestScore.ToString("0.000"));
            Eq(0, none.Included.Count(), "nothing included from a junk folder");

            // -------------------------------------------------- parent folder signal
            var inNamedFolder = new[] { Parse(@"C:\dl\Jujutsu.Kaisen.S02.1080p.WEB-DL\05.mkv") };
            var rescued = ShowSelector.Select(inNamedFolder, "Jujutsu Kaisen", model);
            Eq(FileVerdict.Include, rescued.Files[0].Verdict, "parent folder name rescues an unnamed file",
                rescued.Files[0].Score.ToString("0.000"));

            // ------------------------------------- stress: leetspeak / crossover traps
            var simpsons = factory.Open("The Simpsons");

            var typo = new[]
            {
                Parse(@"C:\dl\th3 s1mpsons - 07.mkv"),
                Parse(@"C:\dl\S1mpsons-S01E12.mkv")
            };
            var typoResult = ShowSelector.Select(typo, "The Simpsons", simpsons);
            Check(typoResult.Files.All(f => f.Verdict == FileVerdict.Include),
                "leetspeak typos are rescued by fuzzy word evidence",
                string.Join(", ", typoResult.Files.Select(f => $"{f.File.FileName}={f.Verdict} ({f.Score:0.000})")));

            var crossover = new List<ParseResult>
            {
                Parse(@"C:\dl\The Simpsons - S01E01.mkv"),
                Parse(@"C:\dl\Family Guy - The Simpsons Guy (S13E01).mkv"),
                Parse(@"C:\dl\Family Guy - S01E01.mkv")
            };
            var crossResult = ShowSelector.Select(crossover, "The Simpsons", simpsons);
            var crossBy = crossResult.Files.ToDictionary(f => f.File.FileName, f => f);

            Eq(FileVerdict.Include, crossBy["The Simpsons - S01E01.mkv"].Verdict,
                "real episode included even when a crossover is present");
            Eq(FileVerdict.Review, crossBy["Family Guy - The Simpsons Guy (S13E01).mkv"].Verdict,
                "crossover episode is never moved silently",
                crossBy["Family Guy - The Simpsons Guy (S13E01).mkv"].Score.ToString("0.000"));
            Check(crossBy["Family Guy - The Simpsons Guy (S13E01).mkv"].CompetingClaim,
                "crossover flagged as claimed by two shows");
            Eq(FileVerdict.Review, crossBy["Family Guy - S01E01.mkv"].Verdict,
                "plain Family Guy episode goes to review, not the plan");

            // semantic lookalikes: famous cartoons score high against each other
            var decoy = new List<ParseResult>
            {
                Parse(@"C:\dl\The Simpsons - S01E02.mkv"),
                Parse(@"C:\dl\[SubsPlease] Futurama - 1x02.mkv"),
                Parse(@"C:\dl\24 - S01E01.mkv")
            };
            var decoyResult = ShowSelector.Select(decoy, "The Simpsons", simpsons);
            var decoyBy = decoyResult.Files.ToDictionary(f => f.File.FileName, f => f);

            Check(decoyBy["[SubsPlease] Futurama - 1x02.mkv"].Verdict != FileVerdict.Include,
                "Futurama is never silently included",
                $"{decoyBy["[SubsPlease] Futurama - 1x02.mkv"].Verdict} ({decoyBy["[SubsPlease] Futurama - 1x02.mkv"].Score:0.000})");
            Eq(FileVerdict.Exclude, decoyBy["24 - S01E01.mkv"].Verdict,
                "junk title \"S E\" cannot pass the lexical guard",
                decoyBy["24 - S01E01.mkv"].Score.ToString("0.000"));
            Check(!decoyBy["24 - S01E01.mkv"].LexicalMatch,
                "single-letter tokens give no lexical evidence");

            // ------------------------------------------------------- fuzzy fallback
            var fuzzy = new FuzzySimilarityFactory().Open("Jujutsu Kaisen");
            Check(fuzzy.Score("Jujutsu Kaisen - 05") >= ShowSelector.IncludeThreshold,
                "fuzzy fallback also clears include for a same show", fuzzy.Score("Jujutsu Kaisen - 05").ToString("0.000"));
            Check(fuzzy.Score("Steins;Gate - 05") < ShowSelector.ReviewThreshold,
                "fuzzy fallback also clears review for a different show", fuzzy.Score("Steins;Gate - 05").ToString("0.000"));
        }
    }

    private static ParseResult Parse(string path) => EpisodeParser.Parse(path);

    // ---------------------------------------------------------------- support

    private static void TestSupportInfo()
    {
        Console.WriteLine("SupportInfo");

        // The shipped payload decodes and verifies.
        var data = SupportInfo.Load();
        Check(data is not null, "shipping payload decodes and passes its seal");
        if (data is null)
            return;

        Eq(2, data.Count, "both banks are present");

        var byBank = data.ToDictionary(b => b.Bank, b => b.Number);
        Check(byBank.ContainsKey("Providus Bank") && byBank.ContainsKey("Access Bank"),
            "banks are Providus and Access", string.Join(", ", byBank.Keys));
        Eq("6505889999", byBank.GetValueOrDefault("Providus Bank", ""), "Providus account number");
        Eq("1698996910", byBank.GetValueOrDefault("Access Bank", ""), "Access account number");

        foreach (var bank in data)
            Check(bank.Number.Length is 10 or 11 && bank.Number.All(char.IsDigit),
                $"{bank.Bank} number looks like an account number", bank.Number);

        // Tampered payload: one character flipped in the base64 blob must fail the seal.
        var payload = SupportInfo.Payload;
        var flipped = payload[..^1] + (payload[^1] == 'A' ? 'B' : 'A');
        Check(SupportInfo.Decode(flipped, SupportInfo.Seal, SupportInfo.ScrambleKey) is null,
            "tampered payload fails the seal");

        // Attacker swaps in their own payload: without the right seal it is rejected.
        var attackerPlain = "Evil Bank|9999999999";
        var attackerBytes = System.Text.Encoding.UTF8.GetBytes(attackerPlain);
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(SupportInfo.ScrambleKey);
        var attackerScrambled = new byte[attackerBytes.Length];
        for (var i = 0; i < attackerBytes.Length; i++)
            attackerScrambled[i] = (byte)(attackerBytes[i] ^ keyBytes[i % keyBytes.Length]);
        var attackerPayload = Convert.ToBase64String(attackerScrambled);

        Check(SupportInfo.Decode(attackerPayload, SupportInfo.Seal, SupportInfo.ScrambleKey) is null,
            "attacker's own account number is rejected against the real seal");

        // Right payload, wrong seal: also rejected.
        Check(SupportInfo.Decode(SupportInfo.Payload, Convert.ToBase64String(new byte[32]), SupportInfo.ScrambleKey) is null,
            "wrong seal is rejected");

        // Right blob, wrong scramble key: garbage never renders.
        Check(SupportInfo.Decode(SupportInfo.Payload, SupportInfo.Seal, "some other key") is null,
            "wrong scramble key is rejected");

        // Malformed base64 fails closed instead of throwing.
        Check(SupportInfo.Decode("not base64!!", SupportInfo.Seal, SupportInfo.ScrambleKey) is null,
            "malformed payload fails closed");

        // An account-shaped number with letters in it is dropped rather than shown.
        var dirtyPlain = "Providus Bank|65058899xx";
        var dirtyBytes = System.Text.Encoding.UTF8.GetBytes(dirtyPlain);
        var dirtyScrambled = new byte[dirtyBytes.Length];
        for (var i = 0; i < dirtyBytes.Length; i++)
            dirtyScrambled[i] = (byte)(dirtyBytes[i] ^ keyBytes[i % keyBytes.Length]);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var dirtySeal = Convert.ToBase64String(sha.ComputeHash(dirtyBytes));
        Check(SupportInfo.Decode(Convert.ToBase64String(dirtyScrambled), dirtySeal, SupportInfo.ScrambleKey) is null,
            "non-numeric account number is dropped (section stays hidden)");

        // A valid payload with a good seal round-trips.
        var cleanPlain = "Access Bank|1698996910";
        var cleanBytes = System.Text.Encoding.UTF8.GetBytes(cleanPlain);
        var cleanScrambled = new byte[cleanBytes.Length];
        for (var i = 0; i < cleanBytes.Length; i++)
            cleanScrambled[i] = (byte)(cleanBytes[i] ^ keyBytes[i % keyBytes.Length]);
        var cleanSeal = Convert.ToBase64String(sha.ComputeHash(cleanBytes));
        var roundTrip = SupportInfo.Decode(Convert.ToBase64String(cleanScrambled), cleanSeal, SupportInfo.ScrambleKey);
        Check(roundTrip is not null, "well-formed payload with matching seal round-trips");
        Eq("1698996910", roundTrip?.Count > 0 ? roundTrip[0].Number : "",
            "round-trip keeps the account number");
    }

    /// <summary>Walks up from the test binary until it finds the app's Models folder.</summary>
    private static string? FindModelsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MediaSorterApp", "Models");
            if (File.Exists(Path.Combine(candidate, "encoder.onnx")))
                return candidate;

            dir = dir.Parent;
        }

        return null;
    }
}
