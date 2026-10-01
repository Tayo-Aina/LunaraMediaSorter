# Lunara Media Sorter

Source code: https://github.com/Tayo-Aina/LunaraMediaSorter

A chat-based Windows desktop app that turns a messy downloads folder into tidy show folders:

```
Target Folder / Jujutsu Kaisen / Season 01 / Jujutsu Kaisen - S01E01.mkv
```

You answer three quick questions in a chat window, the app scans your video files, works out
the season and episode of each one, and shows you a plan. **Nothing on disk changes until you
approve the plan.**

Built with WPF on .NET 9. The "conversation" is a deterministic C# state machine: no LLM,
no network calls, no telemetry. "Does this file belong to the show?" is answered by a small
**local embedding model** (quantized MiniLM, bundled in `Models/`, runs offline on the CPU);
everything else (parsing, planning, moving) stays plain deterministic code.

## Features

- **Selection gate (learned matching)**: before anything is planned, every scanned file is
  checked against the show name you typed with three independent signals: **semantic**
  (a local MiniLM embedding model scores the name vs filename / parsed title / parent
  folder), **lexical** (fuzzy word evidence: `simpsons` matches `s1mpsons`, so leetspeak
  typos are rescued while unrelated shows that merely *sound* similar are not), and
  **conflict** (a file claimed by two names in the folder, e.g. a crossover episode, is
  never decided silently). Files that clearly belong are selected; other shows are left
  untouched and listed with a reason (`looks like "Steins;Gate" (16%)`); everything
  uncertain goes into **one grouped review prompt** instead of being silently decided.
  Without this gate a mixed downloads folder would be filed wholesale into whatever show
  you typed.
- **Three-step chat flow**: source folder → show name → target folder, with quick-reply
  buttons, a Browse button, and drag-and-drop anywhere in the window.
