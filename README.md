# NorthCommander

A modern, high-performance orthodox (dual-pane) file manager for Windows, inspired by legenadry File Commander applications.
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
| `Insert` / `Space` | Select (mark) the item under the cursor and move down; marked items are shown in red. `Space` on a folder also calculates its size |
| `F3` | Lister: view the file under the cursor (`1` Text, `2` Hex, `3` Image, `W` wrap, `Esc` close); on a folder, calculate its size |
| `Alt+Shift+Enter` | Calculate the size of every folder in the panel (in the background; `Esc` cancels) |
| `F5` | Copy marked items (or the item under the cursor) to the other panel (confirm target in overlay) |
| `F6` | Move/rename marked items (or the item under the cursor) to the other panel |
| `Shift+F6` / `F2` | Rename the item under the cursor |
| `F8` / `Delete` | Permanently delete marked items (or the item under the cursor) after confirmation |
| `Shift+F10` / Menu key | Context menu of the item under the cursor |
| `Ctrl+M` | Multi-rename marked items (or the item under the cursor) with TC masks and live preview |
| `Ctrl+Shift+S` | Synchronize directories of the left and right panel (compare, then queue copies) |
| `F4` | Edit file (Notepad on Windows, default app elsewhere) |
| `F7` | Create folder (`a/b` creates nested folders) |
| `F10` / `Alt+X` | Quit |
| typing + `Enter` | Command line: runs in the active panel's folder via `cmd.exe /c` (`/bin/sh -c` on Linux/macOS); `cd` changes the panel folder; output opens in the Lister; `Esc` clears |
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
- Drag and drop with Explorer and other apps: files dropped from Explorer or the Desktop onto a panel (or a
  folder row, or an open ZIP) open the same Copy dialog (`Shift` for Move); NorthCommander does the copy or
  move itself in the background queue. Dragging items out of a panel hands them to the target app as normal
  files (e.g. Explorer copies them). Entries inside an archive can only be dropped onto NorthCommander panels.
- Lister (F3): files are read through a memory-mapped sliding window (`MappedFileSource`), and only the
  visible page is decoded (`TextPager` for UTF-8 text with wrapping, `HexFormatter` for
  `Offset | Hex | ASCII` rows), so multi-gigabyte files open instantly with constant memory.
  PNG/JPG/BMP/GIF files open in image mode, scaled down to fit.
- Multi-rename (Ctrl+M): name/extension masks with `[N]`, `[N2-5]`, `[N3]`, `[N2-]`, `[N-3-]`, `[N2,4]`, `[E]`,
  `[C]` (start/step/digits), plus search & replace. The preview flags invalid characters, duplicates and
  existing targets; Start renames sequentially (swaps like a→b, b→a work) and logs failures per row.
- Archives (shown in magenta) open like folders with Enter/double-click: ZIP, 7z, RAR, TAR and compressed TARs
  (`.tar.gz`/`.tgz`, `.tar.bz2`, `.tar.xz`). The path header shows the type, e.g. `[7Z]`,
  and `..` at the archive root returns to the host folder. F3 views files inside (extracted to a temp file),
  F5 out of an archive extracts in the background queue, and F5 into a ZIP (the other panel shows a
  folder inside a ZIP) adds files/folders. 7z, RAR and TAR archives are read-only (read through SharpCompress),
  and password-protected archives show an error. The archive is rebuilt into a temp file and swapped in only on
  success. Move, rename and delete inside archives are not supported yet.
- Sync dirs (Ctrl+Shift+S): compares both panels' folders (folders inside archives too) by relative path, size and
  modification time (optional 2 s tolerance). `->`/`<-` = newer or missing on the other side, `!=` = same time but
  different size (skipped), `=` = identical. Synchronize queues the copies (overwriting the older file) in the
  background queue, extracting from or packing into ZIPs as needed.
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

## Installation (portable or installed)

