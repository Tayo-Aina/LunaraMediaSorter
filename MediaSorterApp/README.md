# MediaSorterApp Source Code

The Lunara Media Sorter desktop app: a WPF (Windows-only) chat UI on .NET 9 that organizes
video files into `Show Name / Season 01 / Show Name - S01E01.mkv` folders.

The code is split into a UI-free **Engine** (all the file logic) and a **ViewModels** layer
that drives the conversation, so the interesting parts can be tested without a window.

## Files in this directory

### Root

- **`App.xaml` / `App.xaml.cs`**: application theme (flat black palette brushes), control templates
  (flat buttons, chips, input box), the chat-message `DataTemplate`, and a
  `DispatcherUnhandledException` guard that surfaces unexpected errors in the chat instead of
  crashing.
- **`MediaSorterApp.ico`**: the app icon (a white tile with a black folder, matching
  the header logo), embedded through `ApplicationIcon` so the exe, title bar, taskbar, and
  Explorer all show it. Frames at 16/24/32/48/64/128/256 px.
- **`MainWindow.xaml` / `MainWindow.xaml.cs`**: layout: header (step label, Move/Copy toggle,
  include-subfolders checkbox), transcript list, progress strip, and the input bar. The
  code-behind also handles transcript scroll pinning (follow the newest message unless the
  user scrolled away), Enter-to-send, folder drag & drop, and the dark title bar
  (`DwmSetWindowAttribute`).

### `Engine/`: pure logic, no WPF

- **`Models.cs`**: data types (`ParseResult`, `PlanEntry`, `MoveRecord`, `ProgressInfo`, …)
  and `VideoScanner`, which finds `.mkv/.mp4/.avi/.webm` files (optionally recursive).
- **`FilenameCleaner.cs`**: removes quality tags (`1080p`, `x265`, `60fps`), release-group
  brackets, and CRC checksums from filenames, returning what was stripped.
- **`EpisodeParser.cs`**: three-pass parser: precise C# regexes (`SxxEyy`, `1x05`,
  `Season N Episode M`, `Episode N`, `E05`) → AnitomySharp → a standalone-number heuristic.
  Also confidence grading, the `Confirm`/`ApplyUserDecision` hooks used by the inline
  human-in-the-loop prompt, and `TryParseSeasonEpisode` (`2x5`, `S02E05`, `2 5`, `5`).
- **`ShowSelector.cs`**: the selection gate run after parsing, combining three signals per
  file: the embedding score against the typed show name (best of filename / parsed title /
  parent folder), fuzzy **lexical** evidence (every word of the show name must appear in the
  file's text at ≥ 80% fuzzy ratio with comparable token lengths: rescues leetspeak typos,
  blocks famous-but-unrelated shows), and **competing-claim** detection (a name that is close
  to this file but not part of the best match means two shows want it → always `Review`).
  Verdicts: `Include` (score ≥ 0.45 and adjacent to the best match, or lexical evidence),
  `Review` (uncertain or conflicting, answered by one grouped prompt), `Exclude` (below
  0.35, listed with a `looks like "…"` reason).
- **`TextEmbedding.cs`**: the scorer behind the gate: a quantized MiniLM-L6 ONNX model
  (BERT WordPiece tokenizer written by hand, mean pooling, L2 normalization) running fully
  offline on the CPU via ONNX Runtime, plus a FuzzySharp fallback mapped onto the same 0..1
  scale if `Models/` is missing.
- **`FolderMatcher.cs`**: FuzzySharp token-set scoring of parsed titles against existing
  show folders (reuses a close match instead of creating a duplicate). Comparison is
  case-insensitive: on Windows `bluey` and `Bluey` are the same folder, and FuzzySharp's
  raw ratio would otherwise drop case-only pairs below the match threshold for short names.
- **`OrganizationEngine.cs`**: `ResolveShowDirectory`, `BuildPlan` (with collision
  uniquification), `Execute` (move/copy with progress), `Undo` (restores files and removes
  folders the batch created), plus folder/file name sanitizing. `ResolveShowDirectory` reuses
  an existing folder only if its name does **not** carry an episode/season code: token-set
  scoring ignores extra tokens, so a release folder like `… S03E03 Vexations 1080p …-FLUX[TGx]`
  scores ~94 against the show name and must be filtered out or the season would be built
  inside it.

### `ViewModels/`: the conversation

- **`ChatMessage.cs`**: one chat bubble: role, text, quick replies; plus `RelayCommand`.
- **`ChatViewModel.cs`**: the whole view surface: transcript collection, input text,
  step label, the progress strip (status text, percent bar), and the persistent cancel
  command/enable state.
- **`ChatSession.cs`**: the flow script. A `TaskCompletionSource`-based state machine that
  asks the three questions, resolves the show folder (a reused folder that isn't named
  exactly as typed triggers a decision prompt: use as-is / rename / delete-when-empty /
  create new), scans (the destination is *not* excluded from the scan, so files
  inside a reused show folder are found too; the plan marks anything already at its
  destination `skip: already organized`, keeping re-runs idempotent), parses silently, runs
  the selection gate (semantic score +
  fuzzy word evidence + competing-name detection → selected / listed-with-reason / one grouped
  review prompt), awaits
  the inline episode confirmations for the selected files only, then builds and applies the
  plan. Every prompt gets an auto-appended **Cancel** quick reply (unless the caller
  provides its own), and the persistent **Cancel run** button in the input row routes to
  the same `CancelCurrent()` as `/cancel`; it is enabled exactly while a run is in
  progress. Also handles the slash commands (`/help`, `/start`, `/cancel`, `/undo`,
  `/move`, `/copy`, `/subfolders`, `/seasons`).

### Project file

- **`MediaSorterApp.csproj`**: `net9.0-windows` with `UseWPF`, referencing
  `AnitomySharp.NET6` (filename parsing), `FuzzySharp` (token-set matching), and
  `Microsoft.ML.OnnxRuntime` (runs the embedding model). FuzzySharp and Anitomy are
  zero-dependency libraries; ONNX Runtime ships its native library beside the exe.

### Directories

- **`Models/`**: `encoder.onnx` (quantized all-MiniLM-L6-v2, ~22 MB) and `vocab.txt`; copied
  to the build/publish output and loaded lazily on the first selection.
- **`bin/`, `obj/`**: build outputs (ignored by `.gitignore`).
