// Every view has its own URL (docs/web/PLAN.md, "What the page looks like"), so a link can be
// pasted into a review. These build them; the router reads them back.

const seg = (value: string) => encodeURIComponent(value);

/** A file path as URL segments: each escaped, slashes kept. */
export function pathSegments(path: string): string {
  return path.split('/').map(seg).join('/');
}

export function repoUrl(owner: string, repo: string): string {
  return `/${seg(owner)}/${seg(repo)}`;
}

export function blobUrl(owner: string, repo: string, ref: string, path: string, params?: URLSearchParams): string {
  const q = params && params.toString() ? `?${params.toString()}` : '';
  return `${repoUrl(owner, repo)}/blob/${seg(ref)}/${pathSegments(path)}${q}`;
}

export function treeUrl(owner: string, repo: string, ref: string): string {
  return `${repoUrl(owner, repo)}/tree/${seg(ref)}`;
}

export function compareUrl(owner: string, repo: string, base: string, head: string, path?: string, params?: URLSearchParams): string {
  const p = new URLSearchParams(params);
  if (path) {
    p.set('path', path);
  } else {
    p.delete('path');
  }
  const q = p.toString() ? `?${p.toString()}` : '';
  return `${repoUrl(owner, repo)}/compare/${seg(base)}...${seg(head)}${q}`;
}

/** The commits of a ref, or of one path at that ref: GitHub's /commits/ref/path (it was /history). */
export function commitsUrl(owner: string, repo: string, ref: string, path?: string): string {
  return `${repoUrl(owner, repo)}/commits/${seg(ref)}${path ? `/${pathSegments(path)}` : ''}`;
}

/** One commit, as GitHub's /commit/sha. */
export function commitUrl(owner: string, repo: string, sha: string): string {
  return `${repoUrl(owner, repo)}/commit/${seg(sha)}`;
}

export function branchesUrl(owner: string, repo: string): string {
  return `${repoUrl(owner, repo)}/branches`;
}

export function tagsUrl(owner: string, repo: string): string {
  return `${repoUrl(owner, repo)}/tags`;
}

/** The Documentation tab, at one version of the repository; ?path= selects a document. */
export function docsUrl(owner: string, repo: string, ref: string, path?: string, params?: URLSearchParams): string {
  const p = new URLSearchParams(params);
  if (path) {
    p.set('path', path);
  } else {
    p.delete('path');
  }
  const q = p.toString() ? `?${p.toString()}` : '';
  return `${repoUrl(owner, repo)}/docs/${seg(ref)}${q}`;
}

/** The pull request list; ?q= carries GitHub's search grammar ("is:open author:me"). */
export function pullsUrl(owner: string, repo: string, query?: string): string {
  return `${repoUrl(owner, repo)}/pulls${query ? `?q=${encodeURIComponent(query)}` : ''}`;
}

/** Opening a pull request; either branch may be chosen in advance. */
export function newPullUrl(owner: string, repo: string, base?: string, head?: string): string {
  const p = new URLSearchParams();
  if (base) {
    p.set('base', base);
  }
  if (head) {
    p.set('head', head);
  }
  const q = p.toString() ? `?${p.toString()}` : '';
  return `${repoUrl(owner, repo)}/pulls/new${q}`;
}

export type PullTab = 'conversation' | 'commits' | 'checks' | 'files';

/**
 * One pull request's review, in GitHub's shapes: /pull/N for the conversation, /pull/N/commits,
 * /pull/N/checks and /pull/N/files (it was ?tab=). On Files changed, ?path= picks the file. The
 * revision is not in the URL on purpose: a pasted link opens the pull request as it is NOW, and
 * says so if that is not what the sender saw.
 */
export function pullUrl(owner: string, repo: string, number: number, tab?: PullTab | string, path?: string, params?: URLSearchParams): string {
  const p = new URLSearchParams(params);
  p.delete('tab');
  if (path) {
    p.set('path', path);
  } else {
    p.delete('path');
  }
  const q = p.toString() ? `?${p.toString()}` : '';
  const segment = tab && tab !== 'conversation' ? `/${tab}` : '';
  return `${repoUrl(owner, repo)}/pull/${number}${segment}${q}`;
}

/** The tab a /pull/N[/tab] URL names; anything unknown is the conversation. */
export function parsePullTab(segment: string | undefined): PullTab {
  return segment === 'commits' || segment === 'checks' || segment === 'files' ? segment : 'conversation';
}

/** Releases are repository-level, so no ref; one release is GitHub's /releases/tag/v1.2. */
export function releasesUrl(owner: string, repo: string, tag?: string): string {
  return `${repoUrl(owner, repo)}/releases${tag ? `/tag/${seg(tag)}` : ''}`;
}

/** "base...head" back into its two refs. */
export function parseRange(range: string): { base: string; head: string } | null {
  const at = range.indexOf('...');
  if (at <= 0 || at + 3 >= range.length) {
    return null;
  }
  return { base: range.slice(0, at), head: range.slice(at + 3) };
}

/**
 * What a person typed to open a repository: "owner/repo" or a github.com URL. Null when it is
 * neither. Only GitHub's own name alphabet is accepted, as the server accepts.
 */
export function parseRepoInput(input: string): { owner: string; repo: string } | null {
  const trimmed = input.trim().replace(/\.git$/, '');
  const match =
    /^(?:https?:\/\/(?:www\.)?github\.com\/)?([A-Za-z0-9_.-]{1,100})\/([A-Za-z0-9_.-]{1,100})\/?$/.exec(trimmed) ??
    /^https?:\/\/(?:www\.)?github\.com\/([A-Za-z0-9_.-]{1,100})\/([A-Za-z0-9_.-]{1,100})(?:\/.*)?$/.exec(trimmed);
  if (!match || match[2] === '.' || match[2] === '..') {
    return null;
  }
  return { owner: match[1]!, repo: match[2]! };
}
