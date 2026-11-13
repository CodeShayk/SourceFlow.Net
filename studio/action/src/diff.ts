// What the workflow-diff action computes for a pull request (doc 15 §6.3, WFD-36): for every *.sfflow.yaml the range
// changes, the statechart before and after (drawn by the Studio's own SVG renderer, with the changes marked), the
// changes in words, and Mermaid diagrams for the comment. Git gives the two texts; the core draws the graphs.
import type { GraphResult, StudioCoreClient } from '@sourceflow/core-client';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { describeDiff, diffGraphs, type GraphDiff } from '../../app/src/graph/diff.ts';
import type { LayoutEngine } from '../../app/src/graph/layout.ts';
import { statechartPositions } from '../../app/src/graph/statechart.ts';
import { statechartSvg, type Mark, type SvgTheme } from '../../app/src/graph/svg.ts';

export const MARKER = '<!-- sourceflow-workflow-diff -->';

/** GitHub's limit on a comment's length. */
export const COMMENT_LIMIT = 65_536;

export type Core = Pick<StudioCoreClient, 'graph' | 'toMermaid'>;

export interface DiffOptions {
  /** The repository's working folder. */
  cwd: string;
  base: string;
  head: string;
  core: Core;
  layout: LayoutEngine;
  /** Where to write the SVGs (none: not written). */
  out?: string;
  theme?: SvgTheme;
}

export interface Side {
  graph?: GraphResult;
  svg?: string;
  mermaid?: string;
  /** Why it can't be drawn (the core's message). */
  error?: string;
}

export interface FileDiff {
  path: string;
  status: 'added' | 'removed' | 'modified';
  before?: Side;
  after?: Side;
  changes: string[];
  /** The statechart is the same (the text changed in comments or layout only). */
  unchanged: boolean;
  /** The SVG files written, relative to `out`. */
  files: string[];
}

function git(cwd: string, args: string[]): string {
  return execFileSync('git', args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
}

/** The workflow files the range changes, sorted. */
export function changedWorkflows(cwd: string, base: string, head: string): string[] {
  return git(cwd, ['diff', '--name-only', '--no-renames', '-z', base, head, '--', '*.sfflow.yaml'])
    .split('\0')
    .filter((p) => p.endsWith('.sfflow.yaml'))
    .sort();
}

function textAt(cwd: string, commit: string, path: string): string | undefined {
  try {
    return git(cwd, ['show', `${commit}:${path}`]);
  } catch {
    return undefined; // not in that commit
  }
}

async function draw(core: Core, layout: LayoutEngine, text: string): Promise<Side> {
  try {
    const graph = await core.graph({ yaml: text });
    return { graph, mermaid: (await core.toMermaid({ yaml: text })).text };
  } catch (error) {
    return { error: (error as Error).message };
  }
}

async function picture(side: Side, layout: LayoutEngine, marks: Map<string, Mark>, theme: SvgTheme, title: string): Promise<void> {
  if (side.graph !== undefined) {
    side.svg = statechartSvg(side.graph, await statechartPositions(side.graph, layout), { theme, title, marks });
  }
}

/** A file name for a workflow path: its folders joined with '__'. */
export function svgName(path: string, side: 'before' | 'after'): string {
  return `${path.replace(/\.sfflow\.yaml$/, '').replace(/[\\/]/g, '__').replace(/[^A-Za-z0-9_.-]/g, '-')}.${side}.svg`;
}

/** Diffs every changed workflow of `base..head`. */
export async function workflowDiff(options: DiffOptions): Promise<FileDiff[]> {
  const theme = options.theme ?? 'light';
  const results: FileDiff[] = [];
  for (const path of changedWorkflows(options.cwd, options.base, options.head)) {
    const beforeText = textAt(options.cwd, options.base, path);
    const afterText = textAt(options.cwd, options.head, path);
    const before = beforeText === undefined ? undefined : await draw(options.core, options.layout, beforeText);
    const after = afterText === undefined ? undefined : await draw(options.core, options.layout, afterText);
    const diff: GraphDiff = diffGraphs(before?.graph, after?.graph);
    if (before !== undefined) {
      await picture(before, options.layout, diff.before, theme, `${path} before`);
    }

    if (after !== undefined) {
      await picture(after, options.layout, diff.after, theme, `${path} after`);
    }

    const file: FileDiff = {
      path,
      status: before === undefined ? 'added' : after === undefined ? 'removed' : 'modified',
      ...(before === undefined ? {} : { before: { ...before, ...(before.mermaid === undefined ? {} : { mermaid: markMermaid(before.mermaid, before.graph!, diff.before) }) } }),
      ...(after === undefined ? {} : { after: { ...after, ...(after.mermaid === undefined ? {} : { mermaid: markMermaid(after.mermaid, after.graph!, diff.after) }) } }),
      changes: before?.error !== undefined || after?.error !== undefined ? [] : describeDiff(diff),
      unchanged: before?.graph !== undefined && after?.graph !== undefined && diff.unchanged,
      files: [],
    };
    if (options.out !== undefined) {
      mkdirSync(options.out, { recursive: true });
      for (const side of ['before', 'after'] as const) {
        const svg = file[side]?.svg;
        if (svg !== undefined) {
          const name = svgName(path, side);
          writeFileSync(join(options.out, name), svg);
          file.files.push(name);
        }
      }
    }

    results.push(file);
  }

  return results;
}

const MERMAID_CLASSES = [
  '    classDef added fill:#dafbe1,stroke:#1a7f37,stroke-width:3px',
  '    classDef removed fill:#ffebe9,stroke:#b42318,stroke-width:3px,stroke-dasharray:5 3',
  '    classDef changed fill:#fff8c5,stroke:#9a6700,stroke-width:3px',
];

/** Adds Mermaid classes for the marked states (Mermaid can't style transitions, so the comment lists those in words). */
export function markMermaid(mermaid: string, graph: GraphResult, marks: ReadonlyMap<string, Mark>): string {
  const ids = new Map<string, string>();
  for (const match of mermaid.matchAll(/^\s*state "([^"]+)" as (\S+)/gm)) {
    ids.set(match[1]!, match[2]!);
  }

  const byMark = new Map<Mark, string[]>();
  for (const node of graph.nodes) {
    const mark = marks.get(node.id);
    const id = ids.get(node.label) ?? ids.get(node.id) ?? (/^[A-Za-z_][A-Za-z0-9_]*$/.test(node.id) && mermaid.includes(node.id) ? node.id : undefined);
    if (mark !== undefined && id !== undefined) {
      byMark.set(mark, [...(byMark.get(mark) ?? []), id]);
    }
  }

  if (byMark.size === 0) {
    return mermaid;
  }

  const lines = [...MERMAID_CLASSES, ...[...byMark].map(([mark, list]) => `    class ${list.join(',')} ${mark}`)];
  return mermaid.replace(/\s*$/, '\n') + lines.join('\n') + '\n';
}