- **Deterministic episode parsing**: precise `SxxEyy` / `1x05` / `Season N Episode M` regexes
  first, [AnitomySharp](https://github.com/erengy/anitomy) second, a standalone-number
  heuristic last. Season defaults to `1` when the name doesn't state one.
- **Quality/tag cleanup**: strips release tags (`1080p`, `x264`, `60fps`), release-group
  brackets, and CRC hashes, and reports what it removed.
- **Human-in-the-loop confirmation**: when a name is ambiguous the app asks inline:
  `Unsure about file: ....mkv. Is this Season 1 Episode 07?` with **Yes / Specify Season &
  Episode / Skip** buttons (typed answers like `2x5`, `S02E05`, `2 5` also work).
- **Fuzzy folder matching**: token-set matching ([FuzzySharp](https://github.com/leachgio2/FuzzySharp))
  reuses an existing show folder instead of creating a duplicate. Matching is
  case-insensitive (Windows folders are), and when the reused folder isn't named exactly
  what you typed the app **asks what to do with it**: use it as-is, rename it to the typed
  name (everything inside moves with it), delete it (only ever offered when it's empty),
  and create a fresh one, or leave it alone and create a new folder. Release- and
  season-named folders (`… S03E03 Vexations 1080p …-FLUX[TGx]`) are never reused as the
  show root, even though they fuzzy-match the show name almost perfectly.
- **Idempotent re-runs**: the destination is never excluded from the scan, so raw files
  sitting inside a folder that gets reused as the show root (e.g. a downloads folder named
  after the show) are found and organized instead of being invisible. Files already exactly
  where they belong are classified per file (`skip: already organized`), so running the
  same flow twice says `All N file(s) are already organized. Nothing to do.` and leaves
  the disk untouched.
- **Plan first, apply later**: a full per-file preview of moves/copies; approve with one
  button, and `/undo` reverts the batch (including removing folders the batch created).
- **Move or copy mode**, **include-subfolders toggle**, and a `/seasons` command that creates
  `Season 01 … Season NN` folders (carried over from the original console app).
- **Live progress**: scanning, parsing, and per-file move status stream into the chat.
- **Cancel from anywhere**: every prompt carries a **Cancel** quick-reply button, and the
  input row holds a persistent **Cancel run** button that stays lit for the whole run
  (and greys out when idle). Both do exactly what `/cancel` does: stop the run, change
  nothing.

## Commands

| Command | What it does |
| --- | --- |
| `/help` | list commands |
| `/start` | begin a new organize run |
| `/cancel` | stop the current step |
| `/undo` | revert the last batch of moves |
| `/move` \| `/copy` | choose whether files are moved or copied |
| `/subfolders` | toggle scanning of nested folders |
| `/seasons 5` | create `Season 01`…`Season 05` for the current show |
| `/seasons 3,6,9` | create only those seasons |
| `/seasons 5 only` | create only `Season 05` |

You can also just type `help`, `undo`, `cancel`, or `start`.

## Getting Started

### Prerequisites

To **build from source** you need the [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

To **run the pre-built executable** (from the Releases page), no prerequisites are needed.
The app is self-contained.

### Run from source

Double-click **`RunMediaSorter.bat`**, or from a terminal:

```bash
cd MediaSorterApp
dotnet run
```

### Build a self-contained executable

Double-click **`PublishSelfContained.bat`**, or from a terminal:

```bash
cd MediaSorterApp
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o ..
```

The published app lands directly in this folder: double-click
**`MediaSorterApp.exe`**. It is fully self-contained: the .NET runtime is bundled
inside the exe, so it runs on any 64-bit Windows PC with nothing installed: no .NET, no
installer, no admin rights. (WPF's native support DLLs, the ONNX runtime, and the
`Models/` folder with the embedding model sit next to the exe; they're part of the app
and must travel with it: copy them together with the exe.)

## Project Structure

```
LunaraMediaSorter/
├── RunMediaSorter.bat              # Run the app from source
├── PublishSelfContained.bat        # Publish the self-contained app into this folder
├── MediaSorterApp.exe        # Published app (self-contained, runtime bundled)
│   (together with Models/, onnxruntime.dll, the WPF native DLLs; they travel as one set)
├── Directory.Build.targets          # Trims foreign-platform ONNX natives from bin/ builds
├── .gitignore
├── README.md
├── Tests/                          # Engine test suite (no UI)
│   ├── EngineTests.csproj
│   └── Program.cs
└── MediaSorterApp/
    ├── App.xaml(.cs)               # Theme, control templates, global exception guard
    ├── MainWindow.xaml(.cs)        # Layout, scroll pinning, drag & drop, dark title bar
    ├── MediaSorterApp.ico    # App icon (white tile, black folder)
    ├── Engine/                     # Pure logic, no WPF dependencies
    │   ├── Models.cs               # ParseResult, PlanEntry, VideoScanner, …
    │   ├── FilenameCleaner.cs      # tag / CRC / release-group stripping
    │   ├── EpisodeParser.cs        # regex → Anitomy → heuristic, user confirmations
    │   ├── ShowSelector.cs         # selection gate: does this file belong to the show?
    │   ├── TextEmbedding.cs        # MiniLM (ONNX) embedder + fuzzy fallback
    │   ├── FolderMatcher.cs        # fuzzy token-set matching against existing folders
    │   └── OrganizationEngine.cs   # scan, plan, move/copy, undo, folder naming
    ├── Models/                     # encoder.onnx + vocab.txt (the local embedder, bundled)
    ├── ViewModels/
    │   ├── ChatMessage.cs          # chat bubbles, quick replies, roles
    │   ├── ChatViewModel.cs        # transcript, input box, progress strip
    │   └── ChatSession.cs          # the conversation state machine (flow script)
    ├── MediaSorterApp.csproj # net9.0-windows, WPF, AnitomySharp + FuzzySharp
    └── README.md                   # Source-file reference
```

## Tests

The parsing/organizing engine is UI-free and covered by a console test suite:

```bash
dotnet run --project Tests/EngineTests.csproj
```

It currently runs **162 assertions** over filename cleanup, episode parsing, fuzzy matching,
planning, move/copy/undo, folder scanning, and the selection gate (embedding score margins,
mixed-folder verdicts, the review band, parent-folder rescue, leetspeak-typo rescue,
crossover/competing-name detection, the single-letter-token guard, and the fuzzy fallback),
plus show-folder resolution (release/season folders are never reused as the show root, and
case-only names like `bluey` for `Bluey` are reused).

## License

This project is provided as-is for personal use.

Bundled third-party components:

- **AnitomySharp.NET6**: filename parsing (MIT-style license, see the package).
- **FuzzySharp**: token-set fuzzy matching (MIT).
- **ONNX Runtime**: runs the embedding model (MIT).
- **all-MiniLM-L6-v2** (`Models/encoder.onnx`, `Models/vocab.txt`): sentence embedding model
  by Microsoft, quantized; licensed under Apache-2.0
  ([model card](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2)).
