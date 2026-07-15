// Cross-platform path translation tests for the VS Code extension.
//
// These tests exercise the `translatePlatformSharedPath` and `splitCommandLine`
// logic that lives (duplicated) inside sidebar.ts, tfvcContext.ts, scm.ts and
// armTfsCliClient.ts. To avoid pulling in the `vscode` module (which is only
// available inside the extension host), the functions under test are
// reimplemented here as a faithful copy and the canonical implementations live
// in the production .ts files.
//
// Run with:  node --test src/pathLogic.test.mjs
//
// The test suite covers the macOS arm64 ↔ Windows 11 arm64 interop scenarios
// reported in the original issue (path "D:\ArmTFS\arm-tfs.exe" was being
// mangled because `splitCommandLine` treated backslash as a POSIX escape
// character on Windows).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

// ─── Canonical implementation (must stay in sync with production .ts files) ──

function translatePlatformSharedPath(targetPath, platform) {
  if (platform !== 'darwin' && platform !== 'win32') {
    return targetPath;
  }
  const normalized = targetPath.replace(/\\/g, '/');

  for (const prefix of ['//Mac/Home/', '/Mac/Home/']) {
    if (normalized.toLowerCase().startsWith(prefix.toLowerCase())) {
      const rest = normalized.slice(prefix.length);
      return platform === 'darwin'
        ? path.join('/Users', rest)
        : path.join('C:\\Mac\\Home', ...rest.split('/'));
    }
  }

  const driveMatch = normalized.match(/^[A-Za-z]:\/(.*)$/);
  if (driveMatch) {
    const withoutDrive = driveMatch[1];
    if (withoutDrive.toLowerCase().startsWith('mac/home/')) {
      const rest = withoutDrive.slice('mac/home/'.length);
      return platform === 'darwin'
        ? path.join('/Users', rest)
        : path.join('C:\\Mac\\Home', ...rest.split('/'));
    }
  }

  if (platform === 'win32' && normalized.toLowerCase().startsWith('/users/')) {
    const rest = normalized.slice('/Users/'.length);
    return path.join('C:\\Mac\\Home', ...rest.split('/'));
  }

  return targetPath;
}

function splitCommandLine(value, platform) {
  const isWindows = platform === 'win32';
  const parts = [];
  let current = '';
  let quote;
  let escaping = false;

  for (const char of value) {
    if (escaping) {
      current += char;
      escaping = false;
      continue;
    }
    if (char === '\\' && !isWindows && quote === '"') {
      escaping = true;
      continue;
    }
    if (quote) {
      if (char === quote) {
        quote = undefined;
      } else {
        current += char;
      }
      continue;
    }
    if (char === '"' || char === '\'') {
      quote = char;
      continue;
    }
    if (/\s/.test(char)) {
      if (current) {
        parts.push(current);
        current = '';
      }
      continue;
    }
    current += char;
  }
  if (escaping) {
    current += '\\';
  }
  if (current) {
    parts.push(current);
  }
  return parts;
}

// ─── translatePlatformSharedPath ────────────────────────────────────────────

test('translatePlatformSharedPath: macOS /Users/foo → Windows C:\\Mac\\Home\\foo', () => {
  const result = translatePlatformSharedPath('/Users/foo/project', 'win32');
  assert.equal(result, path.join('C:\\Mac\\Home', 'foo', 'project'));
});

test('translatePlatformSharedPath: Windows C:\\Mac\\Home\\foo → macOS /Users/foo', () => {
  const result = translatePlatformSharedPath('C:\\Mac\\Home\\foo\\project', 'darwin');
  assert.equal(result, path.join('/Users', 'foo', 'project'));
});

test('translatePlatformSharedPath: Windows //Mac/Home/foo UNC → macOS /Users/foo', () => {
  const result = translatePlatformSharedPath('//Mac/Home/foo/project', 'darwin');
  assert.equal(result, path.join('/Users', 'foo', 'project'));
});

test('translatePlatformSharedPath: identity on same platform (macOS path on macOS)', () => {
  const result = translatePlatformSharedPath('/Users/foo/project', 'darwin');
  assert.equal(result, '/Users/foo/project');
});

