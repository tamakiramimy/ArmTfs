# Changelog

## 0.5.2 - 2026-07-08

### VS Code extension

- 修复 TFVC 变更文件在不同入口下的菜单和行为不一致问题，统一 `撤销 / 回退 / 暂存 / 取消暂存` 语义。
- 修复 VS Code 文件浏览器、Arm TFS Explorer、Arm TFS 变更视图里的文件操作行为，确保 `Add` 与 `Edit` 的撤销/回退符合 TFVC 语义。
- 修复 `查看差异` 与 `查看历史记录` 路由，统一打开 Arm TFS 历史面板并正确展示选中文件历史。
- 修复 Arm TFS 变更提交过滤逻辑，`取消暂存 / 暂存` 现在会正确影响 checkin 包含文件。
- 移除启动时残留的 GUI smoke 自动执行逻辑。
- 收敛分支徽章的查询范围，避免无关工作区分支触发多余历史查询与调试日志。

### 验证

- 在真实 VS Code 宿主中完成两套工作区的端到端验证，覆盖历史、差异、撤销、回退、取消暂存、暂存与提交流程。

### 发布包

- macOS arm64: `arm-tfs-0.5.2-osx-arm64.zip`
- macOS x64: `arm-tfs-0.5.2-osx-x64.zip`
- Windows arm64: `arm-tfs-0.5.2-win-arm64.zip`
- Windows x64: `arm-tfs-0.5.2-win-x64.zip`
- VSCode 扩展: `arm-tfs-vscode-0.5.2.vsix`

## 0.5.1 - 2026-07-07

### 核心修复

**Checkin SOAP 流程修复**:
- 新增 `UpdateLocalVersionAsync`：服务端工作区在 pend Edit 前必须先设置本地版本基线，否则 upload.ashx 报 ItemNotCheckedOut
- 修复 `UploadFileToWorkspaceAsync`：使用正确的 multipart form-data 格式，包含 item / wsname / wsowner / filelength / MD5 hash / range 字段
- 重构 `CheckInWithContentAsync`：按变更类型分组批量 pend、写本地文件路径、自动检测二进制/文本编码
- 修复 owner 解析：`ResolveOwnerForSoapAsync` 优先读 workspace metadata owner；捕获 TF204017 并用错误中的 GUID 重试

**历史记录时间显示修复（UTC → 本地时间）**:
- 根本原因：TFS SOAP 返回 UTC 时间，但 `DateTimeOffset.TryParse` 对无时区标记的字符串默认按本地时间解析，且 `.DateTime` 属性丢失 Kind 信息，导致 JSON 序列化无 Z 后缀，VS Code 扩展 `toLocaleString()` 无法正确转换时区
- `TfvcSoapClient`：所有 `DateTimeOffset.TryParse` 增加 `AssumeUniversal | AdjustToUniversal`
- `TfvcClientService`：所有 `.DateTime` 改为 `.UtcDateTime`，确保 Kind=UTC
- CLI 所有命令日期显示增加 `.ToLocalTime()` 转换

**模型变更**:
- `SoapItem` 增加 `ItemId` 字段
- 新增 `SoapLocalVersionUpdate` 模型

### 测试
- 补充 QueryItems itemid 属性解析测试
- 补充 UpdateLocalVersion SOAP 报文格式验证测试

### 发布包
- macOS arm64: `arm-tfs-0.5.1-osx-arm64.zip`
- macOS x64: `arm-tfs-0.5.1-osx-x64.zip`
- Windows arm64: `arm-tfs-0.5.1-win-arm64.zip`
- Windows x64: `arm-tfs-0.5.1-win-x64.zip`
- VSCode 扩展: `arm-tfs-vscode-0.5.1.vsix`



### VS Code extension - 手动合并编辑器改进

**修复**:
- 回滚v0.1.7中错误的合并工作台修改（修复了冲突计数bug）
- 切换到使用自定义三栏合并面板替代VSCode原生冲突标记方式

