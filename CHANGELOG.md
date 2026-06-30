# Changelog

## 0.1.4 - 2026-06-30

### VS Code extension

- Moved `armTfs.*` extension settings out of per-workspace VS Code settings into a fixed
  user-level JSON file:
  - macOS: `~/Library/Application Support/arm-tfs/vscode-config.json`
  - Windows: `%APPDATA%\\arm-tfs\\vscode-config.json`
  - Linux: `${XDG_CONFIG_HOME:-~/.config}/arm-tfs/vscode-config.json`
- Added automatic migration from legacy VS Code `armTfs.*` settings into the new user config
  file while keeping legacy settings as a fallback.
- Moved stored TFS connection profiles, active profile selection, workspace mappings, and PAT
  handling onto the user-config-backed model so they no longer depend on the current VS Code
  folder.
- Updated CLI resolution, language selection, server explorer state, merge settings, history
  settings, branch scope, auto-checkout settings, and local root lookup to read and write from
  the shared user config API.

### TFVC change tracking and cross-platform workspace behavior

- Reworked tracked-file detection to use workspace-relative keys instead of absolute local paths.
  This fixes false positives when the same TFVC workspace is opened on macOS and Windows with
  different local root directories.
- Improved server-path to local-path mapping for mixed macOS and Windows environments and updated
  tracked metadata loading to prefer relative mapping resolution.
- Added server-path-based fallback tracking so changed files already known to TFVC are less likely
  to show up as untracked.
- Excluded `packages` from local untracked scanning to avoid flooding the change list with NuGet
  restore output.
- When recursive server item listing is unavailable, the extension now skips untracked scanning
  instead of showing a large number of false untracked files.

### Change view UX

- Reworked the TFS changes view to follow a more Git-like model:
  - `Pending Changes` / `未提交的变更` for files already in TFVC pending state
  - `Changes` / `变更` for local modifications and untracked files not yet added to pending
- Added bulk operations:
  - add all working changes to pending
  - unstage all pending changes
- Added distinct staged vs destructive actions:
  - stage local modifications by checkout
  - stage untracked files by add
  - unstage via `undo --no-restore`
  - discard pending changes only after an explicit confirmation prompt
- Added comprehensive right-click menus for all change-view actions so every hover button also
  has a discoverable menu entry.
- Reduced inline action clutter to keep high-frequency safe actions visible while moving riskier
  actions into text-labeled context menus.

### Diff and compare improvements

- Added diff support for every change-view file group, including untracked files.
- Changed file compare behavior to use the current server latest version as the default reference.
- Added local-file vs empty-baseline compare fallback when no server version exists for the
  current path.
- Improved local-path to server-path resolution for diff commands using workspace mappings.

### Ignore support

- Added `.tfignore` support and compatibility with `.arm-tfsignore`.
- Implemented gitignore-style matching features:
  - comments with `#`
  - negation with `!`
  - `*`, `?`, and `**` wildcards
  - trailing `/` for directory rules
  - leading `/` for workspace-root-anchored rules
- Added change-view action to ignore a file directly from the UI.
- When ignoring a pending file, the extension first removes it from pending state and then writes
  the ignore rule.

### Localization and docs

- Added and updated English and Simplified Chinese strings for the new staged/unstaged/ignore
  workflows and diff fallback messages.
- Documented the user-level config location in the VS Code extension README.
- Updated VSIX release instructions and version references for `0.1.4`.
- Expanded `.vscodeignore` to exclude local editor files, archives, `.DS_Store`, and sourcemaps
  from packaged VSIX output.

### Versioning and packaging

- Bumped `ArmTfs.Cli`, `ArmTfs.Core`, and the VS Code extension package version from `0.1.0` /
  `0.1.3` to `0.1.4`.
- Updated the extension lockfile to reflect the new package version.