export interface CommentOptions {
  /** The artifact the SVGs are uploaded as. */
  artifact?: string;
  /** A link to the run (its artifacts). */
  runUrl?: string;
}

function mermaidBlock(title: string, side: Side | undefined, open: boolean): string {
  if (side?.mermaid === undefined) {
    return '';
  }

  return `<details${open ? ' open' : ''}><summary>${title}</summary>\n\n\`\`\`mermaid\n${side.mermaid.trimEnd()}\n\`\`\`\n\n</details>\n`;
}

/** The pull request comment. */
export function comment(files: readonly FileDiff[], options: CommentOptions = {}, withDiagrams = true): string {
  const parts = [MARKER, '## Workflow changes', ''];
  if (files.length === 0) {
    parts.push('This pull request no longer changes any workflow.');
    return parts.join('\n') + '\n';
  }

  parts.push(`${files.length} workflow file${files.length === 1 ? '' : 's'} changed. Legend: ＋ added, − removed, △ changed.`, '');
  for (const file of files) {
    parts.push(`### \`${file.path}\` · ${file.status}`, '');
    const broken = [file.before?.error === undefined ? undefined : `before: ${file.before.error}`, file.after?.error === undefined ? undefined : `after: ${file.after.error}`]
      .filter((e) => e !== undefined);
    if (broken.length > 0) {
      parts.push(...broken.map((b) => `> ✕ The statechart can't be drawn ${b}`), '');
    } else if (file.unchanged) {
      parts.push('No change to the statechart (comments or formatting only).', '');
    } else {
      const shown = file.changes.slice(0, 50);
      parts.push(...shown.map((c) => `- ${c}`));
      if (file.changes.length > shown.length) {
        parts.push(`- … and ${file.changes.length - shown.length} more`);
      }

      parts.push('');
    }

    if (withDiagrams && !file.unchanged) {
      parts.push(mermaidBlock('Before', file.before, false) + mermaidBlock('After', file.after, true));
    }
  }

  if (options.artifact !== undefined) {
    parts.push(`The before and after SVGs are in the \`${options.artifact}\` artifact${options.runUrl === undefined ? '' : ` of [this run](${options.runUrl})`}.`);
  }

  const body = parts.join('\n') + '\n';
  return body.length > COMMENT_LIMIT && withDiagrams ? comment(files, options, false).replace('## Workflow changes\n', '## Workflow changes\n\n(The diagrams are left out: the comment would be too long. See the SVGs.)\n') : body;
}
