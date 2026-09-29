using System.IO;
using System.Text;
using MediaSorter.Engine;

namespace MediaSorter.ViewModels;

/// <summary>
/// Drives the whole conversation: three questions, a scan, inline human-in-the-loop
/// episode prompts, a plan review, and the actual move/copy.
/// Every prompt is awaited, so the flow reads top-to-bottom like the script it is.
/// </summary>
public sealed class ChatSession
{
    private const string CancelAnswer = "\u0001cancel";

    private readonly ChatViewModel _vm;
    private readonly List<List<MoveRecord>> _undoStack = new();

    /// <summary>The local embedding model, loaded once on first selection and shared by every run.</summary>
    private static readonly Lazy<(ISimilarityFactory Factory, string? Warning)> SimilarityFactory = new(() =>
        Similarity.Load(Path.Combine(AppContext.BaseDirectory, "Models")));

    private TaskCompletionSource<string>? _pending;
    private bool _pendingAcceptsText;
    private bool _running;
    private bool _restartRequested;
    private CancellationTokenSource? _cts;

    private string? _showDir;

    public ChatSession(ChatViewModel vm) => _vm = vm;

    /// <summary>True while a prompt is waiting for something the user can type.</summary>
    public bool AcceptsTypedAnswer => _pending is not null && _pendingAcceptsText;

    private static string DownloadsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // ================================================================== entry

    public void StartFlow()
    {
        if (_running)
        {
            _vm.Add("A run is already going. Press Cancel run or type /cancel to stop it.", ChatRole.System);
            return;
        }

        _ = RunGuardedAsync();
    }

