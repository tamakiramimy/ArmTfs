# arm-tfs-vscode 执行看板
开始时间：2026-06-05

- [ ] 任务总进度: 0/7
- 待办列表:
    - [ ] [阶段1] 修复 sidebar.ts 无限循环 bug（`finally` 中 `fire()` + 错误未缓存）
    - [ ] [阶段2] 重新编译 C# ArmTfs.Cli（osx-arm64，包含 ItemsCommand）
    - [ ] [阶段3] 清除 macOS quarantine 标记
    - [ ] [阶段4] 编译 TypeScript 验证零错误
    - [ ] [阶段5] 验证 arm-tfs.dll items list 命令可用
    - [ ] [阶段6] 验证 Server Explorer 树状展开不循环
    - [ ] [阶段7] 最终总结报告

---
## 问题清单（已分析）

### BUG-1: sidebar.ts 无限循环
- **位置**: `ServerExplorerProvider.loadChildren()` finally 块
- **原因**: `finally { this.onDidChangeTreeDataEmitter.fire() }` → VS Code 重新调用 `getChildren` → 无缓存（error 路径没写缓存）→ 再次 `loadChildren` → 再次 error → 无限循环
- **修复方案**: 
  1. error 路径也写入 `childCache`（防止重试）
  2. 删除 `finally` 中的 `fire()`
  3. 删除 `inFlight` 字段（async getChildren 天然串行，不需要）
  4. `childCache` 类型改为 `Map<string, vscode.TreeItem[]>`（兼容 error 节点）

### BUG-2: C# ItemsCommand 未编译进旧 DLL
- **原因**: 配置了 CLI 路径后，DLL 是旧版构建，不含 `items list` 命令
- **修复方案**: `dotnet build -c Release -r osx-arm64`

### BUG-3: macOS quarantine
- **原因**: dylib 文件带有 `com.apple.quarantine` 扩展属性，macOS 拒绝加载
- **修复方案**: `xattr -rd com.apple.quarantine <osx-arm64目录>`
