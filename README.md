# CallNotes

A fast, local terminal call-note app for Windows Terminal and PowerShell.

## Run

Install Rust from <https://rustup.rs>, then from this folder:

```powershell
cargo run
```

The SQLite database is created as `callnotes.db` beside the executable.

## Keys

- `Ctrl+N`: new call and focus notes
- `Ctrl+S`: save current call
- `F2`: finish current call
- `Ctrl+Q`: save and quit
- `Tab`: next field
- `Shift+Tab`: previous field
- `Up/Down`, `PageUp/PageDown`: scroll call history
- `Ctrl+1`: insert `BMC Engineer`
- `Ctrl+2`: insert `Coforge`
- `Ctrl+3`: insert `INC-`