**新增功能**:
- **顶部导航**: 添加"上一个冲突"和"下一个冲突"按钮（带文字标签和图标）
- **底部状态栏**: 实时显示"剩余 X 个冲突"
- **底部导航**: 重复的上一个/下一个冲突按钮
- **底部操作**: 
  - "撤销合并"按钮 - 重置所有冲突选择
  - "完成合并"按钮 - 检查所有冲突是否已解决
- **冲突导航**: 自动滚动到目标冲突并高亮显示1秒
- **计数实时更新**: 解决冲突时自动更新剩余数量

这些改进使手动合并体验更接近原生Visual Studio TFS的合并功能。

### 发布包
- macOS arm64: `arm-tfs-0.1.8-osx-arm64.zip` (75 MB)
- Windows arm64: `arm-tfs-0.1.8-win-arm64.zip` (112 MB)
- VSCode扩展: `arm-tfs-vscode-0.1.8.vsix` (771 KB)

## 0.1.7 - 2026-07-02

### VS Code extension

- **Merge UI improvements for conflict navigation**:
  - Added "Previous Conflict" and "Next Conflict" buttons with text labels to the top of the conflict panel
  - Added bottom status bar showing "Remaining X conflicts" with duplicate navigation buttons
  - Conflict navigation now auto-scrolls to the target conflict and highlights it for 1 second
  - Navigation supports circular scrolling (wraps from last to first conflict)
- **Merge completion actions**:
  - Added "Complete Merge" button that executes the merge when all conflicts are resolved
  - Added "Undo Merge" button that clears all conflict resolutions with user confirmation
  - Both buttons are in the conflict panel footer for easy access
- Removed the old custom manual merge webview fallback so manual conflict resolution now only uses VS Code's native Merge Editor path.
- If the native Merge Editor command cannot be opened, the extension now reports a clear error instead of falling back to the lower-quality custom panel.

### Packaging

- Built release packages for macOS arm64 (`arm-tfs-0.1.7-osx-arm64.zip`, ~75MB)
- Built release packages for Windows arm64 (`arm-tfs-0.1.7-win-arm64.zip`, ~112MB)
- Updated VS Code extension package to version 0.1.7 (`arm-tfs-vscode-0.1.7.vsix`, ~769KB)

## 0.1.6 - 2026-07-02

### VS Code extension

- Fixed Merge Workbench range conflicts being copied onto every changeset; range conflicts are now tracked once globally and shown once in the conflict list.
- Changed manual conflict resolution to open VS Code's native Merge Editor with source branch on the left, target branch on the right, and the editable merged result as output.
- Kept global range conflict resolutions in each selected changeset's resolution file so resolved range conflicts still execute correctly without duplicating the UI badges.
- Excluded local `release/` build folders from VSIX packaging so old CLI release binaries are not embedded in the extension package.

### Validation and packaging

- Revalidated `<project>` to `<project>` with SOAP range dry-run for `csXXXXXX~csXXXXXX`; it completed in about 1.56 seconds and surfaced the two expected `.csproj` conflicts without creating a changeset.
- Bumped `ArmTfs.Cli`, `ArmTfs.Core`, and the VS Code extension package version to `0.1.6`.
- Built `osx-arm64`, `win-arm64`, and VSIX release artifacts for `0.1.6`.

## 0.1.5 - 2026-07-02

### VS Code extension

- Added `armTfs.merge.planConcurrency` to load Merge Workbench per-changeset dry-run plans with bounded parallelism.
- Changed continuous, conflict-free multi-changeset execution to use one SOAP range merge instead of one SOAP merge per changeset.
- Kept conflicted and non-contiguous selections on the existing safer per-changeset execution path.

### Validation and packaging

- Validated SOAP range preview against `<project>` to `<project>`; the full range preview completed in about 1.55 seconds and surfaced two `.csproj` conflicts without creating pending changes.
- Bumped `ArmTfs.Cli`, `ArmTfs.Core`, and the VS Code extension package version to `0.1.5`.

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
