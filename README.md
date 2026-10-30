# Notepad Classic Port

A C# WinForms (.NET 10) reimplementation of the classic Windows Notepad
(`notepad.exe` 10.0.26100.8457), written from a study of the original's behavior and checked
against the original running side by side. No Microsoft code, icons or binary resources are
copied; behavior is ported, not code. `docs/parity.md` lists every behavior, where the port
implements it, its tests, and whether the port matches the original or differs on purpose.

## Layout

| Path | Contents |
|---|---|
| `src/NotepadClassic.Core` | Pure .NET: encoding rules, document logic, search, printing layout, command line, settings model. No WinForms, no P/Invoke (enforced by `BannedSymbols.txt`) |
| `src/NotepadClassic.Platform` | Win32 implementations of the Core interfaces (`LibraryImport`, COM file dialogs) |
| `src/NotepadClassic.App` | WinForms shell `NotepadClassicPort.exe`: main window, USER32 Edit control host, menus, two-tier keys, status bar |
| `tests/NotepadClassic.Core.Tests` | Unit tests, coverage gate 90% line and branch |
| `tests/NotepadClassic.Platform.Tests` | Integration tests against real Win32 APIs, golden files in `TestData/` |
| `tests/NotepadClassic.App.Tests` | Shell tests on an STA thread with the real Edit control and fake dialogs |
| `tests/NotepadClassic.E2E.Tests` | FlaUI (UIA3) end-to-end tests on the built exe with a temporary settings hive and autosave folder |
| `tests/NotepadClassic.ParityProbes` | Console tool, not run in CI: re-checks observations of the original Notepad (saves and restores its registry key) |
| `bench/NotepadClassic.Benchmarks` | BenchmarkDotNet over the hot paths (load, find, replace all, save) |
| `bench/NotepadClassic.PerfHarness` | Out-of-process startup, load and Find timings, same method for the port and the original |
| `assets/icon.svg` | Source of the app icon (drawn for this project) |
| `docs/parity.md` | Every documented behavior of the original, where it is implemented, its test, and whether the port matches |
| `docs/performance.md` | Budgets, measurements and how to repeat them |
| `docs/local-install.md` | How the release build is installed as the `notepad` command (PowerShell, cmd, Win+R) on the development machine, and how to undo it |
| `tools/` | Scripts that regenerate test data and `app.ico` (`New-AppIcon.ps1`, `IconGen/`) |

## Build and test

```
dotnet build NotepadClassicPort.slnx -c Release
dotnet test --solution NotepadClassicPort.slnx -c Release
```

The end-to-end tests drive the real window with the keyboard for about a minute; leave the desktop alone while
they run, or skip them with `dotnet test --project` on the other test projects.

Core coverage with the CI gate:

```
dotnet test --project tests/NotepadClassic.Core.Tests -c Release -- --coverlet --coverlet-include "[NotepadClassic.Core]*" --coverlet-threshold 90 --coverlet-threshold-type line,branch --coverlet-threshold-stat Total
```

Run the app: `dotnet run --project src/NotepadClassic.App -c Release`. For a release build with
ReadyToRun code (faster startup): `dotnet publish src/NotepadClassic.App -c Release -r win-x64`.
To make `notepad` open the port, see `docs/local-install.md`.
Setting `NOTEPADCLASSICPORT_STARTUP_TRACE` to a file path makes the app append a startup timeline
(milliseconds since process start per step, JIT counters) to that file; see `docs/performance.md`.

Settings live in `HKCU\Software\NotepadClassicPort` (machine-wide default font in
`HKLM\Software\NotepadClassicPort\DefaultFonts`). On the first run, valid values of the original's
`HKCU\Software\Microsoft\Notepad` are copied once; that key is never written. Setting
`NOTEPADCLASSICPORT_SETTINGS_HIVE` to a file path makes the app keep every settings key in that
application hive instead (end-to-end tests use it).

When Restart Manager closes the app (Windows Update, an installer), unsaved text is kept in
`%LOCALAPPDATA%\NotepadClassicPort\Autosave` (`<id>.json` manifest and `<id>.txt` raw UTF-16 LE) and restored when
Windows starts the app again with `RestartByRestartManager:<id>`. A normal logoff or shutdown still asks to save.
`NOTEPADCLASSICPORT_AUTOSAVE_DIR` names another folder (tests use temporary ones).

Links the original hard-codes are read from `AssemblyMetadata` in the app project: `HelpUrl`, `FeedbackUrl` and
`PageSetupHelpUrl` (the "Input Values" link of Page Setup). An empty value hides the menu item or the link.

Printing tests use "Microsoft Print to PDF" with the output sent to a temporary file; no other printer is used.

Tests never touch the real registry or user files: settings tests use temporary hives
(`RegLoadAppKey`), file system tests use temporary directories.
