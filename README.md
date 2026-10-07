# CallNotes

A lightweight, keyboard-driven terminal app for recording support calls. Calls are stored locally; no accounts, network services, or installer are required.

<img width="1235" height="644" alt="image" src="https://github.com/user-attachments/assets/503af581-f411-401c-bf38-822f9df692dd" />


## Quick start

Run `CallNotes.exe` from PowerShell, Command Prompt, or Windows Terminal.

By default, call history and settings are saved in a `data` folder beside the executable. This keeps the generic release self-contained and separate from other CallNotes data. To use a different folder for one run:

```powershell
.\CallNotes.exe --data-dir "D:\CallNotesData"
```

Build from source on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-CallNotes.ps1
```

The build uses the C# compiler included with .NET Framework on Windows. The app itself is a standalone executable and does not require PowerShell.

## Features

- Create calls instantly with a timestamp; notes save automatically.
- The header counts calls started today, and visible call numbers restart each day; older calls remain in history.
- Record a caller, number, location, incident reference, call type, and notes.
- Start with generic call types: Support, Internal, and Other.
- Browse and search call history, then jump directly to a result.
- Edit multiline notes and fields with familiar keyboard controls.
- Copy clean plain-text notes or full call details; export a call as Markdown.
- Choose from Reference, Campbell, Nord, One Half Dark, Solarized Dark, Tango Dark, Vintage, and Monochrome themes.
- Configure a bold, colored outline for the active call and customize call-type colors.
- Use grouped settings for general options, call-type colors, appearance, text colors, and storage. Text colors can be customized independently for headings, main text, field labels, status, help/footer, secondary text, and note highlights.
- View call start/end times and duration for completed calls.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` | Create a call using the default type |
| `Ctrl+1`–`Ctrl+3` | Create a Support, Internal, or Other call |
| `Tab` / `Shift+Tab` | Move between the call's fields and notes |
| `F6` | Focus the call type; use `Left` / `Right` to change it |
| `Ctrl+F` | Search calls; select a result and press `Enter` to jump to it |
| `F3` / `F4` | Select the previous / next call |
| `PageUp` / `PageDown` | Scroll through call history |
| Arrow keys, `Home`, `End` | Move the note cursor |
| `Ctrl+Z` / `Ctrl+Shift+Z` | Undo / redo edits |
| `F2` | Finish the selected call |
| `Ctrl+E` | Copy the selected call's notes |
| `Ctrl+C` | Copy the selected call's details and notes |
| `Ctrl+Shift+E` | Export the selected call as Markdown |
| `F10` | Open grouped settings; use `Up` / `Down` to choose and `Left` / `Right` to change options |
| `Ctrl+S` | Save now |
| `Ctrl+Q` | Save and quit |
| `Ctrl+D` | Delete the selected call (confirmation required) |

## Data and diagnostics

Call history is stored in `calls.json`; settings are stored in `settings.json`. Set a different default data folder in settings, or pass `--data-dir <path>` when launching.

Useful command-line options:

```text
CallNotes.exe --version
CallNotes.exe --validate-json <path>
CallNotes.exe --self-test
```
