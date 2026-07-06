# ArmTfs · arm-tfs

> **EN** | Cross-platform TFVC CLI & VS Code Extension · macOS ARM64/x64 · Windows ARM64/x64
> **中文** | 跨平台 TFVC 命令行工具与 VS Code 扩展 · 支持 macOS / Windows ARM64 & x64

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-0.1.8-green.svg)](src/ArmTfs.VsCode/package.json)
[![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-lightgrey.svg)]()

---

## English

### What is ArmTfs?

ArmTfs is a cross-platform [TFVC (Team Foundation Version Control)](https://learn.microsoft.com/en-us/azure/devops/repos/tfvc/what-is-tfvc) toolchain consisting of:

- **`arm-tfs` CLI** — A .NET 8 command-line tool that communicates with TFS exclusively via the legacy **TFVC SOAP `Repository.asmx`** protocol, enabling full TFVC operations without Visual Studio.
- **VS Code Extension** — A VS Code UI built on top of the CLI, providing SCM integration, branch management, merge workbench, server explorer, and changeset history.

Designed for teams using on-premises TFS / Azure DevOps Server on modern ARM hardware (Apple Silicon Macs, Windows ARM laptops), where the official `tf.exe` is unavailable or unsupported.

### Why was this built?

#### The `tf.exe` problem

Microsoft's `tf.exe` is x86/x64 only — it does not run on macOS (any arch) or Windows 11 ARM natively.

#### Why TEE-CLC also fails on ARM64

[JetBrains TEE-CLC](https://github.com/microsoft/team-explorer-everywhere) is a Java-based TF client, but:

- Its `tf` launch script has no `aarch64` mapping → JNI native library lookup fails on ARM64 Linux / macOS Apple Silicon.
- The bundled `com.microsoft.tfs.jni.jar` has no ARM64 native `.so`/`.dylib` — the project is **archived and no longer maintained**.

#### The `arm-tfs` approach

`arm-tfs` uses the legacy **TFVC SOAP `Repository.asmx`** protocol exclusively for all TFVC operations.
No REST, no JNI, no native TF client, no COM, no P/Invoke — runs anywhere .NET 8 runs.

### Supported Platforms

| Platform | Architecture | CLI Binary | VS Code Extension |
|----------|-------------|-----------|------------------|
| macOS | ARM64 (Apple Silicon) | ✅ | ✅ |
| macOS | x64 (Intel) | ✅ | ✅ |
| Windows 11 | ARM64 | ✅ | ✅ |
| Windows 11 | x64 | ✅ | ✅ |

### Features

| Feature | Description |
|---------|-------------|
| 🌿 Branch Management | Create, delete, list, inspect TFVC branches (source can be any folder path) |
| 🔀 Merge Workbench | Visual merge candidate selection, conflict detection & execution |
| 📜 Changeset History | Browse history with author, date, and comment |
| 🔍 Server Explorer | Browse the TFVC server tree and manage items |
| ✅ SCM Integration | VS Code source control panel with pending changes |
| 📦 Check-in / Checkout | Stage and commit changes with comments |
| 🏷️ Label Support | Create and manage TFVC labels |
| ↩️ Rollback / Revert | Roll back changesets or revert to a specific version |
| 🌐 i18n | UI supports English and Simplified Chinese |

### Installation

#### VS Code Extension

1. Download `arm-tfs-vscode-x.x.x.vsix` from [Releases](https://github.com/tamakiramimy/ArmTfs/releases).
2. In VS Code: `Extensions` → `⋯` → `Install from VSIX…` → select the file.
3. Reload VS Code.

#### CLI Only

Download the zip for your platform from [Releases](https://github.com/tamakiramimy/ArmTfs/releases):

| File | Platform |
|------|----------|
| `arm-tfs-x.x.x-osx-arm64.zip` | macOS Apple Silicon |
| `arm-tfs-x.x.x-osx-x64.zip` | macOS Intel |
| `arm-tfs-x.x.x-win-arm64.zip` | Windows ARM64 |
| `arm-tfs-x.x.x-win-x64.zip` | Windows x64 |

Extract and add to your `PATH`, or point VS Code to it via `arm-tfs: Configure CLI Command`.

### Configuration

After installing the extension, run:
1. **`arm-tfs: Configure CLI Command`** — point to the CLI binary or `.dll`.
2. **`arm-tfs: Configure TFS Connection`** — enter your server URL and PAT.

Credentials are stored securely in VS Code's secret storage (OS keychain) — never written to plain-text config files.

| VS Code Setting | Description |
|----------------|-------------|
| `armTfs.cli.command` | Path to `arm-tfs` executable or `dotnet` |
| `armTfs.cli.commandArgs` | Extra arguments (e.g. `["/path/to/arm-tfs.dll"]`) |
| `armTfs.ui.language` | UI language: `auto` / `zh-CN` / `en` |

### Quick Start (CLI)

```bash
# 1. Configure server and credentials
arm-tfs configure --url https://tfs.example.com/DefaultCollection --pat YOUR_TOKEN

# 2. Create a workspace and map a server folder
arm-tfs workspace new --name MyWorkspace
arm-tfs workspace map --server "$/MyProject/Main" --local ~/code/myproject

# 3. Get latest files
cd ~/code/myproject
arm-tfs get

# 4. Edit and check in
arm-tfs checkout MyFile.cs
# ... make changes ...
arm-tfs checkin -c "Fix bug in MyFile"
```

### Commands Reference

```
arm-tfs configure --url <url> --pat <token> [--display-name <name>] [--show]

arm-tfs workspace new --name <name>
arm-tfs workspace show
arm-tfs workspace map --server <$/...> --local <path>

arm-tfs get [path] [--version <n>] [--force] [--recursive] [--dry-run]
arm-tfs status [path] [--all] [--format table|json]
arm-tfs checkout <paths...> [--recursive]
arm-tfs add      <paths...> [--recursive]
arm-tfs undo     <paths...> [--no-restore]
arm-tfs checkin  [paths...] -c "comment" [--dry-run]
arm-tfs history  [path] [--top <n>] [--author <name>] [--format table|json]
arm-tfs diff     <path> [--base] [--version <n>] [--format text|json]

arm-tfs branch list   [scope]
arm-tfs branch show   <path>
arm-tfs branch create --source <$/...> --target <$/...> [--version <n>] [--comment <text>]
arm-tfs branch delete --path  <$/...>  [--comment <text>]

arm-tfs merge base             --source <$/...> --target <$/...>
arm-tfs merge candidate        --source <$/...> --target <$/...> [--top <n>]
arm-tfs merge execute          --source <$/...> --target <$/...> --changeset <id>
arm-tfs merge execute          --source <$/...> --target <$/...> --from <id> --to <id>
arm-tfs merge preview-conflicts --source <$/...> --target <$/...> --from <id> --to <id>

arm-tfs label list / show / create / delete
arm-tfs items list <$/...> [--recursive]
arm-tfs rollback <changesetId>
arm-tfs revert-to-version <$/...> <id>
arm-tfs delete / rename / undelete / lock
```

### Building from Source

**Prerequisites**: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8), [Node.js 18+](https://nodejs.org/)

```bash
git clone https://github.com/tamakiramimy/ArmTfs.git
cd ArmTfs

# Build CLI (macOS Apple Silicon)
dotnet publish src/ArmTfs.Cli -r osx-arm64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64

# Build CLI (Windows x64)
dotnet publish src/ArmTfs.Cli -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/win-x64

# Build VS Code extension
cd src/ArmTfs.VsCode && npm install && npm run compile
npx @vscode/vsce package --no-dependencies
```

### Architecture

```
ArmTfs/
├── src/
│   ├── ArmTfs.Core/       # SOAP client, workspace model, merge engine
│   │   ├── Client/        # TfvcClientService, TfsConnection, TfvcSoapClient
│   │   ├── Config/        # TfsConfig, credential storage
│   │   ├── Models/        # TFVC domain models & SOAP DTOs
│   │   └── Workspace/     # Local workspace (.tf/) management
│   ├── ArmTfs.Cli/        # CLI entry point (System.CommandLine)
│   └── ArmTfs.VsCode/     # VS Code extension (TypeScript)
└── tests/
```

### License

MIT License — Copyright (c) 2024-2026 [tamakiramimy](mailto:tamakiramimy@163.com)

Free to use, fork, and modify. **Attribution required**: retain the copyright notice in all copies. See [LICENSE](LICENSE).

### Contact

- **Email**: tamakiramimy@163.com
- **Issues**: [GitHub Issues](https://github.com/tamakiramimy/ArmTfs/issues)

---

## 中文

### 项目简介

ArmTfs 是一套跨平台的 [TFVC（Team Foundation 版本控制）](https://learn.microsoft.com/zh-cn/azure/devops/repos/tfvc/what-is-tfvc) 工具链，包含两个组件：

- **`arm-tfs` 命令行工具** — 基于 .NET 8，通过 **TFVC SOAP `Repository.asmx`** 协议直接与 TFS 通信，实现完整的 TFVC 操作，无需安装 Visual Studio。
- **VS Code 扩展** — 提供图形界面，包括 SCM 集成、分支管理、合并工作台、服务器资源管理器、变更集历史等。

专为在 ARM 设备（Apple Silicon Mac、Windows ARM 笔记本）上使用本地部署 TFS / Azure DevOps Server 的团队设计。

### 为什么要做这个工具？

微软官方的 `tf.exe` 仅有 x86/x64 版本，无法在 macOS 或 Windows 11 ARM 上原生运行。JetBrains TEE-CLC 虽是跨平台方案，但其 JNI 本地库不支持 ARM64，项目已归档不再维护。

`arm-tfs` 全量使用 TFVC SOAP `Repository.asmx` 协议，无 REST 依赖、无 JNI、无原生客户端，可在所有支持 .NET 8 的平台上运行。

### 支持平台

| 平台 | 架构 | CLI 工具 | VS Code 扩展 |
|------|------|---------|-------------|
| macOS | ARM64（Apple Silicon） | ✅ | ✅ |
| macOS | x64（Intel） | ✅ | ✅ |
| Windows 11 | ARM64 | ✅ | ✅ |
| Windows 11 | x64 | ✅ | ✅ |

### 功能特性

| 功能 | 说明 |
|------|------|
| 🌿 分支管理 | 创建、删除、查看 TFVC 分支（source 支持普通文件夹路径） |
| 🔀 合并工作台 | 可视化合并候选选择、冲突检测与执行 |
| 📜 变更集历史 | 按作者、日期、备注浏览历史 |
| 🔍 服务器资源管理器 | 浏览 TFVC 服务器目录树，管理文件 |
| ✅ SCM 集成 | VS Code 源代码管理面板，显示待处理变更 |
| 📦 签入/签出 | 暂存并提交带备注的变更 |
| 🏷️ 标签支持 | 创建和管理 TFVC 标签 |
| ↩️ 回滚/还原 | 回滚变更集或还原到指定版本 |
| 🌐 国际化 | 界面支持中文和英文 |

### 安装方法

#### VS Code 扩展

1. 从 [Releases](https://github.com/tamakiramimy/ArmTfs/releases) 下载 `arm-tfs-vscode-x.x.x.vsix`。
2. 在 VS Code 中：`扩展` → `⋯` → `从 VSIX 安装…` → 选择文件。
3. 重新加载 VS Code。

#### 仅使用命令行工具

从 [Releases](https://github.com/tamakiramimy/ArmTfs/releases) 下载对应平台的 zip 包并解压，加入 `PATH` 即可直接使用。

### 快速上手

```bash
# 1. 配置服务器和认证
arm-tfs configure --url http://your-tfs-server/DefaultCollection --pat YOUR_TOKEN

# 2. 创建工作区并映射服务器目录
arm-tfs workspace new --name MyWorkspace
arm-tfs workspace map --server "$/MyProject/Main" --local ~/code/myproject

# 3. 获取最新文件
cd ~/code/myproject
arm-tfs get

# 4. 签出、修改、签入
arm-tfs checkout MyFile.cs
arm-tfs checkin -c "修复 MyFile 中的 Bug"
```

### 开源协议

MIT License — Copyright (c) 2024-2026 [tamakiramimy](mailto:tamakiramimy@163.com)

本项目可自由使用、Fork 和修改。**使用时须保留原始版权声明**。详见 [LICENSE](LICENSE) 文件。

### 联系方式

- **邮箱**：tamakiramimy@163.com
- **问题反馈**：[GitHub Issues](https://github.com/tamakiramimy/ArmTfs/issues)
