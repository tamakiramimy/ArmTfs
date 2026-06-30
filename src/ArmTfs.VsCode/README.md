# arm-tfs thin adapter

This VS Code extension is intentionally thin. It delegates all TFVC work to the `arm-tfs` CLI and only adds:

- CLI discovery and launch
- typed JSON parsing for stable command envelopes
- branch, history, merge, and server-explorer views over the CLI JSON API
- native VS Code diff editors for source-vs-target comparisons
- native VS Code conflict-marker editors for manual merge resolution
- an exported `ArmTfsCliClient` for future tree views or webviews

## Settings

arm-tfs extension settings are stored in a fixed user-level JSON file instead of the
current VS Code workspace settings:

- macOS: `~/Library/Application Support/arm-tfs/vscode-config.json`
- Windows: `%APPDATA%\arm-tfs\vscode-config.json`
- Linux: `${XDG_CONFIG_HOME:-~/.config}/arm-tfs/vscode-config.json`

Existing VS Code `armTfs.*` settings are migrated into that file on activation and kept
as a backward-compatible fallback.

- `armTfs.cli.command`: explicit command, for example `dotnet` or `/custom/path/arm-tfs`
- `armTfs.cli.commandArgs`: prefix arguments, for example `["/path/to/arm-tfs.dll"]`
- `armTfs.cli.cwd`: working directory for CLI execution
- `armTfs.cli.preferWorkspaceBuild`: prefer a workspace-local build before `arm-tfs` on PATH

## Commands

- `arm-tfs: Configure CLI Command`
- `arm-tfs: Show Status JSON`
- `arm-tfs: Show History JSON`
- `arm-tfs: Show Diff JSON`
- `arm-tfs: Show Branch JSON`
- `arm-tfs: Show Changeset JSON`
- `arm-tfs: Show Label JSON`
- `arm-tfs: Show Merge Base JSON`
- `arm-tfs: Show Merge Candidates JSON`

## Merge Workbench

The merge workbench loads per-changeset previews, then asks the server for one SOAP range dry-run
so all conflicts in the selected range are visible before execution. Source/target conflict choices
are passed to the CLI and resolved with SOAP `Resolve`. Manual resolution opens a temporary result
file with standard conflict markers, so VS Code's built-in conflict actions can be used directly.

## Build

```bash
npm install
npm run compile
```
