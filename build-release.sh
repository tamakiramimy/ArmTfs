#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
CLI_DIR="$ROOT_DIR/src/ArmTfs.Cli"
EXT_DIR="$ROOT_DIR/src/ArmTfs.VsCode"
RELEASE_DIR="$EXT_DIR/release"
VERSION="$(awk -F'[><]' '/<Version>/{print $3; exit}' "$CLI_DIR/ArmTfs.Cli.csproj")"

RIDS=("osx-arm64" "osx-x64" "win-arm64" "win-x64")

release_dir_for_rid() {
  case "$1" in
    osx-arm64) echo "macos-arm64" ;;
    osx-x64) echo "macos-x64" ;;
    win-arm64) echo "windows-arm64" ;;
    win-x64) echo "windows-x64" ;;
    *) echo "$1" ;;
  esac
}

artifact_name_for_rid() {
  case "$1" in
    osx-arm64) echo "arm-tfs-$VERSION-osx-arm64.zip" ;;
    osx-x64) echo "arm-tfs-$VERSION-osx-x64.zip" ;;
    win-arm64) echo "arm-tfs-$VERSION-win-arm64.zip" ;;
    win-x64) echo "arm-tfs-$VERSION-win-x64.zip" ;;
    *) echo "arm-tfs-$VERSION-$1.zip" ;;
  esac
}

mkdir -p "$RELEASE_DIR"
rm -f "$RELEASE_DIR"/arm-tfs-*-osx-arm64.zip \
      "$RELEASE_DIR"/arm-tfs-*-osx-x64.zip \
      "$RELEASE_DIR"/arm-tfs-*-win-arm64.zip \
      "$RELEASE_DIR"/arm-tfs-*-win-x64.zip \
      "$RELEASE_DIR"/arm-tfs-vscode-*.vsix \
      "$RELEASE_DIR"/arm-tfs-vscode.vsix

echo "== Building ArmTfs CLI release outputs =="
for rid in "${RIDS[@]}"; do
  echo "-- $rid"
  out_dir="$RELEASE_DIR/$(release_dir_for_rid "$rid")"
  rm -rf "$out_dir"
  dotnet publish -c Release -r "$rid" --self-contained true "$CLI_DIR/ArmTfs.Cli.csproj" -o "$out_dir"
  if [[ "$rid" == osx-* ]]; then
    chmod +x "$out_dir/arm-tfs" 2>/dev/null || true
  fi
  rm -f "$RELEASE_DIR/$(artifact_name_for_rid "$rid")"
  (
    cd "$RELEASE_DIR"
    ditto -c -k --sequesterRsrc --keepParent "$(release_dir_for_rid "$rid")" "$(artifact_name_for_rid "$rid")"
  )
done

echo
echo "== Building VS Code extension =="
(
  cd "$EXT_DIR"
  ./node_modules/.bin/tsc -p .
  VSIX_NAME="arm-tfs-vscode-$(node -p "require('./package.json').version").vsix"
  rm -f arm-tfs-vscode-*.vsix arm-tfs-vscode.vsix
  ./node_modules/.bin/vsce package --allow-missing-repository --skip-license -o "$VSIX_NAME"
  cp "$VSIX_NAME" arm-tfs-vscode.vsix
  cp "$VSIX_NAME" "$RELEASE_DIR/$VSIX_NAME"
  cp "$VSIX_NAME" "$RELEASE_DIR/arm-tfs-vscode.vsix"
)

echo
echo "== Clearing macOS quarantine on macOS outputs =="
xattr -rd com.apple.quarantine "$RELEASE_DIR/macos-arm64" 2>/dev/null || true
xattr -rd com.apple.quarantine "$RELEASE_DIR/macos-x64" 2>/dev/null || true

echo
echo "Build complete."
echo "CLI outputs:"
for rid in "${RIDS[@]}"; do
  echo "  $RELEASE_DIR/$(release_dir_for_rid "$rid")"
  echo "  $RELEASE_DIR/$(artifact_name_for_rid "$rid")"
done
echo "VSIX output:"
echo "  $EXT_DIR/arm-tfs-vscode-$(node -p "require('$EXT_DIR/package.json').version").vsix"
echo "  $RELEASE_DIR/arm-tfs-vscode-$(node -p "require('$EXT_DIR/package.json').version").vsix"
echo "  $RELEASE_DIR/arm-tfs-vscode.vsix"
