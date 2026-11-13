// The action's entry point: inputs from the runner, the pull request from its event, then diff, SVGs and the comment.
import { createInProcessClient } from '@sourceflow/core-client/node';
import ELK from 'elkjs/lib/elk.bundled.js';
import { appendFileSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { elkEngine } from '../../app/src/graph/layout.ts';
import { comment, workflowDiff, type FileDiff } from './diff.ts';
import { upsertComment, type Fetcher } from './github.ts';

export interface RunEnvironment {
  env: Record<string, string | undefined>;
  log: (line: string) => void;
  fetcher?: Fetcher;
  /** The core's _framework folder as a URL (default: the one bundled beside the action). */
  frameworkUrl?: string;
}

function input(env: Record<string, string | undefined>, name: string): string | undefined {
  const value = env[`INPUT_${name.toUpperCase()}`]?.trim();
  return value === undefined || value === '' ? undefined : value;
}

interface PullRequestEvent {
  number?: number;
  pull_request?: { number: number; base: { sha: string }; head: { sha: string } };
}

/** Runs the action; returns the changed files. */
export async function run({ env, log, fetcher, frameworkUrl }: RunEnvironment): Promise<FileDiff[]> {
  const event = env.GITHUB_EVENT_PATH === undefined ? {} : (JSON.parse(readFileSync(env.GITHUB_EVENT_PATH, 'utf8')) as PullRequestEvent);
  const base = input(env, 'base') ?? event.pull_request?.base.sha;
  const head = input(env, 'head') ?? event.pull_request?.head.sha;
  if (base === undefined || head === undefined) {
    throw new Error('No base and head: run the action on pull_request, or give it the base and head inputs.');
  }

  const cwd = env.GITHUB_WORKSPACE ?? process.cwd();
  const out = resolve(cwd, input(env, 'out') ?? 'workflow-diff');
  const theme = input(env, 'theme') === 'dark' ? 'dark' : 'light';
  const core = createInProcessClient(frameworkUrl ?? new URL('./framework/', import.meta.url).href);
  try {
    const files = await workflowDiff({ cwd, base, head, core, layout: elkEngine(new ELK()), out, theme });
    log(`${files.length} workflow file(s) changed between ${base.slice(0, 7)} and ${head.slice(0, 7)}.`);
    for (const file of files) {
      log(`  ${file.status} ${file.path}: ${file.unchanged ? 'same statechart' : `${file.changes.length} change(s)`}${file.files.length > 0 ? ` -> ${file.files.join(', ')}` : ''}`);
    }

    const issue = event.pull_request?.number ?? event.number;
    if (input(env, 'comment') !== 'false' && issue !== undefined && env.GITHUB_REPOSITORY !== undefined) {
      const runUrl = env.GITHUB_RUN_ID === undefined ? undefined : `${env.GITHUB_SERVER_URL ?? 'https://github.com'}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}`;
      const body = comment(files, { artifact: input(env, 'artifact') ?? 'workflow-diff', ...(runUrl === undefined ? {} : { runUrl }) });
      const done = await upsertComment(
        { api: env.GITHUB_API_URL ?? 'https://api.github.com', token: input(env, 'token') ?? '', repository: env.GITHUB_REPOSITORY, issue },
        body,
        files.length > 0,
        fetcher,
      );
      log(`Comment ${done}.`);
    }

    if (env.GITHUB_OUTPUT !== undefined) {
      appendFileSync(env.GITHUB_OUTPUT, `changed=${files.length}\nout=${out}\n`);
    }

    return files;
  } finally {
    core.dispose();
  }
}

// Started by the runner (dist/main.js), not imported by the tests.
if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  run({ env: process.env, log: (line) => console.log(line) }).catch((error: unknown) => {
    console.log(`::error::${(error as Error).message}`);
    process.exitCode = 1;
  });
}