    private async Task RunGuardedAsync()
    {
        _running = true;
        _cts = new CancellationTokenSource();
        _vm.CancelEnabled = true;

        try
        {
            await RunFlowAsync();
        }
        catch (OperationCanceledException)
        {
            if (!_restartRequested)
                _vm.Bot("Cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            _vm.Add($"Something went wrong: {ex.Message}", ChatRole.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pending = null;
            _pendingAcceptsText = false;
            _vm.ClearProgress();
            _vm.StepLabel = "Ready";
            _running = false;
            _vm.CancelEnabled = false;

            if (_restartRequested)
            {
                _restartRequested = false;
                StartFlow();
            }
        }
    }

    private async Task RunFlowAsync()
    {
        var token = _cts!.Token;

        _vm.Bot(Greeting());

        // ------------------------------------------------------------ step 1
        _vm.StepLabel = "Step 1 of 3 · Source folder";

        (string Label, string Value)[] step1Options = Directory.Exists(DownloadsDirectory)
            ? new[] { ("My Downloads folder", DownloadsDirectory) }
            : Array.Empty<(string Label, string Value)>();

        var sourceDir = await AskDirectoryAsync(
            "Step 1 of 3: Source folder\nPaste the folder that has your video files, or use a quick option below.",
            "Enter a folder that exists (you can also paste the path of a file inside it):",
            mustExist: true,
            step1Options);

        _vm.Add($"Source: {sourceDir}", ChatRole.System);

        // ------------------------------------------------------------ step 2
        _vm.StepLabel = "Step 2 of 3 · Show name";
        var showName = await AskShowNameAsync();

        // ------------------------------------------------------------ step 3
        _vm.StepLabel = "Step 3 of 3 · Target folder";

        var targetDir = await AskDirectoryAsync(
            $"Step 3 of 3: Target directory\nWhere should \"{showName}\" be created?",
            "Enter a target folder (its parent folder must already exist):",
            mustExist: false,
            ("Organize inside the source folder", sourceDir));

        _showDir = OrganizationEngine.ResolveShowDirectory(targetDir, showName, out var reusedFolder, out var reuseScore);

        if (reusedFolder is not null)
        {
            var typedFolder = OrganizationEngine.Sanitize(showName);
            var matchedPath = Path.Combine(targetDir, reusedFolder);

            if (string.Equals(reusedFolder, typedFolder, StringComparison.Ordinal))
            {
                // Already named exactly what the user typed — nothing to decide.
                _vm.Add($"Found an existing folder \"{reusedFolder}\" ({reuseScore}% match). We're reusing it instead of creating a duplicate. Its video files are included in this run.", ChatRole.Success);
            }
            else
            {
                _vm.Add($"Found an existing folder \"{reusedFolder}\" ({reuseScore}% match). It isn't named \"{typedFolder}\" exactly.", ChatRole.Success);

                var isEmpty = Directory.Exists(matchedPath) && Directory.GetFileSystemEntries(matchedPath).Length == 0;
                var caseOnly = string.Equals(reusedFolder, typedFolder, StringComparison.OrdinalIgnoreCase);

                var options = new List<(string Label, string Value)>
                {
                    ($"Use \"{reusedFolder}\" as-is", "reuse")
                };

                // Deleting only ever applies to an empty folder; a non-empty one is offered
                // a rename instead (rename moves everything with it — no data can be lost).
                if (isEmpty)
                    options.Add(($"Delete it and create \"{typedFolder}\"", "delete"));
                else
                    options.Add(($"Rename it to \"{typedFolder}\"", "rename"));

                // A brand-new folder can only coexist with the old one when the names differ
                // by more than case (Windows folders are case-insensitive).
                if (!caseOnly)
                    options.Add(($"Create \"{typedFolder}\" new (leave \"{reusedFolder}\" alone)", "fresh"));

                var choice = await AskAsync($"What would you like to do with \"{reusedFolder}\"?", false, options.ToArray());
                var typedPath = Path.Combine(targetDir, typedFolder);

                switch (choice)
                {
                    case "rename":
                        try
                        {
                            RenameDirectory(matchedPath, typedPath);
                            _showDir = typedPath;
                            _vm.Add($"Renamed \"{reusedFolder}\" to \"{typedFolder}\". Everything inside it moved with it, and its video files are included in this run.", ChatRole.Success);
                        }
                        catch (Exception ex)
                        {
                            _vm.Add($"Couldn't rename it ({ex.Message}). The folder may be open in another window or program. Close it and try again. Reusing \"{reusedFolder}\" as-is.", ChatRole.Warning);
                        }
                        break;

                    case "delete":
                        try
                        {
                            Directory.Delete(matchedPath, recursive: false);
                            _showDir = typedPath;
                            _vm.Add($"Deleted the empty folder \"{reusedFolder}\". \"{typedFolder}\" will be created for your files.", ChatRole.Success);
                        }
                        catch (Exception ex)
                        {
                            _vm.Add($"Couldn't delete it ({ex.Message}). The folder may be open in another window or program. Close it and try again. Reusing \"{reusedFolder}\" as-is.", ChatRole.Warning);
                        }
                        break;

                    case "fresh":
                        if (Directory.Exists(typedPath))
                        {
                            _vm.Add($"\"{typedFolder}\" already exists. Reusing \"{reusedFolder}\" as-is.", ChatRole.Warning);
                        }
                        else
                        {
                            _showDir = typedPath;
                            _vm.Add($"Keeping \"{reusedFolder}\" untouched. A new folder \"{typedFolder}\" will be created.", ChatRole.System);
                        }
                        break;

                    default: // reuse as-is
                        _vm.Add($"Reusing \"{reusedFolder}\" as-is. Its video files are included in this run.", ChatRole.Success);
                        break;
                }
            }
        }
        else if (!Directory.Exists(targetDir))
            _vm.Add($"Target directory will be created: {targetDir}", ChatRole.System);

        // ----------------------------------------------------------- scanning
        _vm.StepLabel = "Scanning source files";
        _vm.Bot(
            $"Looking for video files ({string.Join("  ", VideoScanner.DefaultExtensions)}) in:\n{sourceDir}\n" +
            (_vm.IncludeSubfolders ? "including subfolders" : "this folder only (subfolders are off)"));

        _vm.SetProgress(0, "Scanning source folder…", "");

        IProgress<int> scanProgress = new Progress<int>(found =>
            _vm.SetProgress(0, $"Scanning source folder… {found} file(s) found", ""));

        // No destination-based exclusion: hiding the show folder from the scan would also
        // hide raw, not-yet-organized files that happen to live inside it (e.g. a downloads
        // folder named after the show that got reused as the destination). BuildPlan already
        // classifies finished files per file ("skip — already organized"), so nothing is lost
        // by scanning everything.
        var files = await Task.Run(() => VideoScanner.Scan(sourceDir, _vm.IncludeSubfolders, onProgress: p => scanProgress.Report(p)), token);

        _vm.ClearProgress();

        if (files.Count == 0)
        {
            _vm.Add($"No video files found in {sourceDir}.", ChatRole.Warning);
            _vm.Bot("You can point me somewhere else, or start over.");
            var again = await AskAsync("What would you like to do?", false,
                ("Choose another folder", "restart"),
                ("Show commands", "help"));

            if (again == "restart")
                _restartRequested = true;
            else
                PrintHelp();

            return;
        }

        _vm.Add($"Found {files.Count} video file(s).", ChatRole.Success);

        // ------------------------------------------------------------ parsing
        _vm.StepLabel = "Parsing file names";
        var (parsed, stripped) = await ParseAsync(files, token);
        _vm.ClearProgress();

        ReportParsedTags(parsed, stripped);
        token.ThrowIfCancellationRequested();

        // ---------------------------------------------------------- selection
        var chosen = await SelectAsync(parsed, showName, token);
        if (chosen is null)
            return;

        if (chosen.Count == 0)
        {
            _vm.Bot("No files were selected, so there's nothing to organize.");
            await AskStartOverAsync();
            return;
        }

        // ----------------------------------------------- episode confirmation
        _vm.StepLabel = "Confirming episodes";
        var confirmed = await ConfirmAsync(chosen, token);
        _vm.ClearProgress();

        if (confirmed.Count == 0)
        {
            _vm.Bot("Every file was skipped, so there's nothing to organize.");
            await AskStartOverAsync();
            return;
        }

        token.ThrowIfCancellationRequested();

        // --------------------------------------------------------------- plan
        _vm.StepLabel = "Review the plan";
        var copy = !_vm.MoveFiles;
        var plan = OrganizationEngine.BuildPlan(confirmed, _showDir!, showName, copy, out var warnings);

        foreach (var warning in warnings)
            _vm.Add(warning, ChatRole.Warning);

        _vm.Bot(BuildPlanSummary(plan, _showDir!, copy));

        var decision = await AskAsync(
            "Ready? Nothing on disk changes until you approve the plan.",
            false,
            (copy ? "Apply plan (copy files)" : "Apply plan (move files)", "apply"),
            ("Cancel", "cancel"));

        if (decision != "apply")
        {
            _vm.Bot("Plan discarded. Nothing was changed.");
            _restartRequested = true;
            return;
        }

        // ------------------------------------------------------------ execute
        await ExecuteAsync(plan, token);

        // ------------------------------------------------------------- finish
        while (true)
        {
            var options = new List<(string Label, string Value)>
            {
                ("Organize another folder", "restart")
            };

            if (_undoStack.Count > 0)
                options.Insert(0, ("Undo the last batch", "undo"));

            var next = await AskAsync("What's next?", false, options.ToArray());

            if (next == "restart")
            {
                _restartRequested = true;
                return;
            }

            DoUndo();
            _vm.Bot("Reverted. Anything else?");
        }
    }

    // ================================================================= parsing

    private async Task<(List<ParseResult> parsed, HashSet<string> stripped)> ParseAsync(
        IReadOnlyList<string> files,
        CancellationToken token)
    {
        IProgress<int> progress = new Progress<int>(found =>
            _vm.SetProgress(found * 100.0 / files.Count, $"Reading {found} of {files.Count} filename(s)…"));

        var (parsed, stripped) = await Task.Run(() =>
        {
            var results = new List<ParseResult>(files.Count);
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var result = EpisodeParser.Parse(files[i]);

                foreach (var tag in result.StrippedTags)
                    tags.Add(tag);

                results.Add(result);

                if (i % 5 == 0 || i == files.Count - 1)
                    progress.Report(i + 1);
            }

            return (results, tags);
        }, token);

        for (var i = 0; i < parsed.Count; i++)
        {
            if (i < 30 || (i + 1) % 10 == 0)
                _vm.Add($"parsed   {parsed[i].FileName}  →  {parsed[i].Describe()}", ChatRole.System);
        }

        return (parsed, stripped);
    }

