# NorthCommander Learning Guide

This guide explains how NorthCommander is built and how it was developed with an AI co-pilot.
It is meant for anyone who wants to understand the code, extend it, or use the same approach for
their own project.

## 1. What NorthCommander is

An orthodox (dual-pane) file manager for Windows in the tradition of other legendary File Commander applications: two file panels side by side, everything reachable from the keyboard, file operations
always going "from the active panel to the other one".

| Area | Choice |
| --- | --- |
| Language / runtime | C# on .NET 10 (`global.json`) |
| UI framework | Avalonia UI 12 (cross-platform XAML, Windows is the primary target) |
| Pattern | MVVM with `CommunityToolkit.Mvvm` source generators |
| Composition | `Microsoft.Extensions.DependencyInjection` |
| Tests | xUnit, `tests/NorthCommander.Core.Tests` |
| Code quality | `TreatWarningsAsErrors`, nullable reference types, `.editorconfig` |

## 2. System architecture

```
┌──────────────────────────── NorthCommander.UI (Avalonia, MVVM) ────────────────────────────┐
│ Views (.axaml)           MainWindow, FilePanelView, ListerView, MultiRenameView,            │
│                          SyncDirectoriesView, TransferDialogView, ConflictDialogView, ...   │
│        │ data binding                                                                       │
│ ViewModels               MainWindowViewModel ─┬─ FilePanelViewModel (left) ── TabViewModel* │
│                                               ├─ FilePanelViewModel (right) ─ TabViewModel* │
│                                               ├─ ListerViewModel, MultiRenameViewModel,     │
│                                               │  SyncDirectoriesViewModel, dialogs          │
│                                               └─ TransferQueueViewModel                     │
│ Services                 ShellIntegration (Explorer, Properties: Windows only)              │
└────────────────────────────────────────┬────────────────────────────────────────────────────┘
                                         │ depends on (never the other way round)
┌────────────────────────────── NorthCommander.Core (no UI references) ──────────────────────┐
│ Services/    DirectoryService (async listing), FileSystemOperations, CommandRunner          │
│ Transfers/   BackgroundTransferQueue, FileTransferTask, Copy/Move operations, conflicts     │
│ Archives/    ArchiveService + ArchiveReader (virtual folders), Extract/AddToArchive ops     │
│ Viewer/      MappedFileSource, TextPager, HexFormatter                                      │
│ Rename/      RenameMask (TC tokens), MultiRename                                            │
│ Sync/        DirectoryComparer                                                              │
│ Settings/    AppSettings, SettingsStore (NorthCommander.config.json)                        │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

**The main rule:** Core contains all logic and has no reference to Avalonia. That keeps it fast to
test (`dotnet test` needs no window) and lets the UI stay thin: views bind to view models, view
models call Core.

### 2.1 Panels and tabs

- `MainWindowViewModel` owns the two `FilePanelViewModel`s, tracks the active one and handles the
  global commands (F-keys, command line, dialogs).
- Each `FilePanelViewModel` holds a collection of `TabViewModel`s. A tab owns everything about one
  folder view: path, listing, cursor, marked items, history and lock state. Views bind to
  `SelectedTab.*`.
- A locked tab never leaves its folder; navigating away opens a new tab instead.

### 2.2 Directory listing

`DirectoryService` enumerates on the thread pool with `DirectoryInfo.EnumerateFileSystemInfos`,
skips entries it can't access and returns folders first, then files. A new navigation cancels the
previous one, so a slow network folder never blocks the UI or shows stale results.

### 2.3 Background transfers

```
F5/F6 ─▶ TransferDialog ─▶ FileTransferBatch ─▶ BackgroundTransferQueue ─▶ worker(s)
                                                    │                         │
                                       pause / resume / cancel        IFileOperation
                                                                  (Copy, Move, Extract, Pack)
                                                                              │
                                              conflict? ─▶ IFileConflictResolver (dialog)
