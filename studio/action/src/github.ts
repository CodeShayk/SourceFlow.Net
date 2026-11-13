// The one comment the action keeps on a pull request: found by its marker, updated in place, created once.
import { MARKER } from './diff.ts';

export interface GitHubTarget {
  /** https://api.github.com, or the GitHub Enterprise API. */
  api: string;
  token: string;
  /** owner/name */
  repository: string;
  issue: number;
}

export type Fetcher = (url: string, init?: RequestInit) => Promise<Response>;

async function call(fetcher: Fetcher, target: GitHubTarget, method: string, path: string, body?: unknown): Promise<unknown> {
  const response = await fetcher(target.api.replace(/\/$/, '') + path, {
    method,
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${target.token}`,
      'X-GitHub-Api-Version': '2022-11-28',
      'User-Agent': 'sourceflow-workflow-diff',
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  if (!response.ok) {
    throw new Error(`GitHub ${method} ${path}: ${response.status} ${await response.text()}`);
  }

  return response.status === 204 ? undefined : response.json();
}

/** The action's earlier comment on the pull request, if any. */
export async function findComment(target: GitHubTarget, fetcher: Fetcher = fetch): Promise<number | undefined> {
  for (let page = 1; page <= 50; page++) {
    const comments = (await call(fetcher, target, 'GET', `/repos/${target.repository}/issues/${target.issue}/comments?per_page=100&page=${page}`)) as Array<{ id: number; body?: string }>;
    const found = comments.find((c) => c.body?.startsWith(MARKER) === true);
    if (found !== undefined) {
      return found.id;
    }

    if (comments.length < 100) {
      return undefined;
    }
  }

  return undefined;
}

/**
 * Writes the comment: updates the earlier one, or creates it. With nothing to say (`create` false) and no earlier
 * comment, it does nothing. Returns what it did.
 */
export async function upsertComment(target: GitHubTarget, body: string, create = true, fetcher: Fetcher = fetch): Promise<'created' | 'updated' | 'skipped'> {
  const existing = await findComment(target, fetcher);
  if (existing !== undefined) {
    await call(fetcher, target, 'PATCH', `/repos/${target.repository}/issues/comments/${existing}`, { body });
    return 'updated';
  }

  if (!create) {
    return 'skipped';
  }

  await call(fetcher, target, 'POST', `/repos/${target.repository}/issues/${target.issue}/comments`, { body });
  return 'created';
}