    // =============================================================== selection

    /// <summary>
    /// Runs the selection gate: every file is scored against the typed show name by the
    /// local embedding model. Skipped files are listed with reasons, the middle band is
    /// resolved by one grouped prompt. Returns null when the user cancelled.
    /// </summary>
    private async Task<List<FileSelection>?> SelectAsync(IReadOnlyList<ParseResult> parsed, string showName, CancellationToken token)
    {
        _vm.StepLabel = "Matching files to the show name";

        var progress = new Progress<double>(percent =>
            _vm.SetProgress(percent, $"Matching files against \"{showName}\"… {percent:0}%"));

        var (similarity, selection) = await Task.Run(() =>
        {
            var factory = SimilarityFactory.Value;
            var model = factory.Factory.Open(showName);
            return (factory, ShowSelector.Select(parsed, showName, model, progress));
        }, token);

        _vm.ClearProgress();

        if (similarity.Warning is not null)
            _vm.Add(similarity.Warning, ChatRole.Warning);

        var reviews = selection.Reviews.ToList();
        var excluded = selection.Excluded.ToList();

        if (excluded.Count > 0)
        {
            var shown = excluded
                .Take(8)
                .Select(f => $"    {f.File.FileName}\n        {f.Reason}");

            var extra = excluded.Count > 8 ? $"\n    … and {excluded.Count - 8} more" : "";

            _vm.Add(
                $"Not \"{showName}\". Left alone ({excluded.Count} file(s)):\n{string.Join("\n", shown)}{extra}",
                ChatRole.System);
        }

        if (selection.Included.Count() == 0 && reviews.Count == 0)
        {
            _vm.Add(
                $"Nothing in this folder looks like \"{showName}\". Closest was \"{selection.BestMatchText}\" " +
                $"({Percent(selection.BestScore)}%).",
                ChatRole.Warning);

            var answer = await AskAsync("What would you like to do?", false,
                ($"Use all {parsed.Count} file(s) anyway", "useall"),
                ("Cancel", "cancel"));

            if (answer == "cancel")
            {
                _vm.Bot("Plan discarded. Nothing was changed.");
                _restartRequested = true;
                return null;
            }

            foreach (var file in selection.Files)
            {
                file.Verdict = FileVerdict.Include;
                file.Reason = null;
            }

            _vm.Add("Using every file as requested.", ChatRole.System);
        }
        else if (reviews.Count > 0)
        {
            var lines = reviews.Select(f => $"    {f.File.FileName} ({f.Percent}%)");

            var answer = await AskAsync(
                $"{reviews.Count} file(s) don't clearly match \"{showName}\":\n{string.Join("\n", lines)}\n\n" +
                "Include these files?",
                false,
                ($"Include {reviews.Count}", "include"),
                ($"Exclude {reviews.Count}", "exclude"),
                ("Cancel", "cancel"));

            if (answer == "cancel")
            {
                _vm.Bot("Plan discarded. Nothing was changed.");
                _restartRequested = true;
                return null;
            }

            var include = answer == "include";
            foreach (var file in reviews)
                file.Verdict = include ? FileVerdict.Include : FileVerdict.Exclude;
        }

        var chosen = selection.Files
            .Where(f => f.Verdict == FileVerdict.Include)
            .ToList();

        if (chosen.Count > 0)
            _vm.Add(
                $"Selected {chosen.Count} of {parsed.Count} file(s) for \"{showName}\". Best match: " +
                $"\"{selection.BestMatchText}\" ({Percent(selection.BestScore)}%).",
                ChatRole.Success);

        return chosen;
    }

