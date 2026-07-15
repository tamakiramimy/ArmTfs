# ArmTFS 跨平台路径处理修复文档

## 1. 问题分析

### 1.1 现象

ArmTFS 插件在 macOS arm64 上运行正常，但在 Windows 11 arm64 环境下出现：

- 编译产物路径 `D:\ArmTFS\arm-tfs.exe` 异常
- VS Code 扩展无法定位 CLI 可执行文件
- 工作区映射在跨平台共享卷（Parallels `\\Mac\Home`）场景下匹配失败
- 配置了 `armTfs.cli.command` 后路径被错误截断

### 1.2 根本原因

经源码审计，问题由 **4 个独立的路径处理缺陷叠加** 造成：

#### 缺陷 1：`splitCommandLine` 在 Windows 上把 `\` 当作转义字符

[armTfsCliClient.ts](file:///Y:/ArmTFS/src/ArmTfs.VsCode/src/armTfsCliClient.ts) 第 764-816 行的原实现无条件地将反斜杠视为 POSIX 转义符：

```ts
// 原代码（有 bug）
if (char === '\\') {
  escaping = true;   // 吞掉反斜杠，下一字符当字面量
  continue;
}
```

在 Windows 上，用户配置 `armTfs.cli.command = "dotnet D:\ArmTFS\arm-tfs.dll"` 会被解析为 `["dotnet", "D:ArmTFSarm-tfs.dll"]` —— 所有路径分隔符被吞掉，导致 DLL 路径不存在。这正是 `D:\ArmTFS\arm-tfs.exe` 异常的直接原因。

#### 缺陷 2：`sidebar.ts` 缺少 macOS `/Users/` → Windows `C:\Mac\Home\` 转换

跨平台路径翻译函数 `translatePlatformSharedPath` 在三个 TS 文件中重复实现，但 **`sidebar.ts` 版本不一致**：

| 文件 | 处理 `/Users/foo` → `C:\Mac\Home\foo`（Windows 端）？ |
|------|:---:|
| `tfvcContext.ts` | ✅ |
| `scm.ts` | ✅ |
| `sidebar.ts` | ❌ **缺失** |

当 macOS 端写入的 `workspace.json` 含 `/Users/foo/project` 映射，在 Windows 端打开时，sidebar 分支视图无法解析该路径，导致分支视图静默失败。

#### 缺陷 3：C# `WorkspaceManager.TranslatePlatformSharedPath` 缺少 `/Users/` 转换

[WorkspaceManager.cs](file:///Y:/ArmTFS/src/ArmTfs.Core/Workspace/WorkspaceManager.cs) 第 483-507 行原实现只处理 `Mac/Home/` 前缀的路径，**不处理**裸 `/Users/foo/...` 路径。这意味着 macOS 端写入的 `workspace.json` 在 Windows 端读取时，`LocalToServerPath` 的前缀匹配会失败。

#### 缺陷 4：`FolderProfile.pubxml` 双重嵌套发布目录

[FolderProfile.pubxml](file:///Y:/ArmTFS/src/ArmTfs.Cli/Properties/PublishProfiles/FolderProfile.pubxml) 原配置：

```xml
<PublishDir>bin\Release\net8.0\win-arm64\publish\win-arm64\</PublishDir>
```

这导致 `dotnet publish` 输出到 `bin\Release\net8.0\win-arm64\publish\win-arm64\`（双重嵌套），而 `tryResolveWorkspaceBuild` 只查找 `bin\Release\net8.0\win-arm64\`，找不到产物。

#### 缺陷 5：`GetLocalMappingPreference` 未翻译映射路径

`WorkspaceManager.GetLocalMappingPreference` 直接用 `mapping.LocalPath` 做平台检测，未先调用 `TranslatePlatformSharedPath`。在跨平台共享卷场景下，macOS 端写入的 `/Users/foo/...` 映射在 Windows 端评分时拿不到 `+100` 平台原生加分，导致选错映射。

---

## 2. 解决方案

### 2.1 修复 `splitCommandLine`（Windows 反斜杠保留）

**文件**：[src/ArmTfs.VsCode/src/armTfsCliClient.ts](file:///Y:/ArmTFS/src/ArmTfs.VsCode/src/armTfsCliClient.ts)

**改动**：仅在非 Windows 平台的双引号内保留反斜杠转义语义；Windows 上反斜杠始终作为路径分隔符字面量保留。

```ts
// 修复后
const isWindows = process.platform === 'win32';
// ...
if (char === '\\' && !isWindows && quote === '"') {
  escaping = true;
  continue;
}
```

**效果**：`"dotnet D:\ArmTFS\arm-tfs.dll"` 现在正确解析为 `["dotnet", "D:\\ArmTFS\\arm-tfs.dll"]`。

### 2.2 统一 `translatePlatformSharedPath`（sidebar.ts 对齐）

**文件**：[src/ArmTfs.VsCode/src/sidebar.ts](file:///Y:/ArmTFS/src/ArmTfs.VsCode/src/sidebar.ts) 第 1748-1795 行

**改动**：将 `sidebar.ts` 版本重写为与 `tfvcContext.ts`/`scm.ts` 一致，补充缺失的 `/Users/` → `C:\Mac\Home\` 转换分支，并移除对 `getPlatformSharedHomeDirectory()` 的依赖（改用硬编码 `C:\Mac\Home`，与其他实现一致）。

### 2.3 修复 C# `TranslatePlatformSharedPath`

**文件**：[src/ArmTfs.Core/Workspace/WorkspaceManager.cs](file:///Y:/ArmTFS/src/ArmTfs.Core/Workspace/WorkspaceManager.cs) 第 483-526 行

**改动**：补充 `/Users/` → `C:\Mac\Home\` 转换分支（仅在 Windows 平台触发）。

```csharp
// 新增分支
if (OperatingSystem.IsWindows() && normalized.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase))
    return Path.Combine(sharedHome, normalized["/Users/".Length..]);
```

### 2.4 修复 `GetLocalMappingPreference` 跨平台评分

**文件**：[src/ArmTfs.Core/Workspace/WorkspaceManager.cs](file:///Y:/ArmTFS/src/ArmTfs.Core/Workspace/WorkspaceManager.cs) 第 447-468 行

**改动**：在评分前先调用 `TranslatePlatformSharedPath(mapping.LocalPath)`，确保 macOS 端写入的 `/Users/foo/...` 映射在 Windows 端能正确翻译为 `C:\Mac\Home\foo\...` 再做平台原生路径检测。

### 2.5 修复 `FolderProfile.pubxml` 发布路径

**文件**：[src/ArmTfs.Cli/Properties/PublishProfiles/FolderProfile.pubxml](file:///Y:/ArmTFS/src/ArmTfs.Cli/Properties/PublishProfiles/FolderProfile.pubxml)

**改动**：将 `PublishDir` 从双重嵌套的 `bin\Release\net8.0\win-arm64\publish\win-arm64\` 改为扁平的 `bin\Release\net8.0\win-arm64\publish\`。

### 2.6 扩展 `tryResolveWorkspaceBuild` 候选路径

**文件**：[src/ArmTfs.VsCode/src/armTfsCliClient.ts](file:///Y:/ArmTFS/src/ArmTfs.VsCode/src/armTfsCliClient.ts) 第 670-724 行

**改动**：扩展 DLL 和可执行文件的候选路径列表，覆盖 `publish/` 子目录和 Debug/Release 两种配置，确保无论用哪种构建方式产物都能被发现。

---

## 3. 测试结果

### 3.1 C# 单元测试（xUnit）

**命令**：`dotnet test tests/ArmTfs.Core.Tests/ArmTfs.Core.Tests.csproj`

**结果**：

```
Passed! - Failed: 0, Passed: 87, Skipped: 0, Total: 87
```

新增测试覆盖：

- `CrossPlatformPathTests.cs` —— 跨平台路径分隔符、驱动器号检测、工作区元数据跨平台读写、版本追踪文件跨平台读写
- `WorkspaceManagerTests.cs` 新增用例 —— 混合分隔符处理、映射尾部斜杠、跨平台版本追踪回退

关键测试用例：

| 测试 | 验证场景 |
|------|----------|
| `IsWindowsDrivePath_DetectsBothSeparators` | `C:\` 和 `C:/` 均被识别为 Windows 驱动器路径 |
| `WorkspaceMetadata_RoundTripsAcrossPlatformSeparators` | Windows 端写入 `\` 路径，macOS 端读取仍能映射 |
| `TrackedVersion_SurvivesCrossPlatformReadWrite` | 跨平台版本追踪文件读写不丢失 |
| `LocalToServerPath_HandlesTrailingSlashInMapping` | 映射含/不含尾部斜杠均正确 |

### 3.2 TypeScript 单元测试（Node.js 内置测试运行器）

**命令**：`node --test src/ArmTfs.VsCode/src/pathLogic.test.mjs`（或 `npm test`）

**结果**：

```
# tests 16
# pass 16
# fail 0
```

测试覆盖：

- `translatePlatformSharedPath` —— macOS `/Users/` ↔ Windows `C:\Mac\Home\` 双向转换、同平台恒等、非共享路径（如 `D:\ArmTFS`）不被误转
- `splitCommandLine` —— Windows 路径反斜杠保留、POSIX 双引号内转义保留、`D:\ArmTFS\arm-tfs.exe` 不被破坏
- 集成测试 —— 模拟用户配置 `dotnet D:\path\arm-tfs.dll` 的完整解析流程

### 3.3 编译验证

| 项目 | 命令 | 结果 |
|------|------|------|
| ArmTfs.Core | `dotnet build src/ArmTfs.Core/ArmTfs.Core.csproj` | 0 警告，0 错误 |
| ArmTfs.Cli | `dotnet build src/ArmTfs.Cli/ArmTfs.Cli.csproj` | 0 警告，0 错误 |
| ArmTfs.Core.Tests | `dotnet test` | 87 通过，0 失败 |

---

## 4. 变更文件清单

| 文件 | 变更类型 | 说明 |
|------|----------|------|
| `src/ArmTfs.VsCode/src/armTfsCliClient.ts` | 修改 | 修复 `splitCommandLine` Windows 反斜杠处理；扩展 `tryResolveWorkspaceBuild` 候选路径 |
| `src/ArmTfs.VsCode/src/sidebar.ts` | 修改 | 对齐 `translatePlatformSharedPath` 与其他实现一致 |
| `src/ArmTfs.Core/Workspace/WorkspaceManager.cs` | 修改 | 补充 `/Users/` 跨平台转换；修复 `GetLocalMappingPreference` 跨平台评分 |
| `src/ArmTfs.Cli/Properties/PublishProfiles/FolderProfile.pubxml` | 修改 | 修复双重嵌套发布目录 |
| `src/ArmTfs.VsCode/package.json` | 修改 | 新增 `test` 脚本 |
| `tests/ArmTfs.Core.Tests/WorkspaceManagerTests.cs` | 修改 | 新增跨平台路径测试用例 |
| `tests/ArmTfs.Core.Tests/CrossPlatformPathTests.cs` | 新增 | 跨平台路径处理专项测试 |
| `src/ArmTfs.VsCode/src/pathLogic.test.mjs` | 新增 | TypeScript 路径逻辑测试 |

---

## 5. 跨平台路径处理约定

为确保未来开发不再引入类似问题，约定如下：

1. **TFVC 服务器路径**：始终使用 `/` 分隔符（如 `$/Project/Main/src/file.cs`），不依赖平台。
2. **本地路径**：使用 `Path.Combine`（C#）或 `path.join`（TS）构造，自动采用平台原生分隔符。
3. **路径比较**：先用 `NormalizeLocalPath`/`normalizeForCompare` 规范化（含 `TranslatePlatformSharedPath`），再做大小写不敏感比较。
4. **跨平台共享卷**：macOS `/Users/X` ↔ Windows `C:\Mac\Home\X`，由 `TranslatePlatformSharedPath` 统一处理。
5. **命令行解析**：Windows 上反斜杠是路径分隔符，不得作为转义符；POSIX 上仅在双引号内作为转义符。
6. **工作区元数据**：`workspace.json` 中的 `LocalPath` 字段可能由任一平台写入，读取时必须经过 `TranslatePlatformSharedPath` 翻译。
