# Arm TFS Release Guide

## 1. Prerequisites

- Install `.NET 8 SDK`
- Install `Node.js` and `npm`
- Run `npm install` once in `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode`
- Optional: install GitHub CLI `gh` if you want to create GitHub Releases from the command line

## 2. One-command local rebuild

From repo root:

```bash
cd /Volumes/MAC-DATA/ArmTFS
./build-release.sh 0.5.3
```

This does all of the following:

- rebuilds CLI outputs for `osx-arm64`
- rebuilds CLI outputs for `osx-x64`
- rebuilds CLI outputs for `win-arm64`
- rebuilds CLI outputs for `win-x64`
- compiles the VS Code extension
- packages the latest `.vsix`
- installs the latest VSIX into local VS Code with `--force`
- copies the 4 platform zip packages and both VSIX files into `src/ArmTfs.VsCode/release`
- clears macOS quarantine from the `macos-arm64` and `macos-x64` release folders

## 3. Manual CLI build commands

If you want to run them one by one:

```bash
cd /Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.Cli
dotnet build -c Release -r osx-arm64
dotnet build -c Release -r osx-x64
dotnet build -c Release -r win-arm64
dotnet build -c Release -r win-x64
```

Outputs are generated here:

- `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.Cli/bin/Release/net8.0/osx-arm64`
- `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.Cli/bin/Release/net8.0/osx-x64`
- `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.Cli/bin/Release/net8.0/win-arm64`
- `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.Cli/bin/Release/net8.0/win-x64`

On macOS, clear quarantine after rebuilding:

```bash
xattr -rd com.apple.quarantine /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.Cli/bin/Release/net8.0/osx-arm64
```

## 4. Manual VSIX build

```bash
cd /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode
./node_modules/.bin/tsc -p .
npx @vscode/vsce package -o arm-tfs-vscode-0.5.3.vsix
```

Output:

- `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode/arm-tfs-vscode-<version>.vsix`

## 5. Install VSIX locally

### From command line

```bash
code --install-extension /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode/arm-tfs-vscode-0.5.3.vsix --force
```

### From VS Code UI

1. Open VS Code
2. Open Extensions view
3. Click the `...` menu in the top-right corner
4. Click `Install from VSIX...`
5. Pick `/Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode/arm-tfs-vscode-0.5.3.vsix`

Release script note:

- Always pass an explicit version, for example `./build-release.sh 0.5.3`
- The script will sync both CLI and VS Code extension version numbers
- The script will also install the generated VSIX into local VS Code before you verify features
- Final release artifacts are written only into `src/ArmTfs.VsCode/release`

## 6. Prepare for Marketplace publishing

Before publishing, update `/Users/tamakirami/Desktop/arm-tfs/src/ArmTfs.VsCode/package.json`:

1. Change `publisher` from `local` to your real Marketplace publisher ID if you plan to publish to Marketplace
2. Bump `version`
3. Add a `repository` field if Marketplace packaging requires it
4. Add a `LICENSE` file if you want to remove packaging warnings

Recommended `repository` example:

This repository now uses GitHub tag releases for packaged binaries. Pushing a tag like `v0.5.3`
or `0.5.3` can trigger the GitHub Actions release build.

## 7. GitHub Release automation

The repository includes a GitHub Actions workflow that:

- reads the pushed tag version
- runs `./build-release.sh <tag-version>` in CI mode
- uploads every artifact from `src/ArmTfs.VsCode/release`
- creates or updates the matching GitHub Release

Example:

```bash
git tag 0.5.3
git push origin main
git push origin 0.5.3
```

## 8. Create Marketplace publisher

1. Open [Visual Studio Marketplace Publisher Management](https://marketplace.visualstudio.com/manage)
2. Sign in with the Microsoft account that will own the publisher
3. Create a new publisher
4. Record the publisher ID exactly

## 9. Create Marketplace PAT

1. Open [Azure DevOps PAT page](https://dev.azure.com/)
2. Sign in
3. Open `User settings` -> `Personal access tokens`
4. Create a new token
5. Give it Marketplace publish permission
6. Copy the token immediately

## 10. Log in to vsce

```bash
cd /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode
npx @vscode/vsce login <your-publisher-id>
```

When prompted, paste the Marketplace PAT.

## 11. Publish to Marketplace

### Option A: publish directly

```bash
cd /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode
npx @vscode/vsce publish
```

### Option B: publish a specific version

```bash
cd /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode
npx @vscode/vsce publish 0.1.1
```

### Option C: publish the already-built VSIX

```bash
cd /Volumes/MAC-DATA/ArmTFS/src/ArmTfs.VsCode
npx @vscode/vsce publish --packagePath arm-tfs-vscode-0.5.3.vsix
```

## 12. Verify after publishing

1. Search for the extension in Marketplace
2. Install it in a clean VS Code window
3. Confirm the first-run CLI picker appears
4. Confirm TFS connection management appears in the `Connections & CLI` view
5. Confirm history browser can open and diff files
6. Confirm `Add` file history opens as `empty ↔ current`

## 13. Recommended release checklist

1. Run `/Volumes/MAC-DATA/ArmTFS/build-release.sh 0.5.3`
2. Install the new VSIX locally
3. Open a fresh Extension Development Host
4. Verify server explorer, branch scope, history browser, diff, and checkout
5. Commit the versioned changes
6. Push branch and release tag
7. Confirm the GitHub Actions release workflow uploads the `0.5.3` artifacts