    // ================================================ episode confirmation loop

    /// <summary>The spec's inline human-in-the-loop prompt — now only for selected files.</summary>
    private async Task<List<ParseResult>> ConfirmAsync(IReadOnlyList<FileSelection> chosen, CancellationToken token)
    {
        var confirmed = new List<ParseResult>(chosen.Count);

        for (var i = 0; i < chosen.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            _vm.SetProgress(i * 100.0 / chosen.Count, $"Checking {i + 1} of {chosen.Count} file(s)…");

            var result = chosen[i].File;

            if (result.NeedsConfirmation && !await ConfirmEpisodeAsync(result))
                continue;

            confirmed.Add(result);
        }

        return confirmed;
    }

    private void ReportParsedTags(IReadOnlyList<ParseResult> parsed, HashSet<string> stripped)
    {
        if (stripped.Count == 0)
            return;

        var tags = stripped.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", tags.Take(12));

        if (tags.Count > 12)
            shown += $"  +{tags.Count - 12} more";

        _vm.Add($"Removed {tags.Count} quality or release tag(s) from {parsed.Count} file name(s):\n{shown}", ChatRole.System);
    }

    /// <summary>The inline human-in-the-loop prompt required by the spec.</summary>
    private async Task<bool> ConfirmEpisodeAsync(ParseResult result)
    {
        var prompt = result.GuessLabel is not null
            ? $"Unsure about file: {result.FileName}. Is this {result.GuessLabel}?"
            : $"Unsure about file: {result.FileName}. I couldn't read an episode number from the name.";

        (string Label, string Value)[] options = result.GuessLabel is not null
            ? new[] { ("Yes", "yes"), ("Specify Season & Episode", "specify"), ("Skip", "skip") }
            : new[] { ("Specify Season & Episode", "specify"), ("Skip", "skip") };

        while (true)
        {
            var answer = await AskAsync(prompt, false, options);

            if (answer == "skip")
            {
                _vm.Add($"skipped   {result.FileName}", ChatRole.System);
                return false;
            }

            if (answer == "yes")
            {
                EpisodeParser.Confirm(result);
                _vm.Add($"confirmed   {result.FileName}  →  {result.Describe()}", ChatRole.Success);
                return true;
            }

            // ---- specify
            _vm.Add($"Enter the season and episode for {result.FileName}. Examples: 2x5, S02E05, 2 5, or just 5.", ChatRole.System);
            var typed = await AskAsync("Season and episode:", true);

            if (EpisodeParser.TryParseSeasonEpisode(typed, out var season, out var episode))
            {
                EpisodeParser.ApplyUserDecision(result, season, episode);
                _vm.Add($"confirmed   {result.FileName}  →  {result.Describe()}", ChatRole.Success);
                return true;
            }

            _vm.Add($"Couldn't read \"{typed}\". Use a form like 2x5, S02E05 or \"2 5\".", ChatRole.Error);
        }
    }

