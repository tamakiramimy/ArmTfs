import { appendFileSync, existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';

const ignoreFileNames = ['.tfignore', '.arm-tfsignore'];

export interface IgnoreMatcher {
  isIgnored(localPath: string, isDirectory?: boolean): boolean;
}

interface IgnoreRule {
  pattern: string;
  negative: boolean;
  directoryOnly: boolean;
  anchored: boolean;
  hasSlash: boolean;
  hasGlob: boolean;
  regex: RegExp;
}

export function loadIgnoreMatcher(workspaceRoot: string): IgnoreMatcher {
  const rules: IgnoreRule[] = [];
  for (const fileName of ignoreFileNames) {
    const ignoreFile = path.join(workspaceRoot, fileName);
    if (!existsSync(ignoreFile)) {
      continue;
    }
    for (const line of readFileSync(ignoreFile, 'utf8').split(/\r?\n/)) {
      const rule = parseIgnoreRule(line);
      if (rule) {
        rules.push(rule);
      }
    }
  }

  return {
    isIgnored(localPath: string, isDirectory = false): boolean {
      const relativePath = normalizeRelativePath(path.relative(workspaceRoot, localPath));
      if (!relativePath || relativePath.startsWith('../')) {
        return false;
      }

      let ignored = false;
      for (const rule of rules) {
        if (matchesRule(rule, relativePath, isDirectory)) {
          ignored = !rule.negative;
        }
      }
      return ignored;
    },
  };
}

export function addIgnorePatternForPath(
  workspaceRoot: string,
  localPath: string,
): { added: boolean; pattern: string; filePath: string } {
  const relativePath = normalizeRelativePath(path.relative(workspaceRoot, localPath));
  const isDirectory = safeIsDirectory(localPath);
  const pattern = `/${relativePath}${isDirectory ? '/' : ''}`;
  const filePath = path.join(workspaceRoot, ignoreFileNames[0]);

  const current = existsSync(filePath) ? readFileSync(filePath, 'utf8') : '';
  const existing = new Set(
    current
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean),
  );
  if (existing.has(pattern)) {
    return { added: false, pattern, filePath };
  }

  if (!existsSync(filePath)) {
    writeFileSync(
      filePath,
      [
        '# arm-tfs ignore patterns',
        '# Uses gitignore-style patterns. Leading / anchors to this TFVC workspace root.',
        '',
      ].join('\n'),
    );
  } else if (current.length > 0 && !current.endsWith('\n')) {
    appendFileSync(filePath, '\n');
  }

  appendFileSync(filePath, `${pattern}\n`);
  return { added: true, pattern, filePath };
}

function parseIgnoreRule(rawLine: string): IgnoreRule | undefined {
  let line = rawLine.trim();
  if (!line || line.startsWith('#')) {
    return undefined;
  }

  let negative = false;
  if (line.startsWith('!')) {
    negative = true;
    line = line.slice(1).trim();
  }
  if (!line) {
    return undefined;
  }

  const directoryOnly = line.endsWith('/');
  const anchored = line.startsWith('/');
  line = line.replace(/^\/+/, '').replace(/\/+$/, '');
  if (!line) {
    return undefined;
  }

  const pattern = normalizeRelativePath(line);
  const hasSlash = pattern.includes('/');
  const hasGlob = /[*?[\]]/.test(pattern);
  return {
    pattern,
    negative,
    directoryOnly,
    anchored,
    hasSlash,
    hasGlob,
    regex: globToRegex(pattern),
  };
}

function matchesRule(rule: IgnoreRule, relativePath: string, isDirectory: boolean): boolean {
  const segments = relativePath.split('/');

  if (rule.directoryOnly) {
    if (rule.hasSlash || rule.anchored) {
      return relativePath === rule.pattern || relativePath.startsWith(`${rule.pattern}/`);
    }
    return segments.includes(rule.pattern);
  }

  if (rule.hasSlash || rule.anchored) {
    return rule.regex.test(relativePath);
  }

  if (!rule.hasGlob) {
    return segments.includes(rule.pattern);
  }

  const target = isDirectory ? segments[segments.length - 1] : path.posix.basename(relativePath);
  return rule.regex.test(target);
}

function globToRegex(pattern: string): RegExp {
  let output = '^';
  for (let index = 0; index < pattern.length; index += 1) {
    const char = pattern[index];
    const next = pattern[index + 1];
    if (char === '*' && next === '*') {
      output += '.*';
      index += 1;
      continue;
    }
    if (char === '*') {
      output += '[^/]*';
      continue;
    }
    if (char === '?') {
      output += '[^/]';
      continue;
    }
    output += escapeRegex(char);
  }
  output += '$';
  return new RegExp(output);
}

function escapeRegex(value: string): string {
  return value.replace(/[\\^$.*+?()[\]{}|]/g, '\\$&');
}

function normalizeRelativePath(relativePath: string): string {
  return relativePath
    .replace(/\\/g, '/')
    .replace(/^\/+/, '')
    .replace(/\/+$/, '')
    .toLowerCase();
}

function safeIsDirectory(localPath: string): boolean {
  try {
    return statSync(localPath).isDirectory();
  } catch {
    return false;
  }
}
