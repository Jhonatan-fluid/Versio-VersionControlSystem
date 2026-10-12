// The server's JSON API, and nothing else: the page talks only to its own origin. No token ever
// passes through here; the session is an HttpOnly cookie the browser sends and no script can read.

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

export interface Me {
  signedIn: boolean;
  login: string | null;
  name: string | null;
  avatarUrl: string | null;
  signInAvailable: boolean;
  anonymousAccess: boolean;
  installUrl: string | null;
}

export interface RepoInfo {
  owner: string;
  name: string;
  private: boolean;
  defaultBranch: string;
}

export interface Branch {
  name: string;
  /** The name for display: hidden characters spelled by the server (security rule 9). */
  label: string;
  commit: string;
}

/** The branches, and whether the server listed only the first of them (its cap). */
export interface BranchList {
  branches: Branch[];
  truncated: boolean;
}

export interface TreeNode {
  /** For display, spelled by the server. */
  name: string;
  /** For navigation: the exact path. */
  path: string;
  /** The path for display, spelled. */
  label: string;
  kind: 'folder' | 'object' | 'project' | 'file' | 'library' | 'task' | 'device';
  primary: string | null;
  files: string[];
  children: TreeNode[];
}

export interface Tree {
  commit: string;
  isVersio: boolean;
  truncated: boolean;
  problems: string[];
  nodes: TreeNode[];
}

export interface View {
  kind: string;
  title: string;
  path: string;
  html: string;
  notices: string[];
  layers: string[];
  /** Each layer's path for display, spelled; the chips show these and ask for `layers`. */
  layerLabels: string[];
}

export interface ChangedFile {
  path: string;
  /** The path for display, spelled. */
  label: string;
  change: 'added' | 'modified' | 'deleted';
  object: string | null;
}

export interface Comparison {
  base: string;
  head: string;
  /** Every changed file, including any the server did not list. */
  total: number;
  /** The server listed only the first files (its cap); the page says so. */
  truncated: boolean;
  files: ChangedFile[];
}

export interface Commit {
  sha: string;
  message: string;
  author: string;
  when: string;
}

export type PaneView = 'rendered' | 'raw';
export type Layout = 'unified' | 'split';

const e = encodeURIComponent;

/** The query string for an object of the API: only the values given, each escaped. */
export function query(values: Record<string, string | undefined>): string {
  const parts = Object.entries(values)
    .filter((kv): kv is [string, string] => kv[1] !== undefined && kv[1] !== '')
    .map(([k, v]) => `${e(k)}=${e(v)}`);
  return parts.length ? `?${parts.join('&')}` : '';
}

export function repoBase(owner: string, repo: string): string {
  return `/api/repos/${e(owner)}/${e(repo)}`;
}

async function get<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' }, signal });
  if (!response.ok) {
    let body: { error?: string; message?: string } = {};
    try {
      body = await response.json();
    } catch {
      // A proxy's HTML error page: the status says enough.
    }
    throw new ApiError(response.status, body.error ?? 'error', body.message ?? response.statusText);
  }
  return (await response.json()) as T;
}

export const api = {
  me: (signal?: AbortSignal) => get<Me>('/api/me', signal),
  repo: (owner: string, repo: string, signal?: AbortSignal) => get<RepoInfo>(repoBase(owner, repo), signal),
  branches: (owner: string, repo: string, signal?: AbortSignal) => get<BranchList>(`${repoBase(owner, repo)}/branches`, signal),
  tree: (owner: string, repo: string, ref: string, signal?: AbortSignal) =>
    get<Tree>(`${repoBase(owner, repo)}/tree${query({ ref })}`, signal),
  view: (owner: string, repo: string, ref: string, path: string, view: PaneView, layout: Layout, signal?: AbortSignal) =>
    get<View>(`${repoBase(owner, repo)}/view${query({ ref, path, view, layout })}`, signal),
  compare: (owner: string, repo: string, base: string, head: string, signal?: AbortSignal) =>
    get<Comparison>(`${repoBase(owner, repo)}/compare${query({ base, head })}`, signal),
  compareView: (owner: string, repo: string, base: string, head: string, path: string, view: PaneView, layout: Layout, signal?: AbortSignal) =>
    get<View>(`${repoBase(owner, repo)}/compare/view${query({ base, head, path, view, layout })}`, signal),
  history: (owner: string, repo: string, ref: string, path?: string, signal?: AbortSignal) =>
    get<Commit[]>(`${repoBase(owner, repo)}/history${query({ ref, path })}`, signal),

  /** Sign-out: a POST with the header no cross-site request can send (the server checks it). */
  async logout(): Promise<void> {
    await fetch('/auth/logout', { method: 'POST', credentials: 'same-origin', headers: { 'X-Versio-Request': '1' } });
  },

  signInUrl(returnUrl: string): string {
    return `/auth/login${query({ returnUrl })}`;
  },
};