    // ============================================================== execution

    private async Task ExecuteAsync(IReadOnlyList<PlanEntry> plan, CancellationToken token)
    {
        var undoBatch = new List<MoveRecord>();
        var lines = 0;

        _vm.StepLabel = "Applying plan";
        _vm.SetProgress(0, $"Preparing {plan.Count} file operation(s)…");

        var progress = new Progress<ProgressInfo>(p =>
        {
            _vm.SetProgress(p.Percent, p.Message);

            if (lines < 40)
                _vm.Add(p.Message, p.IsError ? ChatRole.Error : ChatRole.System);
            else if (lines % 25 == 0)
                _vm.Add($"… {p.Percent:0}% complete", ChatRole.System);

            lines++;
        });

        var result = await Task.Run(() => OrganizationEngine.Execute(plan, progress, token, undoBatch), token);

        _vm.ClearProgress();
        _vm.StepLabel = "Done";

        if (undoBatch.Count > 0)
            _undoStack.Add(undoBatch);

        var sb = new StringBuilder();

        if (result.Moved > 0)
            sb.Append($"Done. Moved {result.Moved} file(s)");
        else if (result.Copied > 0)
            sb.Append($"Done. Copied {result.Copied} file(s)");
        else
            sb.Append("Done. Nothing needed moving");

        sb.Append($" in {result.ElapsedSeconds:0.0}s.");

        if (result.Skipped > 0)
            sb.Append($" {result.Skipped} skipped.");
        if (result.Failed > 0)
            sb.Append($" {result.Failed} failed.");

        sb.AppendLine();
        sb.Append("Folder: ").Append(_showDir);

        if (result.Moved > 0)
            sb.AppendLine().Append("Type /undo to revert this batch.");

        if (result.Errors.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine("Problems:");
            foreach (var error in result.Errors.Take(10))
                sb.AppendLine("  • " + error);
        }

        _vm.Add(sb.ToString(), result.Failed > 0 ? ChatRole.Warning : ChatRole.Success);
    }

    private void DoUndo()
    {
        if (_undoStack.Count == 0)
        {
            _vm.Add("There's nothing left to undo.", ChatRole.System);
            return;
        }

        var batch = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        var (restored, errors) = OrganizationEngine.Undo(batch);

        foreach (var error in errors)
            _vm.Add(error, ChatRole.Warning);

        _vm.Add($"Reverted {restored} file(s).", restored > 0 ? ChatRole.Success : ChatRole.System);
    }

