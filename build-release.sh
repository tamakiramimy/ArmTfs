#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
CLI_DIR="$ROOT_DIR/src/ArmTfs.Cli"
EXT_DIR="$ROOT_DIR/src/ArmTfs.VsCode"
RELEASE_DIR="$EXT_DIR/release"
CLI_PROJECT="$CLI_DIR/ArmTfs.Cli.csproj"
EXT_PACKAGE_JSON="$EXT_DIR/package.json"
IS_CI="${CI:-}"
INSTALL_VSIX_MODE="${INSTALL_VSIX:-auto}"

RIDS=("osx-arm64" "osx-x64" "win-arm64" "win-x64")

usage() {
  cat <<'EOF'
Usage:
  ./build-release.sh <version>

Example:
  ./build-release.sh 0.5.3

Behavior:
  - syncs CLI csproj version fields to the supplied version
  - syncs VS Code extension package.json version to the supplied version
  - rebuilds CLI release outputs for all supported platforms
  - rebuilds the VS Code extension VSIX
  - installs the VSIX locally when running on a desktop with `code` available
  - writes every final artifact into:
      src/ArmTfs.VsCode/release
EOF
}

fail() {
  echo "Error: $*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Required command not found: $1"
}

has_cmd() {
  command -v "$1" >/dev/null 2>&1
}

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

validate_version() {
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || fail "Invalid version: $1"
}

sync_cli_version() {
  VERSION="$1" CLI_PROJECT="$2" python3 - <<'PY'
import os
import pathlib
import re

version = os.environ["VERSION"]
project_path = pathlib.Path(os.environ["CLI_PROJECT"])
text = project_path.read_text(encoding="utf-8-sig")

for tag in ("AssemblyVersion", "FileVersion", "Version"):
    pattern = rf"<{tag}>[^<]+</{tag}>"
    replacement = f"<{tag}>{version}</{tag}>"
    text, count = re.subn(pattern, replacement, text, count=1)
    if count != 1:
        raise SystemExit(f"Could not update <{tag}> in {project_path}")

project_path.write_text(text, encoding="utf-8")
PY
}

sync_extension_version() {
  VERSION="$1" EXT_PACKAGE_JSON="$2" python3 - <<'PY'
import json
import os
import pathlib

version = os.environ["VERSION"]
package_path = pathlib.Path(os.environ["EXT_PACKAGE_JSON"])
data = json.loads(package_path.read_text(encoding="utf-8"))
data["version"] = version
package_path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY
}

clean_release_outputs() {
  mkdir -p "$RELEASE_DIR"
  rm -rf \
    "$RELEASE_DIR/macos-arm64" \
    "$RELEASE_DIR/macos-x64" \
    "$RELEASE_DIR/windows-arm64" \
    "$RELEASE_DIR/windows-x64"
  rm -f \
    "$RELEASE_DIR"/arm-tfs-*-osx-arm64.zip \
    "$RELEASE_DIR"/arm-tfs-*-osx-x64.zip \
    "$RELEASE_DIR"/arm-tfs-*-win-arm64.zip \
    "$RELEASE_DIR"/arm-tfs-*-win-x64.zip \
    "$RELEASE_DIR"/arm-tfs-vscode-*.vsix \
    "$RELEASE_DIR"/arm-tfs-vscode.vsix
}

archive_directory() {
  local source_dir="$1"
  local output_name="$2"
  (
    cd "$RELEASE_DIR"
    if has_cmd ditto; then
      ditto -c -k --sequesterRsrc --keepParent "$source_dir" "$output_name"
    else
      zip -rq "$output_name" "$source_dir"
    fi
  )
}