```

- `BackgroundTransferQueue` is a singleton with one worker by default (FIFO); parallelism is
  configurable.
- Copies stream through a pooled 1 MiB buffer. Overwrites go to a temporary file first, so the
  existing file survives a failed or cancelled copy, and partial output is always deleted.
- Moves try a fast same-volume rename and fall back to copy + delete.
- A `FileTransferBatch` groups one F5/F6, so "Overwrite All" / "Skip All" apply to that batch only.

### 2.4 Lister (F3)

Multi-gigabyte files open instantly because nothing reads the whole file:

- `MappedFileSource` memory-maps a 4 MiB window and slides it as you scroll.
- `TextPager` decodes only the visible page, scrolling by byte offset instead of building a line
  index. Very long lines are broken every 8 KiB so scrolling backwards stays fast.
- `HexFormatter` renders `Offset | 16 hex bytes | ASCII` rows.

### 2.5 Archives as folders

A path that continues past an archive file points inside it (`C:\backup.7z\docs\a.txt`).
`ArchiveService` resolves such paths, so tabs, `..`, F3, F5 and sync work unchanged. `ArchiveReader`
hides the format: ZIP uses System.IO.Compression; 7z, RAR and TAR use SharpCompress random access;
compressed TARs and solid RARs are read front to back, once per extraction. Only ZIPs can be written:
adding files rebuilds the archive into a temporary file and swaps it in only on success. Entries with
`..` or absolute paths are ignored (zip-slip protection).

### 2.6 Drag and drop

All drags, between panels or with other apps, use the operating system's drag and drop. Dragging
out hands real files to Explorer, the Desktop or any other app. Files dropped onto a panel, a folder
row or an open ZIP go through the normal Copy/Move dialog and the background queue (Shift = Move).
NorthCommander always reports the drop as a copy to the source app, so Explorer never deletes the
originals before the move is confirmed and has run. Files inside archives can only be dropped onto
NorthCommander panels, not dragged out.

### 2.7 Folder sizes

`FolderSizeCalculator` adds up a folder recursively on the thread pool, skipping folders it can't
read and not following symlinks/junctions. Inside archives it sums the entries below the virtual
path. Each tab keeps a queue of folders to measure (Space, F3 on a folder, Alt+Shift+Enter for all),
works through it in the background and cancels it on Esc or when you navigate away. The result
replaces `<DIR>` in the size column and is included in the status bar summary.

### 2.8 Multi-Rename (Ctrl+M) and Sync (Ctrl+Shift+S)

- `RenameMask` expands Total Commander tokens (`[N]`, `[N2-5]`, `[E]`, `[C]`, ...). `MultiRename`
  previews every new name, flags conflicts and renames in two phases so swaps (`a→b`, `b→a`) work.
- `DirectoryComparer` matches both sides by relative path, then by time (optional 2 s tolerance
  for FAT32) and size, producing `->`, `<-`, `!=` or `=`. Synchronize turns the arrows into queue
  operations.

### 2.9 Command line

`CommandRunner` runs the typed command via `cmd.exe /c` in the active panel's folder, without a
console window, and shows the output in the Lister. `cd` is handled by the panel itself, because a
`cd` in a child process would have no effect.

### 2.10 Portability

`SettingsStore` saves tabs, locks, window state, masks and sync options to
`NorthCommander.config.json` next to `NorthCommander.exe` (written atomically via a temp file).
The app never uses AppData or the registry. `install.ps1` only adds shortcuts and a user PATH
entry; `uninstall.ps1` removes them again.

### 2.11 Look and feel

All colours are defined in `src/NorthCommander.UI/Themes/RetroTheme.axaml` using the Norton
Commander palette: blue `#0000A8` panels, yellow `#FFFF55` marked items, cyan `#55FFFF` focus.

## 3. Build, test, release

```sh
dotnet build
dotnet test
dotnet run --project src/NorthCommander.UI
```

- `.github/workflows/build.yml` builds and tests every push.
- Pushing a tag like `v1.0.0` runs `.github/workflows/release.yml`, which publishes a self-contained
  single-file `NorthCommander.exe` (win-x64) and attaches a zip to a GitHub release.