    // ================================================================= prompts

    private async Task<string> AskDirectoryAsync(
        string prompt,
        string retryPrompt,
        bool mustExist,
        params (string Label, string Value)[] options)
    {
        var current = prompt;

        while (true)
        {
            var typed = await AskAsync(current, true, options);

            if (TryResolveDirectory(typed, mustExist, out var directory, out var adjusted, out var error))
            {
                if (adjusted)
                    _vm.Add($"Using: {directory}", ChatRole.System);

                return directory;
            }

            _vm.Add(error!, ChatRole.Error);
            current = retryPrompt;
        }
    }

    private async Task<string> AskShowNameAsync()
    {
        var current = "Step 2 of 3: Target show name\nWhat should the show folder be called? (for example, Jujutsu Kaisen)";

        while (true)
        {
            var typed = (await AskAsync(current, true)).Trim().Trim('"');

            if (typed.Length == 0)
            {
                _vm.Add("The show name can't be empty.", ChatRole.Error);
                current = "Enter the show name:";
                continue;
            }

            if (typed is "." or "..")
            {
                _vm.Add("That isn't a usable folder name.", ChatRole.Error);
                current = "Enter the show name:";
                continue;
            }

            var sanitized = OrganizationEngine.Sanitize(typed);
            if (!string.Equals(sanitized, typed, StringComparison.Ordinal))
            {
                _vm.Add($"Removed characters Windows doesn't allow in folder names. Using \"{sanitized}\".", ChatRole.System);
                typed = sanitized;
            }

            return typed;
        }
    }

    /// <summary>Posts a prompt and suspends the flow until a quick reply or typed answer arrives.</summary>
    private async Task<string> AskAsync(string prompt, bool acceptsText, params (string Label, string Value)[] options)
    {
        // Every prompt carries a Cancel button so the user can bail out from anywhere —
        // skipped only when the caller already provides its own (the plan prompt does).
        if (!options.Any(o => string.Equals(o.Label, "Cancel", StringComparison.OrdinalIgnoreCase)))
            options = options.Append(("Cancel", CancelAnswer)).ToArray();

        var message = _vm.Bot(prompt);

        foreach (var (label, value) in options)
        {
            var reply = new QuickReply(label, value);
            reply.Command = new RelayCommand(() => OnQuickReply(message, reply));
            message.AddReply(reply);
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        _pendingAcceptsText = acceptsText;

        try
        {
            var answer = await tcs.Task;

            if (answer == CancelAnswer)
                throw new OperationCanceledException();

            return answer;
        }
        finally
        {
            if (ReferenceEquals(_pending, tcs))
            {
                _pending = null;
                _pendingAcceptsText = false;
            }

            message.ClearReplies();
        }
    }

    // ================================================================= input

    public void OnTextInput(string text)
    {
        text = text.Trim();

        if (text.Length == 0)
            return;

        if (TryHandleCommand(text))
            return;

        if (_pending is { } pending && _pendingAcceptsText)
        {
            _vm.User(text);
            pending.TrySetResult(text);
            return;
        }

        _vm.User(text);

        if (_pending is not null)
            _vm.Add("Please use one of the options above (or type /help).", ChatRole.System);
        else
            _vm.Add("Nothing is waiting for input. Type /help to see what I can do.", ChatRole.System);
    }

    public void OnQuickReply(ChatMessage message, QuickReply reply)
    {
        if (!message.HasReplies)
            return;

        message.ClearReplies();
        _vm.User(reply.Label);
        _pending?.TrySetResult(reply.Value);
    }

    // =============================================================== commands

    private bool TryHandleCommand(string text)
    {
        var command = text;

        if (!command.StartsWith('/'))
        {
            command = command.ToLowerInvariant() switch
            {
                "help" => "/help",
                "undo" => "/undo",
                "cancel" => "/cancel",
                "start" or "new" or "restart" => "/start",
                _ => ""
            };

            if (command.Length == 0)
                return false;
        }

        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1].Trim() : "";

        switch (name)
        {
            case "/help":
                PrintHelp();
                return true;

            case "/cancel":
                if (_running)
                    CancelCurrent();
                else
                    _vm.Add("Nothing to cancel.", ChatRole.System);
                return true;

            case "/undo":
                DoUndo();
                return true;

            case "/start":
            case "/new":
                if (_running)
                {
                    _vm.Bot("Restarting…");
                    _restartRequested = true;
                    CancelCurrent();
                }
                else
                {
                    StartFlow();
                }
                return true;

            case "/seasons":
                CreateSeasonFolders(argument);
                return true;

            case "/move":
                _vm.MoveFiles = true;
                _vm.Add("Mode: MOVE. Files will be moved out of the source folder.", ChatRole.System);
                return true;

            case "/copy":
                _vm.MoveFiles = false;
                _vm.Add("Mode: COPY. Originals stay where they are.", ChatRole.System);
                return true;

            case "/subfolders":
                _vm.IncludeSubfolders = !_vm.IncludeSubfolders;
                _vm.Add($"Include subfolders: {(_vm.IncludeSubfolders ? "on" : "off")}", ChatRole.System);
                return true;

            case "/support":
                if (!_vm.HasSupport)
                {
                    _vm.Add("Support details aren't available in this copy of the app.", ChatRole.System);
                    return true;
                }

                _vm.ShowSupport = !_vm.ShowSupport;
                if (!_vm.ShowSupport)
                    _vm.Add("Support section hidden.", ChatRole.System);
                return true;

            default:
                _vm.Add($"Unknown command \"{parts[0]}\". Type /help for the list.", ChatRole.Error);
                return true;
        }
    }

