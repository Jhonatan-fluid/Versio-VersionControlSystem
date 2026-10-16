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

export function historyUrl(owner: string, repo: string, ref: string, path?: string): string {
  return `${repoUrl(owner, repo)}/history/${seg(ref)}${path ? `/${pathSegments(path)}` : ''}`;
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

/** Pull requests are repository-level, so no ref; ?n= selects one. */
export function pullsUrl(owner: string, repo: string, number?: number): string {
  return `${repoUrl(owner, repo)}/pulls${number ? `?n=${number}` : ''}`;
}

/** Releases are repository-level, so no ref; ?tag= selects one. */
export function releasesUrl(owner: string, repo: string, tag?: string): string {
  return `${repoUrl(owner, repo)}/releases${tag ? `?tag=${seg(tag)}` : ''}`;
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
