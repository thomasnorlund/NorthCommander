# CommanderTom

A modern, high-performance orthodox (dual-pane) file manager for Windows, inspired by Total Commander.
Built with C# / .NET 10 and [Avalonia UI](https://avaloniaui.net/) using the MVVM pattern.

> Status: Phase 1 (foundation) – two panels, async directory listing, keyboard navigation.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 (primary target). Linux and macOS also run thanks to Avalonia.

## Build & run

```sh
dotnet build
dotnet run --project src/CommanderTom.UI
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

## Project layout

```
CommanderTom.sln
├── src/CommanderTom.Core          Class library, no UI dependencies
│   ├── Models/                    FileSystemEntry, DirectoryListing
│   └── Services/                  IDirectoryService, DirectoryService
├── src/CommanderTom.UI            Avalonia desktop app (MVVM)
│   ├── ViewModels/                MainWindowViewModel, FilePanelViewModel, FileItemViewModel
│   └── Views/                     MainWindow, FilePanelView
└── tests/CommanderTom.Core.Tests  xUnit tests for Core
```

- **`DirectoryService`** enumerates with `DirectoryInfo.EnumerateFileSystemInfos` on the thread pool,
  skipping inaccessible entries, and returns directories first then files, sorted by name, with a `..` entry.
- **`FilePanelViewModel`** loads directories asynchronously, cancels superseded loads, and keeps the
  previous listing visible on errors.
- **`MainWindowViewModel`** owns the left/right panels and tracks the active one.
- Dependencies are wired with `Microsoft.Extensions.DependencyInjection` in `App.axaml.cs`;
  MVVM plumbing uses `CommunityToolkit.Mvvm` source generators.

## License

[MIT](LICENSE)