    private void CancelCurrent()
    {
        _cts?.Cancel();

        var pending = _pending;
        _pending = null;
        _pendingAcceptsText = false;

        if (pending is not null)
            pending.TrySetResult(CancelAnswer);
    }

    /// <summary>Wired to the persistent "Cancel run" button — identical to /cancel.</summary>
    public void OnCancelClicked()
    {
        if (_running)
            CancelCurrent();
        else
            _vm.Add("Nothing to cancel.", ChatRole.System);
    }

    private void PrintHelp()
    {
        _vm.Bot(
            "Commands\n" +
            "  /help              this list\n" +
            "  /start             begin a new organize run\n" +
            "  /cancel            stop the current step\n" +
            "  /undo              revert the last batch of moves\n" +
            "  /move | /copy      choose whether files are moved or copied\n" +
            "  /subfolders        toggle scanning of nested folders\n" +
            "  /support           show or hide the support section\n" +
            "  /seasons 5         create Season 01…05 for the current show\n" +
            "  /seasons 3,6,9     create only those seasons\n" +
            "  /seasons 5 only    create only Season 05\n" +
            "\n" +
            "Inline prompts also accept typed answers such as 2x5, S02E05 or \"2 5\".\n" +
            "You can drop a folder onto this window at any time.");
    }

    private void CreateSeasonFolders(string argument)
    {
        if (_showDir is null)
        {
            _vm.Bot("I need a show folder first. Go through steps 1 to 3, then run /seasons again.");
            return;
        }

        var seasons = ParseSeasonArgument(argument);

        if (seasons.Count == 0)
        {
            _vm.Add("Usage: /seasons 5   |   /seasons 3,6,9   |   /seasons 5 only", ChatRole.Error);
            return;
        }

        var created = new List<string>();

        foreach (var number in seasons)
        {
            var directory = Path.Combine(_showDir, $"Season {number:D2}");
            if (Directory.Exists(directory))
                continue;

            OrganizationEngine.EnsureDirectory(directory);
            created.Add($"Season {number:D2}");
        }

        _vm.Add(
            created.Count == 0
                ? $"All of those season folders already exist in {_showDir}."
                : $"Created {created.Count} folder(s) in {_showDir}: {string.Join(", ", created)}",
            ChatRole.Success);
    }

