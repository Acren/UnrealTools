# Unreal Tools
Unreal Engine and local automation tools made for personal convenience.

## Formatting

This repository uses `.editorconfig` as the source of truth for C# formatting rules and `dotnet format` as the standard formatter.

Run the formatter across the solution:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build\Format.ps1
```

Verify that the current tree already matches the configured format rules:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build\CheckFormat.ps1
```

OpenCode formats `.cs` files through `opencode.json`, which routes formatting to `Build/Format.ps1` for the edited file.
