# arm-tfs thin adapter

This VS Code extension is intentionally thin. It delegates all TFVC work to the `arm-tfs` CLI and only adds:

- CLI discovery and launch
- typed JSON parsing for stable command envelopes
- simple commands that open JSON results in the editor
- an exported `ArmTfsCliClient` for future tree views or webviews

## Settings

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

## Build

```bash
npm install
npm run compile
```