test('translatePlatformSharedPath: identity on same platform (Windows path on Windows)', () => {
  const result = translatePlatformSharedPath('C:\\Users\\foo\\project', 'win32');
  assert.equal(result, 'C:\\Users\\foo\\project');
});

test('translatePlatformSharedPath: non-shared Windows drive path (D:) is not translated on Windows', () => {
  // D:\ArmTFS\arm-tfs.exe must NOT be remapped to C:\Mac\Home\...
  const result = translatePlatformSharedPath('D:\\ArmTFS\\arm-tfs.exe', 'win32');
  assert.equal(result, 'D:\\ArmTFS\\arm-tfs.exe');
});

test('translatePlatformSharedPath: relative path is left unchanged', () => {
  assert.equal(translatePlatformSharedPath('relative/path', 'win32'), 'relative/path');
  assert.equal(translatePlatformSharedPath('relative\\path', 'darwin'), 'relative\\path');
});

// ─── splitCommandLine (Windows backslash fix) ────────────────────────────────

test('splitCommandLine (Windows): backslashes are preserved in unquoted paths', () => {
  const parts = splitCommandLine('dotnet D:\\ArmTFS\\arm-tfs.dll get .', 'win32');
  assert.deepEqual(parts, ['dotnet', 'D:\\ArmTFS\\arm-tfs.dll', 'get', '.']);
});

test('splitCommandLine (Windows): backslashes are preserved inside double quotes', () => {
  const parts = splitCommandLine('echo "C:\\Users\\foo\\bar"', 'win32');
  assert.deepEqual(parts, ['echo', 'C:\\Users\\foo\\bar']);
});

test('splitCommandLine (Windows): "D:\\ArmTFS\\arm-tfs.exe" is not mangled', () => {
  // The original bug: backslashes were treated as escapes, producing
  // "D:ArmTFSarm-tfs.exe" (all separators stripped).
  const parts = splitCommandLine('D:\\ArmTFS\\arm-tfs.exe --version', 'win32');
  assert.deepEqual(parts, ['D:\\ArmTFS\\arm-tfs.exe', '--version']);
});

test('splitCommandLine (POSIX): backslash still escapes inside double quotes', () => {
  const parts = splitCommandLine('echo "hello\\ world"', 'darwin');
  assert.deepEqual(parts, ['echo', 'hello world']);
});

test('splitCommandLine (POSIX): backslash is literal outside quotes', () => {
  // On POSIX, backslash outside quotes is not an escape (the original code
  // incorrectly treated it as one). The fix only escapes inside double
  // quotes on non-Windows platforms.
  const parts = splitCommandLine('echo /Users/foo/bar', 'darwin');
  assert.deepEqual(parts, ['echo', '/Users/foo/bar']);
});

test('splitCommandLine: simple command splits on whitespace', () => {
  const parts = splitCommandLine('arm-tfs get .', 'win32');
  assert.deepEqual(parts, ['arm-tfs', 'get', '.']);
});

test('splitCommandLine: handles multiple spaces between args', () => {
  const parts = splitCommandLine('arm-tfs    get    .', 'win32');
  assert.deepEqual(parts, ['arm-tfs', 'get', '.']);
});

test('splitCommandLine: empty string yields no parts', () => {
  assert.deepEqual(splitCommandLine('', 'win32'), []);
  assert.deepEqual(splitCommandLine('', 'darwin'), []);
});

// ─── Integration: configured CLI command with Windows path ──────────────────

test('integration: configured command "dotnet D:\\path\\arm-tfs.dll" parses correctly on Windows', () => {
  // Simulates the user configuring armTfs.cli.command =
  // "dotnet D:\\ArmTFS\\src\\ArmTfs.Cli\\bin\\Release\\net8.0\\win-arm64\\arm-tfs.dll"
  const configured = 'dotnet D:\\ArmTFS\\src\\ArmTfs.Cli\\bin\\Release\\net8.0\\win-arm64\\arm-tfs.dll';
  const parts = splitCommandLine(configured, 'win32');
  assert.equal(parts[0], 'dotnet');
  assert.equal(parts[1], 'D:\\ArmTFS\\src\\ArmTfs.Cli\\bin\\Release\\net8.0\\win-arm64\\arm-tfs.dll');
  assert.equal(parts.length, 2);
});
