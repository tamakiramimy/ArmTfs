# Arm TFS Release Guide

## 1. Prerequisites

- Install `.NET 8 SDK`
- Install `Node.js` and `npm`
- Run `npm install` once in `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode`
- For Marketplace publishing, prepare a Visual Studio Marketplace publisher and a Personal Access Token

## 2. One-command local rebuild

From repo root:

```bash
cd /Users/tamakirami/Desktop/arm-tfs
./build-release.sh
```

This does all of the following:

- rebuilds CLI outputs for `osx-arm64`
- rebuilds CLI outputs for `win-arm64`
- rebuilds CLI outputs for `win-x64`
- rebuilds CLI outputs for `linux-arm64`
- rebuilds CLI outputs for `linux-x64`
- compiles the VS Code extension
- packages the latest `.vsix`
- clears macOS quarantine from the `osx-arm64` release folder

## 3. Manual CLI build commands

If you want to run them one by one:

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli
dotnet build -c Release -r osx-arm64
dotnet build -c Release -r win-arm64
dotnet build -c Release -r win-x64
dotnet build -c Release -r linux-arm64
dotnet build -c Release -r linux-x64
```

Outputs are generated here:

- `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli/bin/Release/net8.0/osx-arm64`
- `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli/bin/Release/net8.0/win-arm64`
- `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli/bin/Release/net8.0/win-x64`
- `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli/bin/Release/net8.0/linux-arm64`
- `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli/bin/Release/net8.0/linux-x64`

On macOS, clear quarantine after rebuilding:

```bash
xattr -rd com.apple.quarantine /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli/bin/Release/net8.0/osx-arm64
```

## 4. Manual VSIX build

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode
./node_modules/.bin/tsc -p .
npx @vscode/vsce package -o arm-tfs-vscode-0.1.0.vsix
```

Output:

- `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode/arm-tfs-vscode-0.1.0.vsix`

## 5. Install VSIX locally

### From command line

```bash
code --install-extension /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode/arm-tfs-vscode-0.1.0.vsix --force
```

### From VS Code UI

1. Open VS Code
2. Open Extensions view
3. Click the `...` menu in the top-right corner
4. Click `Install from VSIX...`
5. Pick `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode/arm-tfs-vscode-0.1.0.vsix`

## 6. Prepare for Marketplace publishing

Before publishing, update `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode/package.json`:

1. Change `publisher` from `local` to your real Marketplace publisher ID
2. Bump `version`
3. Add a `repository` field
4. Add a `LICENSE` file if you want to remove packaging warnings

Recommended `repository` example:

```json
"repository": {
  "type": "git",
  "url": "https://github.com/your-org/arm-tfs.git"
}
```

## 7. Create Marketplace publisher

1. Open [Visual Studio Marketplace Publisher Management](https://marketplace.visualstudio.com/manage)
2. Sign in with the Microsoft account that will own the publisher
3. Create a new publisher
4. Record the publisher ID exactly

## 8. Create Marketplace PAT

1. Open [Azure DevOps PAT page](https://dev.azure.com/)
2. Sign in
3. Open `User settings` -> `Personal access tokens`
4. Create a new token
5. Give it Marketplace publish permission
6. Copy the token immediately

## 9. Log in to vsce

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode
npx @vscode/vsce login <your-publisher-id>
```

When prompted, paste the Marketplace PAT.

## 10. Publish to Marketplace

### Option A: publish directly

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode
npx @vscode/vsce publish
```

### Option B: publish a specific version

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode
npx @vscode/vsce publish 0.1.1
```

### Option C: publish the already-built VSIX

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode
npx @vscode/vsce publish --packagePath arm-tfs-vscode-0.1.0.vsix
```

## 11. Verify after publishing

1. Search for the extension in Marketplace
2. Install it in a clean VS Code window
3. Confirm the first-run CLI picker appears
4. Confirm TFS connection management appears in the `Connections & CLI` view
5. Confirm history browser can open and diff files
6. Confirm `Add` file history opens as `empty ↔ current`

## 12. Recommended release checklist

1. Run `/Users/tamakirami/Desktop/arm-tfs/build-release.sh`
2. Install the new VSIX locally
3. Open a fresh Extension Development Host
4. Verify server explorer, branch scope, history browser, diff, and checkout
5. Bump version
6. Publish