build_cli_release() {
  echo "== Building ArmTfs CLI release outputs =="
  for rid in "${RIDS[@]}"; do
    echo "-- $rid"
    local out_dir="$RELEASE_DIR/$(release_dir_for_rid "$rid")"
    dotnet publish -c Release -r "$rid" --self-contained true "$CLI_PROJECT" -o "$out_dir"
    if [[ "$rid" == osx-* ]]; then
      chmod +x "$out_dir/arm-tfs" 2>/dev/null || true
    fi
    archive_directory "$(release_dir_for_rid "$rid")" "$(artifact_name_for_rid "$rid")"
  done
}

build_extension_release() {
  echo
  echo "== Building VS Code extension =="
  (
    cd "$EXT_DIR"
    ./node_modules/.bin/tsc -p .
    local_vsix="arm-tfs-vscode-$VERSION.vsix"
    rm -f arm-tfs-vscode-*.vsix arm-tfs-vscode.vsix
    ./node_modules/.bin/vsce package --allow-missing-repository --skip-license -o "$local_vsix"
    cp "$local_vsix" arm-tfs-vscode.vsix
    cp "$local_vsix" "$RELEASE_DIR/$local_vsix"
    cp "$local_vsix" "$RELEASE_DIR/arm-tfs-vscode.vsix"
  )
}

install_vsix_locally() {
  if [[ "$INSTALL_VSIX_MODE" == "never" ]]; then
    echo
    echo "== Skipping VSIX install (INSTALL_VSIX=never) =="
    return
  fi

  if [[ -n "$IS_CI" ]]; then
    echo
    echo "== Skipping VSIX install in CI =="
    return
  fi

  if ! has_cmd code; then
    if [[ "$INSTALL_VSIX_MODE" == "always" ]]; then
      fail "INSTALL_VSIX=always but 'code' command was not found."
    fi
    echo
    echo "== Skipping VSIX install ('code' command not found) =="
    return
  fi

  echo
  echo "== Installing VSIX locally =="
  code --install-extension "$RELEASE_DIR/arm-tfs-vscode-$VERSION.vsix" --force
}

clear_macos_quarantine() {
  echo
  echo "== Clearing macOS quarantine on macOS outputs =="
  xattr -rd com.apple.quarantine "$RELEASE_DIR/macos-arm64" 2>/dev/null || true
  xattr -rd com.apple.quarantine "$RELEASE_DIR/macos-x64" 2>/dev/null || true
}

print_summary() {
  echo
  echo "Build complete for version $VERSION."
  echo "CLI outputs:"
  for rid in "${RIDS[@]}"; do
    echo "  $RELEASE_DIR/$(release_dir_for_rid "$rid")"
    echo "  $RELEASE_DIR/$(artifact_name_for_rid "$rid")"
  done
  echo "VSIX outputs:"
  echo "  $RELEASE_DIR/arm-tfs-vscode-$VERSION.vsix"
  echo "  $RELEASE_DIR/arm-tfs-vscode.vsix"
}

if [[ $# -ne 1 ]]; then
  usage
  exit 1
fi

VERSION="$1"
validate_version "$VERSION"

require_cmd dotnet
require_cmd node
require_cmd python3
if ! has_cmd ditto && ! has_cmd zip; then
  fail "Require either 'ditto' or 'zip' for release packaging."
fi

[[ -f "$CLI_PROJECT" ]] || fail "Missing CLI project: $CLI_PROJECT"
[[ -f "$EXT_PACKAGE_JSON" ]] || fail "Missing extension package.json: $EXT_PACKAGE_JSON"
[[ -x "$EXT_DIR/node_modules/.bin/tsc" ]] || fail "Missing tsc: run npm install in $EXT_DIR"
[[ -x "$EXT_DIR/node_modules/.bin/vsce" ]] || fail "Missing vsce: run npm install in $EXT_DIR"

echo "== Syncing version to $VERSION =="
sync_cli_version "$VERSION" "$CLI_PROJECT"
sync_extension_version "$VERSION" "$EXT_PACKAGE_JSON"

clean_release_outputs
build_cli_release
build_extension_release
install_vsix_locally
clear_macos_quarantine
print_summary