## 4. How it was built with an AI co-pilot

NorthCommander was developed by a human product owner working with Devin, an AI software engineer.
The human decided what to build and in which order; the AI wrote the code, tests and docs, ran
them, and reported back. The git history shows each step as one commit.

### 4.1 The phases

| Step | Feature | Commit message prefix |
| --- | --- | --- |
| 1 | Solution scaffold, async directory service, two panels | `Phase 1` |
| 2 | Background transfer queue with pause/resume/cancel | `Phase 2` |
| 3 | Multi-selection and the overwrite/skip/rename dialog | `Phase 2.3` |
| 4 | Folder tabs and tab locking | `Phase 2.1` |
| 5 | Norton Commander theme, mouse, context menu, drag and drop | `Retro Norton Commander theme` |
| 6 | Rename CommanderTom → NorthCommander | `Rename application` |
| 7 | F3 Lister | `F3 Lister` |
| 8 | Ctrl+M Multi-Rename | `Ctrl+M multi-rename` |
| 9 | ZIP virtual file system | `ZIP virtual file system` |
| 10 | Directory synchronization | `Directory synchronization` |
| 11 | Command line, F-key bar, release workflow | `Command line` |
| 12 | Portable settings, install/uninstall scripts, PATH | `Portable settings`, `install.ps1` |
| 13 | Drag and drop with Explorer and other apps | `Drag and drop` |
| 14 | 7z, RAR and TAR archives (read-only) | `Browse 7z, RAR and TAR` |
| 15 | Folder size calculation | `Folder size calculation` |

### 4.2 What worked well

1. **One feature per prompt, written as a spec.** Each request named the goal, the requirements
   as a numbered list and the expected behaviour ("Esc closes the viewer and returns focus to the
   panel"). Clear acceptance criteria led to code that could be checked against them.
2. **Fix the architecture early.** The first prompt set the stack and the Core/UI split. Every later
   feature had an obvious place to live, and Core stayed testable.
3. **Tests and a clean build as the gate.** Warnings are errors, and every step ended with
   `dotnet build` and `dotnet test` passing (108 tests at the end). Regressions showed up
   immediately instead of three features later.
4. **Try it like a user.** Beyond unit tests, the AI ran the app on its machine and tried each
   feature by hand (opening a 2.4 GB file, dragging between panels, renaming real files) and sent
   screenshots.
5. **Let the AI surface decisions.** Each report ended with "decisions for you", the points where
   the spec was ambiguous or where the AI deviated (e.g. images opening directly in image mode, as
   in Total Commander). The human could accept or redirect without reading the whole diff.
6. **Small, reviewable commits.** One commit per step makes it easy to see how a feature was added
   and to revert it if needed.

### 4.3 Limits to keep in mind

- **Platform gaps.** The AI worked on Linux. Cross-platform code was tested there, but
  Windows-only parts (Explorer integration, `install.ps1`, the Start Menu shortcut, the PATH
  change) were only syntax-checked and linted, and need a test on a real Windows machine.
- **Verify claims.** Treat "it works" as "it worked in the scenario that was tested". The reports
  say what was tested and where; read that part.
- **Be explicit about trade-offs.** Things like "adding to a ZIP rewrites the whole archive"
  are reasonable defaults, but only the product owner can say whether they're acceptable.

### 4.4 A prompt template that worked

```
Let's implement <feature> for NorthCommander.

Requirements:
1. <Architecture / where the logic lives (Core vs UI)>
2. <Behaviour, including keys and edge cases>
3. <UI and theme expectations>
4. <Error handling>

Please implement <logic> in NorthCommander.Core with tests, build the UI,
and wire up <shortcut>.
```

## 5. Where to go next

- A password prompt for encrypted archives (they currently show an error).
- Writing 7z archives (would need 7-Zip's native `7z.dll`); RAR creation is proprietary.
- The full Explorer context menu (Windows-only).
- Recycle Bin delete was deliberately left out for now: F8 deletes permanently, after confirmation.
