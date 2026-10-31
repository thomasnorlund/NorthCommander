# NorthCommander

A modern, high-performance orthodox (dual-pane) file manager for Windows, inspired by Total Commander.
Built with C# / .NET 10 and [Avalonia UI](https://avaloniaui.net/) using the MVVM pattern.

> Status: Phase 2 – two panels, async directory listing, keyboard navigation, background F5/F6 copy/move queue, TC-style multi-selection and overwrite/skip conflict prompt.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 (primary target). Linux and macOS also run thanks to Avalonia.

## Build & run

```sh
dotnet build
dotnet run --project src/NorthCommander.UI
dotnet test
```

## Keyboard

| Key | Action |
| --- | --- |
| `Tab` | Switch active panel |
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | Move selection |
| `Enter` / double-click | Open folder (or launch file with its default app) |
| `Backspace` | Go to parent folder |
| `Ctrl+R` | Refresh panel |
| `Insert` / `Space` | Select (mark) the item under the cursor and move down; marked items are shown in red |
| `F3` | Lister: view the file under the cursor (`1` Text, `2` Hex, `3` Image, `W` wrap, `Esc` close) |
| `F5` | Copy marked items (or the item under the cursor) to the other panel (confirm target in overlay) |
| `F6` | Move/rename marked items (or the item under the cursor) to the other panel |
| `Shift+F6` / `F2` | Rename the item under the cursor |
| `F8` / `Delete` | Permanently delete marked items (or the item under the cursor) after confirmation |
| `Shift+F10` / Menu key | Context menu of the item under the cursor |
| `Ctrl+M` | Multi-rename marked items (or the item under the cursor) with TC masks and live preview |
| `Ctrl+T` | New folder tab in the active panel (same folder) |
| `Ctrl+W` | Close the current tab (the last tab of a panel stays open); middle-click also closes |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab of the active panel |
| `Enter` / `Esc` (in dialog) | Confirm / cancel |

## Project layout

```
NorthCommander.sln
├── src/NorthCommander.Core          Class library, no UI dependencies
│   ├── Models/                    FileSystemEntry, DirectoryListing
│   ├── Services/                  IDirectoryService, DirectoryService
│   └── Transfers/                 IFileOperation, FileTransferTask, BackgroundTransferQueue, copy/move operations
├── src/NorthCommander.UI            Avalonia desktop app (MVVM)
│   ├── ViewModels/                MainWindowViewModel, FilePanelViewModel, FileItemViewModel
│   └── Views/                     MainWindow, FilePanelView
└── tests/NorthCommander.Core.Tests  xUnit tests for Core
```

- **`DirectoryService`** enumerates with `DirectoryInfo.EnumerateFileSystemInfos` on the thread pool,
  skipping inaccessible entries, and returns directories first then files, sorted by name, with a `..` entry.
- **`FilePanelViewModel`** loads directories asynchronously, cancels superseded loads, and keeps the
  previous listing visible on errors.
- **`MainWindowViewModel`** owns the left/right panels and tracks the active one.
- **`BackgroundTransferQueue`** (singleton) runs `FileTransferTask`s on background workers – one by default
  (FIFO), configurable via `BackgroundTransferQueueOptions.MaxParallelTransfers`. Tasks can be paused,
  resumed and canceled individually or all at once, whether queued or running. Copies stream through a
  pooled 1 MiB buffer, never overwrite existing files, and delete partial output on failure/cancel.
  Moves try a same-volume rename first and fall back to copy + delete.
- Look: Norton Commander colours (blue `#0000A8` panels, white text, yellow `#FFFF55` marked items,
  cyan `#55FFFF` focus, grey `#AAAAAA` inactive). All colours live in `src/NorthCommander.UI/Themes/RetroTheme.axaml`.
- Mouse: double-click opens folders/files; right-drag marks a range of rows (Total Commander style);
  a short right-click opens the context menu (Open, Copy, Move, Rename, Delete, Copy full path,
  Show in Explorer, Properties on Windows). Left-drag items onto the other panel (or onto a folder) to open
  the Copy dialog; hold `Shift` while dropping for Move.
- Lister (F3): files are read through a memory-mapped sliding window (`MappedFileSource`), and only the
  visible page is decoded (`TextPager` for UTF-8 text with wrapping, `HexFormatter` for
  `Offset | Hex | ASCII` rows), so multi-gigabyte files open instantly with constant memory.
  PNG/JPG/BMP/GIF files open in image mode, scaled down to fit.
- Multi-rename (Ctrl+M): name/extension masks with `[N]`, `[N2-5]`, `[N3]`, `[N2-]`, `[N-3-]`, `[N2,4]`, `[E]`,
  `[C]` (start/step/digits), plus search & replace. The preview flags invalid characters, duplicates and
  existing targets; Start renames sequentially (swaps like a→b, b→a work) and logs failures per row.
- Each panel holds folder tabs (`TabViewModel`). Right-click a tab to Lock/Unlock it; a locked tab
  (shown as `*name`) never leaves its folder: entering a subfolder or going up opens a new tab instead.
- When a target file already exists, only that task waits while the conflict dialog asks:
  Overwrite, Overwrite All, Skip, Skip All, Rename (keeps both as `name (2).ext`) or Cancel.
  "All" answers apply to every item of the same F5/F6 (`FileTransferBatch`). Overwrites go through a
  temporary file, so the existing file survives a failed or canceled copy; skipped files stay in the source on move.
- While transfers run, the active panel's status bar shows progress with Pause/Resume and Cancel buttons.
- Dependencies are wired with `Microsoft.Extensions.DependencyInjection` in `App.axaml.cs`;
  MVVM plumbing uses `CommunityToolkit.Mvvm` source generators.

## License

[MIT](LICENSE)
