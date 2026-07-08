# ARM TFS v0.5.2 Release Files

## 发布日期
2026-07-08

## 本次发布产物

### VSCode 扩展
- `arm-tfs-vscode-0.5.2.vsix`
- `arm-tfs-vscode.vsix`

### CLI 运行时
- `arm-tfs-0.5.2-osx-arm64.zip`
- `arm-tfs-0.5.2-osx-x64.zip`
- `arm-tfs-0.5.2-win-arm64.zip`
- `arm-tfs-0.5.2-win-x64.zip`

## 平台目录

- `macos-arm64/`
- `macos-x64/`
- `windows-arm64/`
- `windows-x64/`

## 主要修复

- 统一 TFVC 文件菜单和行为，覆盖 VS Code 资源管理器、Arm TFS Explorer、Arm TFS 变更视图。
- 修复 `撤销 / 回退 / 暂存 / 取消暂存` 在 TFVC 变更中的实际语义与提交流程。
- 修复 `查看差异` 与 `查看历史记录`，统一打开正确的 TFVC 文件历史。
- 移除扩展启动时残留的 GUI smoke 自动执行逻辑。
- 收敛分支徽章查询，避免无关分支触发多余历史查询与日志噪音。

## 安装

### VSCode 扩展
```bash
code --install-extension arm-tfs-vscode-0.5.2.vsix --force
```

### CLI
```bash
unzip arm-tfs-0.5.2-osx-arm64.zip
cd macos-arm64
./arm-tfs --help
```

## 说明

- `arm-tfs-vscode.vsix` 是当前最新版别名。
- 4 个平台 zip 都来自 `dotnet publish` 的自包含发布产物。
