# ARM TFS v0.5.1 Release Files

## 发布日期
2026-07-07

## 文件说明

### VSCode 扩展
- **arm-tfs-vscode-0.5.1.vsix** (~774 KB)
  - ARM TFS VSCode 扩展安装包
  - 安装命令: `code --install-extension arm-tfs-vscode-0.5.1.vsix`
  - 兼容 VSCode 1.90.0+

### CLI 运行时 - macOS arm64
- **arm-tfs-0.5.1-osx-arm64.zip** (~34 MB)
  - macOS ARM64 平台自包含单文件可执行程序
  - 解压后直接运行: `./arm-tfs --help`
  - 适用于: Apple Silicon Macs (M1/M2/M3)

### CLI 运行时 - macOS x64
- **arm-tfs-0.5.1-osx-x64.zip** (~36 MB)
  - macOS Intel x64 平台自包含单文件可执行程序
  - 适用于: Intel Macs

### CLI 运行时 - Windows arm64
- **arm-tfs-0.5.1-win-arm64.zip** (~35 MB)
  - Windows ARM64 平台自包含单文件可执行程序
  - 解压后运行: `arm-tfs.exe --help`
  - 适用于: Windows on ARM 设备

### CLI 运行时 - Windows x64
- **arm-tfs-0.5.1-win-x64.zip** (~36 MB)
  - Windows x64 平台自包含单文件可执行程序
  - 适用于: 标准 Windows PC

## 新功能 (v0.5.1)

### Checkin SOAP 流程修复
- 新增 `UpdateLocalVersionAsync`（服务端工作区 pend Edit 前必须设置本地版本基线）
- 修复 `UploadFileToWorkspaceAsync`：正确的 multipart form-data 格式（含 MD5 hash/range）
- 重构 `CheckInWithContentAsync`：分类型批量 pend + 本地路径写文件 + 二进制/文本编码自动检测
- 修复 owner 解析：优先读 workspace metadata，捕获 TF204017 用错误中 GUID 重试

### 历史记录时间显示修复
- 修复时间显示为零时区（UTC）的问题
- 所有 history/changeset/label/shelveset/branch 时间现在正确显示本地时间



## 文件说明

### VSCode 扩展
- **arm-tfs-vscode-0.1.7.vsix** (769 KB)
  - ARM TFS VSCode 扩展安装包
  - 安装命令: `code --install-extension arm-tfs-vscode-0.1.7.vsix`
  - 兼容 VSCode 1.90.0+

### CLI 运行时 - macOS arm64
- **arm-tfs-0.1.7-osx-arm64.zip** (75 MB)
  - macOS ARM64 平台的 CLI 运行时
  - 包含 arm-tfs.dll 和所有依赖项
  - 解压后可直接使用: `dotnet arm-tfs.dll --help`
  - 适用于: Apple Silicon Macs (M1/M2/M3)

### CLI 运行时 - Windows arm64
- **arm-tfs-0.1.7-win-arm64.zip** (112 MB)
  - Windows ARM64 平台的 CLI 运行时
  - 包含 arm-tfs.dll 和所有依赖项
  - 解压后可直接使用: `dotnet arm-tfs.dll --help`
  - 适用于: Windows on ARM 设备（如 Surface Pro X）

## 新功能 (v0.1.7)

### 🎯 合并冲突导航改进
- ✅ 顶部添加 "上一个冲突" / "下一个冲突" 按钮（带文字标签）
- ✅ 底部添加 "剩余 X 个冲突" 状态栏和导航按钮
- ✅ 自动滚动到目标冲突并高亮显示
- ✅ 循环导航支持（从最后回到第一个）

### 🔧 合并操作增强
- ✅ "完成合并" 按钮 - 所有冲突解决后执行合并
- ✅ "撤销合并" 按钮 - 清除所有冲突解决方案
- ✅ 智能按钮状态（未解决冲突时自动禁用）

### 🎨 UI 改进
- 更接近原生 Visual Studio TFS 合并体验
- 清晰的冲突状态反馈
- 改进的导航体验

## 安装说明

### VSCode 扩展安装
```bash
# 方法1: 使用命令行
code --install-extension arm-tfs-vscode-0.1.7.vsix --force

# 方法2: 在 VSCode 中
# 1. 打开 VSCode
# 2. Ctrl+Shift+P (Cmd+Shift+P on Mac)
# 3. 输入 "Extensions: Install from VSIX..."
# 4. 选择 arm-tfs-vscode-0.1.7.vsix 文件
```

### CLI 运行时安装

#### macOS
```bash
# 1. 解压
unzip arm-tfs-0.1.7-osx-arm64.zip

# 2. 移除 macOS 隔离属性
xattr -rd com.apple.quarantine macos-arm64/

# 3. 测试运行
cd macos-arm64
dotnet arm-tfs.dll --version
```

#### Windows
```powershell
# 1. 解压
Expand-Archive arm-tfs-0.1.7-win-arm64.zip

# 2. 测试运行
cd windows-arm64
dotnet arm-tfs.dll --version
```

## 系统要求

### 必需
- **.NET 8.0 Runtime** 或更高版本
- **VSCode 1.90.0** 或更高版本

### 推荐
- macOS 11+ (Big Sur) for Apple Silicon
- Windows 10/11 ARM64
- 至少 4GB 可用内存
- 稳定的网络连接（用于 TFS 服务器通信）

## 配置说明

### VSCode 扩展配置
扩展配置文件位置：
- **macOS**: `~/Library/Application Support/arm-tfs/vscode-config.json`
- **Windows**: `%APPDATA%\arm-tfs\vscode-config.json`
- **Linux**: `${XDG_CONFIG_HOME:-~/.config}/arm-tfs/vscode-config.json`

### TFS 连接配置
首次使用需要配置：
1. TFS 服务器 URL
2. 个人访问令牌 (PAT)
3. 工作区映射

## 测试建议

### 测试合并功能
```bash
# 使用提供的测试分支
源分支: /path/to/MindrayApp.TeamsPortal-P_V20260515
目标分支: /path/to/MindrayApp.TeamsPortal

# 在 VSCode 中
# 1. 打开目标分支工作区
# 2. ARM TFS 侧边栏 > 分支视图
# 3. 右键点击源分支 > "Merge to Current Branch"
# 4. 测试冲突导航和解决功能
```

## 已知问题
- 无

## 反馈和支持
如遇问题，请检查：
1. `.NET 8.0 Runtime` 是否正确安装
2. VSCode 版本是否 >= 1.90.0
3. TFS 服务器连接是否正常
4. 查看 `Output` > `ARM TFS` 日志

## 更新历史
- v0.1.7 (2026-07-02) - 合并 UI 改进
- v0.1.6 (2026-07-02) - 原生合并编辑器集成
- v0.1.5 (2026-07-02) - 范围合并优化
- v0.1.4 (2026-06-30) - 用户配置文件迁移

## 许可证
查看项目根目录的 LICENSE 文件
