#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
CLI_DIR="$ROOT_DIR/src/ArmTfs.Cli"
EXT_DIR="$ROOT_DIR/src/ArmTfs.VsCode"

RIDS=(
  "osx-arm64"
  "win-arm64"
  "win-x64"
  "linux-arm64"
  "linux-x64"
)

echo "== Building ArmTfs CLI release outputs =="
for rid in "${RIDS[@]}"; do
  echo "-- $rid"
  dotnet build -c Release -r "$rid" "$CLI_DIR/ArmTfs.Cli.csproj"
done

echo
echo "== Building VS Code extension =="
(
  cd "$EXT_DIR"
  ./node_modules/.bin/tsc -p .
  VSIX_NAME="arm-tfs-vscode-$(node -p "require('./package.json').version").vsix"
  echo y | ./node_modules/.bin/vsce package --allow-missing-repository --skip-license -o "$VSIX_NAME"
)

echo
echo "== Clearing macOS quarantine on osx-arm64 output =="
xattr -rd com.apple.quarantine "$CLI_DIR/bin/Release/net8.0/osx-arm64" 2>/dev/null || true

echo
echo "Build complete."
echo "CLI outputs:"
for rid in "${RIDS[@]}"; do
  echo "  $CLI_DIR/bin/Release/net8.0/$rid"
done
echo "VSIX output:"
echo "  $EXT_DIR/arm-tfs-vscode-$(node -p "require('$EXT_DIR/package.json').version").vsix"