Every release on the [Releases](https://github.com/thomasnorlund/NorthCommander/releases) page comes in two
variants, each as a setup program and as a portable zip:

| Variant | Files | Size | Needs |
|---------|-------|------|-------|
| **Full** | `NorthCommander-Setup-<version>.exe`, `NorthCommander-<version>-win-x64.zip` | ~65 MB | nothing (self-contained) |
| **Lite** | `NorthCommander-Setup-Lite-<version>.exe`, `NorthCommander-<version>-win-x64-lite.zip` | ~14 MB | the .NET 10 runtime |

Lite's `NorthCommander.exe` looks for the .NET runtime first in a `dotnet` folder next to itself, then in
`DOTNET_ROOT`, then in the system-wide install. So the runtime can live inside the app folder and travel
with it (portable), or be installed once for the whole PC.

Either variant can be used as an installed app (shortcuts, PATH) or purely portable (unzip and run, e.g.
from a USB stick); in both cases everything lives in the one app folder.

### Checking dependencies

Double-click `Check-Dependencies.cmd` in any NorthCommander folder (release or source clone), or run
`powershell -ExecutionPolicy Bypass -File .\deps.ps1`. It works out what that folder needs and lists what is
present or missing:

| Folder | Needs | Install options (only after you confirm) |
|--------|-------|-------------------------------------------|
| Full release | nothing | none |
| Lite release | .NET 10 runtime | **[A]** into the app folder's `dotnet` subfolder (portable, no admin, no registry) or **[M]** system-wide with winget |
| Source clone | .NET 10 SDK | **[U]** for your user only (`%LOCALAPPDATA%\Microsoft\dotnet`, no admin) or **[M]** system-wide with winget |

Without winget, the app-folder and user options use Microsoft's official `dotnet-install.ps1`.
`deps.ps1 -Check` only reports (exit code 0 = complete, 1 = something missing), and
`deps.ps1 -Yes -Scope App|User|Machine` installs without asking, for scripts. The setup programs below use
the same checks.

### Setup program (recommended)

Download a setup from the Releases page and run it. It needs nothing installed (it runs on .NET Framework 4.8,
part of Windows 10/11) and no administrator rights:

1. **Requirements:** checks for Windows 10/11 64-bit. The full setup needs nothing else. The Lite setup
   shows whether the .NET 10 runtime is present and, if it's missing, asks whether to install it into the
   app folder (portable) or system-wide with winget.
2. **Set up:** unpacks NorthCommander into the folder you choose (default
   `%LOCALAPPDATA%\Programs\NorthCommander`), installs the runtime if you chose to (Lite), then runs
   `install.ps1` there for the Start Menu shortcut, Desktop shortcut and PATH entry (each optional).

Running a newer setup over an existing folder updates it and keeps `NorthCommander.config.json`.
The setup is built by the release workflow (`src/NorthCommander.Setup`, which embeds the zipped app).

### Setup wizard from source

Double-click `Setup.cmd` (in a clone of this repository, or in the portable release zip). It opens a small setup
window (Windows PowerShell + Windows Forms, built into Windows 10/11; no administrator rights needed):

1. **Requirements:** lists what is needed and what is missing (`deps.ps1`): the .NET 10 SDK for a
   source clone, the .NET 10 runtime for a Lite release, nothing for a full release.
2. **Install missing...:** after asking, installs it: the SDK system-wide with winget or for your user only,
   the runtime into the app folder or system-wide. Windows shows its own permission prompt for system-wide
   installs.
3. **Install:** from a source clone it builds a self-contained `NorthCommander.exe` into the app folder
   you choose (default `%LOCALAPPDATA%\Programs\NorthCommander`); from a release zip it uses the exe in
   place, or copies it to another folder. It then runs `install.ps1` there for the shortcuts and PATH
   (each optional).

Running it again updates an existing app folder; `NorthCommander.config.json` is kept.

### Manual

1. Unzip a release zip to any folder, for example `C:\Tools\NorthCommander` or a USB stick. For Lite, run
   `Check-Dependencies.cmd` once and choose **[A]** to put the .NET runtime into that folder too.
2. Optional Windows integration (no administrator rights needed):
   `powershell -ExecutionPolicy Bypass -File .\install.ps1`
   - creates `NorthCommander.lnk` in `%APPDATA%\Microsoft\Windows\Start Menu\Programs` (typing "North" in Windows Search finds it) and on the Desktop (`-NoDesktop` skips it);
   - adds the folder to the user PATH (`-NoPath` skips it), so `northcommander` or `north [folder]` (`north.cmd`) starts it from any new terminal;
   - unblocks the downloaded files in that folder.
   The PATH entry is the only registry change (`HKCU\Environment`, where Windows keeps the user PATH).
3. `uninstall.ps1` removes both shortcuts and the PATH entry. To remove NorthCommander completely, delete its folder.

Settings (tabs and locks, active panel, window size/position, Multi-Rename masks, Sync options) are saved to
`NorthCommander.config.json` next to `NorthCommander.exe` when the app closes, never to AppData or the registry.
If the folder is read-only, NorthCommander still runs but doesn't save settings.

## Releases

Pushing a tag such as `v1.0.0` runs `.github/workflows/release.yml` on Windows: it tests, publishes
`NorthCommander.exe` (win-x64, single file) twice, self-contained with ReadyToRun (full) and
framework-dependent (Lite, plus `NorthCommander.package.json` so `deps.ps1` knows it needs the runtime), zips
each with the scripts and docs, builds a setup exe around each zip, and attaches all four files to a GitHub
release.

Local equivalent:

```sh
dotnet publish src/NorthCommander.UI -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=None -o publish

# Lite (add NorthCommander.package.json, see the workflow)
dotnet publish src/NorthCommander.UI -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o publish-lite
```