    /// <summary>Same grammar the original console app accepted.</summary>
    private static List<int> ParseSeasonArgument(string input)
    {
        var seasons = new List<int>();
        input = input.Trim();

        if (input.Length == 0)
            return seasons;

        if (input.Contains(','))
        {
            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var n) && n > 0)
                    seasons.Add(n);
            }
        }
        else if (input.EndsWith("only", StringComparison.OrdinalIgnoreCase))
        {
            var numberPart = input[..^4].Trim();
            if (int.TryParse(numberPart, out var n) && n > 0)
                seasons.Add(n);
        }
        else if (int.TryParse(input, out var total) && total > 0)
        {
            for (var i = 1; i <= total; i++)
                seasons.Add(i);
        }

        return seasons.Distinct().OrderBy(n => n).ToList();
    }

    // ================================================================ helpers

    private static bool TryResolveDirectory(string raw, bool mustExist, out string directory, out bool adjusted, out string? error)
    {
        directory = "";
        adjusted = false;
        error = null;

        var original = raw.Trim().Trim('"');
        var value = original;

        if (value.StartsWith("~"))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[1..].TrimStart('\\', '/'));

        if (value.Length == 0)
        {
            error = "Please enter a folder path.";
            return false;
        }

        try
        {
            // Accepting a file path and using its folder is a nice convenience.
            if (File.Exists(value))
                value = Path.GetDirectoryName(value)!;

            value = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            error = $"That isn't a valid path ({ex.Message}).";
            return false;
        }

        if (Directory.Exists(value))
        {
            directory = Path.TrimEndingDirectorySeparator(value);
            adjusted = !string.Equals(Path.TrimEndingDirectorySeparator(original), directory, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        if (mustExist)
        {
            error = $"Folder not found: {value}";
            return false;
        }

        var parent = Path.GetDirectoryName(value);

        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            error = $"Can't create that folder because it has no existing parent: {parent}";
            return false;
        }

        directory = Path.TrimEndingDirectorySeparator(value);
        adjusted = !string.Equals(Path.TrimEndingDirectorySeparator(original), directory, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string Greeting() =>
        "Hi! I turn a messy downloads folder into a tidy show folder:\n" +
        "    Target Folder / Show Name / Season 01 / Show Name - S01E01.mkv\n" +
        "\n" +
        "Three quick questions, then I show you a plan. Nothing moves until you approve it.\n" +
        "Type /help for commands.";

    private async Task AskStartOverAsync()
    {
        var again = await AskAsync("Start over?", false,
            ("Yes, start over", "restart"),
            ("No, I'm done", "done"));

        if (again == "restart")
            _restartRequested = true;
    }

    private static int Percent(double score) => (int)Math.Round(score * 100);

    /// <summary>
    /// Renames a directory. <c>Directory.Move</c> rejects a case-only rename ("source and
    /// destination path must be different"), so those take a detour through a temporary
    /// name — with a rollback attempt if the second leg fails.
    /// </summary>
    private static void RenameDirectory(string from, string to)
    {
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }

        var temp = from + ".__renaming__" + Guid.NewGuid().ToString("N")[..8];
        Directory.Move(from, temp);

        try
        {
            Directory.Move(temp, to);
        }
        catch
        {
            try { Directory.Move(temp, from); } catch { /* best effort */ }
            throw;
        }
    }

    private static string BuildPlanSummary(IReadOnlyList<PlanEntry> plan, string showDirectory, bool copy)
    {
        var sb = new StringBuilder();

        var pending = plan.Count(p => p.Action != PlanAction.Skip);

        if (pending == 0 && plan.Count > 0)
            sb.AppendLine($"All {plan.Count} file(s) are already organized. Nothing to do.").AppendLine();

        sb.Append("Plan: ").Append(pending).Append(" file(s) to ")
          .Append(copy ? "copy into" : "move into").AppendLine(":");
        sb.AppendLine(showDirectory);
        sb.AppendLine();

        var lines = new List<string>();

        foreach (var group in plan.GroupBy(p => p.File.SeasonFolder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"    {group.Key}   ({group.Count()} file(s))");

            foreach (var entry in group)
            {
                lines.Add($"      {entry.File.FileName}");

                lines.Add(entry.Action == PlanAction.Skip
                    ? $"        skip: {entry.Note}"
                    : $"        → {Path.GetRelativePath(showDirectory, entry.DestinationPath)}");
            }
        }

        foreach (var line in lines.Take(34))
            sb.AppendLine(line);

        if (lines.Count > 34)
            sb.AppendLine($"    … and {lines.Count - 34} more line(s)");

        sb.AppendLine();
        sb.Append("Mode: ").AppendLine(copy ? "COPY (originals stay where they are)" : "MOVE (originals leave the source folder)");
        sb.Append("Removed quality tags from ").Append(plan.Count).AppendLine(" file name(s). Season defaults to 01 when not stated.");

        return sb.ToString();
    }
}
