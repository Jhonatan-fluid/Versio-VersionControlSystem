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

/**
 * The server's repository list. Scoped: the admin configured `Web:Repositories`, and `repos` is
 * that list filtered to what THIS caller's credential can read. Unscoped: the server shows any
 * repository the credential can read, and the home page offers its open-a-repository form.
 */
export interface RepoList {
  scoped: boolean;
  repos: RepoInfo[];
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

/** An open pull request as the list shows it; names spelled by the server (rule 9). */
export interface Pull {
  number: number;
  title: string;
  author: string;
  when: string;
  base: string;
  baseLabel: string;
  head: string;
  headLabel: string;
  /** For comparing: the head COMMIT, reachable here even when the branch lives in a fork. */
  headSha: string;
  draft: boolean;
}

export interface PullList {
  truncated: boolean;
  /** Whether "New merge request" is offered: writes on here and a signed-in person. */
  writable?: boolean;
  pulls: Pull[];
}

/** A repository label: value is GitHub's own name, sent back to add or remove; label is shown. */
export interface RepoLabel {
  value: string;
  label: string;
  color: string;
}

/** One pull request: the list's fields plus its description, rendered as a README is. */
export interface PullView {
  number: number;
  title: string;
  author: string;
  when: string;
  base: string;
  baseLabel: string;
  headLabel: string;
  headSha: string;
  /** Where the changes are measured from (GitHub's three-dot diff); null when GitHub could not say. */
  mergeBase: string | null;
  draft: boolean;
  html: string;
}

export interface Release {
  tag: string;
  tagLabel: string;
  name: string;
  author: string;
  when: string;
  prerelease: boolean;
}

export interface ReleaseList {
  truncated: boolean;
  releases: Release[];
}

/** One release: the list's fields plus its notes, rendered as a README is. */
export interface ReleaseView extends Release {
  html: string;
}

/**
 * One revision of a pull request, sealed by the server (docs/web/PLAN.md, *Pull request review*,
 * C1). Every other review call carries `token`, so everything the page combines describes these
 * SHAs; the token proves only that the server made it, and grants nothing.
 */
export interface ReviewSnapshot {
  token: string;
  /** Whether to draw write controls: writes on for this repository and a signed-in person. */
  writable: boolean;
  /** Whether to draw the merge box: the merge tier on as well (S11). */
  canMerge?: boolean;
  number: number;
  title: string;
  author: string;
  when: string;
  draft: boolean;
  baseRef: string;
  baseLabel: string;
  headLabel: string;
  baseSha: string;
  mergeBaseSha: string;
  headSha: string;
  issuedAt: string;
  html: string;
}

/**
 * How a write ended, as the server reports it (docs/web/PLAN.md, C5): done; still in flight; or
 * unknown — it may or may not have been posted, and the page keeps the draft and says so. Errors
 * carry the server's code (snapshot-stale, line-not-in-diff, operation-expired, github-refused...).
 */
export interface WriteAnswer {
  status: number;
  outcome?: 'done' | 'pending' | 'unknown';
  id?: string;
  error?: string;
  message?: string;
}

/** Why a file takes file-level comments only; null when its lines can take them. */
export type InlineUnavailable = 'no-patch' | 'list-cut' | 'not-in-github-diff' | 'github-unavailable';

export interface ReviewFile {
  path: string;
  label: string;
  change: 'added' | 'modified' | 'deleted' | 'renamed';
  previousPath: string | null;
  previousLabel: string | null;
  object: string | null;
  inline: boolean;
  inlineUnavailable: InlineUnavailable | null;
}

export interface ReviewFiles {
  patchSource: 'pull' | 'compare' | 'none';
  githubListCut: boolean;
  total: number;
  truncated: boolean;
  files: ReviewFile[];
}

/**
 * Where one thread goes on a rendered drawing, decided by the server from the drawing's own anchors
 * (docs/web/PLAN.md, C3): on an element (exact, moved, line), on a drawn source line (hint), or
 * nowhere, with the reason (unplaced).
 */
export interface ThreadPlacement {
  threadId: string;
  outcome: 'exact' | 'moved' | 'line' | 'hint' | 'unplaced';
  dom: string | null;
  reason: string | null;
  hintPath: string | null;
  hintSide: 'LEFT' | 'RIGHT' | null;
  hintLine: number | null;
}

/** A stretch of changed lines a drawing may not show (docs/web/PLAN.md, C4); start 0 is the whole file. */
export interface CoverageGap {
  path: string;
  pathLabel: string;
  side: 'old' | 'new';
  start: number;
  end: number;
  reason: 'not-shown' | 'no-evidence' | 'not-drawn' | 'too-large' | string;
}

/**
 * A changed file's drawing, and how far it can be trusted: "complete" only when every changed line
 * of the object is one the drawing names as changed; "partial" with the gaps otherwise (C4).
 */
export interface ReviewFileView {
  view: View;
  coverage: 'complete' | 'partial' | 'raw';
  placements: ThreadPlacement[];
  gaps: CoverageGap[];
}

/**
 * The merge box's read (slice 6). tier false: merging is switched off here, and nothing is shown.
 * error: GitHub could not say whether a merge is allowed, and none is offered.
 */
export interface MergeStateView {
  tier: boolean;
  error?: string;
  state: string;
  merged: boolean;
  headSha: string;
  mergeStateStatus: string;
  mergeable: boolean;
  blocked: string | null;
  methods: string[];
  autoMergeAllowed: boolean;
  autoMerge: { method: string; by: string } | null;
  canDisableAutoMerge: boolean;
  mergeCommit: string | null;
}

/** The viewer's pending review: one per person per pull request, created on the commit it will approve. */
export interface PendingReviewView {
  review: {
    id: string;
    commit: string;
    onSnapshotHead: boolean;
    comments: { id: string; path: string; pathLabel: string; html: string }[];
  } | null;
}

/** Per changed file, whether its drawing covers every change: what the Approve dialog totals up. */
export interface CoverageSummary {
  files: { path: string; coverage: 'complete' | 'partial' | 'unchecked'; gaps: number }[];
}

export interface ReviewStatus {
  /** The pull request's live head or base is no longer the snapshot's. */
  stale: boolean;
  headMoved: boolean;
  baseMoved: boolean;
  summaryError: string | null;
  summary: {
    state: string;
    draft: boolean;
    merged: boolean;
    mergeable: string;
    reviewDecision: string | null;
    /** name is shown; value is the name as GitHub has it, sent back to remove it. */
    labels: { name: string; value: string; color: string }[];
    assignees: string[];
    reviewers: { login: string; state: string }[];
    /** GitHub refuses an author's approval: the page does not offer it. */
    viewerIsAuthor: boolean;
    /** The commit the viewer last reviewed, for "changes since your last review". */
    viewerLastReviewCommit: string | null;
    /** The title and description as they are now: shown, and what an edit starts from and names as replaced. */
    title: string;
    titleText: string;
    markdown: string;
    html: string;
    /** GitHub's word on whether the viewer may edit the pull request; edit controls follow it. */
    viewerCanUpdate: boolean;
  } | null;
  checksError: string | null;
  checks: { name: string; status: string; conclusion: string | null; url: string | null; source: string }[] | null;
}

export interface ReviewComment {
  id: string;
  author: string;
  when: string;
  /** The text to edit, Versio's tags removed; only when the viewer may edit. */
  markdown: string | null;
  canEdit: boolean;
  canDelete: boolean;
  /** Core's rendering of untrusted Markdown; the page sanitises it again before placing it. */
  html: string;
  commit: string | null;
  originalCommit: string | null;
}

export interface ReviewThread {
  id: string;
  path: string;
  pathLabel: string;
  resolved: boolean;
  outdated: boolean;
  /** GitHub's current line, or null when outdated or on the file. */
  line: number | null;
  startLine: number | null;
  side: 'LEFT' | 'RIGHT';
  originalLine: number | null;
  subject: 'LINE' | 'FILE';
  truncated: boolean;
  canReply: boolean;
  canResolve: boolean;
  canUnresolve: boolean;
  comments: ReviewComment[];
}

export interface ReviewThreads {
  truncated: boolean;
  threads: ReviewThread[];
}

export interface ReviewTimeline {
  truncated: boolean;
  items: {
    kind: string; actor: string; when: string; html: string | null; text: string | null; state: string | null; sha: string | null;
    id: string | null; markdown: string | null; canEdit: boolean; canDelete: boolean;
  }[];
}

export interface ReviewCommits {
  truncated: boolean;
  headFound: boolean;
  commits: { sha: string; headline: string; author: string; when: string; inSnapshot: boolean }[];
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

function reviewBase(owner: string, repo: string, number: number): string {
  return `${repoBase(owner, repo)}/pulls/${number}`;
}

/** The snapshot rides in a header, never the URL: it stays out of history and any logged query. */
function sealed(s: ReviewSnapshot): Record<string, string> {
  return { 'X-Versio-Snapshot': s.token };
}

/** A document's bytes: the PDF the docs tab embeds, the download behind every other row. */
export function rawUrl(owner: string, repo: string, ref: string, path: string): string {
  return `${repoBase(owner, repo)}/raw${query({ ref, path })}`;
}

/**
 * A write. Never throws on a refusal: every answer — done, pending, unknown, refused — is the
 * caller's to show. The header no cross-site request can send rides on every one.
 */
async function send(url: string, method: 'POST' | 'PATCH', headers: Record<string, string>, body: unknown): Promise<WriteAnswer> {
  let response: Response;
  try {
    response = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json', 'X-Versio-Request': '1', ...headers },
      body: JSON.stringify(body),
    });
  } catch {
    // The request may or may not have reached the server: exactly the unknown case.
    return { status: 0, outcome: 'unknown', message: 'The connection dropped. This may or may not have been posted; refresh to check.' };
  }
  let json: Omit<WriteAnswer, 'status'> = {};
  let ours = false;
  try {
    json = await response.json();
    ours = typeof json === 'object' && json !== null;
  } catch {
    // Not JSON: not this server's answer (it always answers JSON), so something in between spoke.
  }
  // Only this server's own answer can say a write was refused, and so that nothing happened. A
  // 5xx, or anything that is not its JSON — a proxy's 502 or 504 page, a load balancer's timeout —
  // may come after the request went through and GitHub did it: that is "unknown", and the key is
  // kept, exactly as for a dropped connection. Reading it as a refusal cleared the key, and the
  // next click could post the same comment twice (found by review, 2026-10-20).
  if (json.outcome === undefined && (response.status >= 500 || !ours)) {
    return { status: response.status, outcome: 'unknown', message: 'No clear answer came back. This may or may not have been posted; refresh to check before posting it again.' };
  }
  return { status: response.status, ...json };
}

async function get<T>(url: string, signal?: AbortSignal, headers?: Record<string, string>): Promise<T> {
  const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json', ...headers }, signal });
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
  repos: (signal?: AbortSignal) => get<RepoList>('/api/repos', signal),
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
  pulls: (owner: string, repo: string, signal?: AbortSignal) => get<PullList>(`${repoBase(owner, repo)}/pulls`, signal),
  pullView: (owner: string, repo: string, number: number, signal?: AbortSignal) =>
    get<PullView>(`${repoBase(owner, repo)}/pulls/view${query({ number: String(number) })}`, signal),
  review: {
    snapshot: (owner: string, repo: string, number: number, signal?: AbortSignal) =>
      get<ReviewSnapshot>(`${reviewBase(owner, repo, number)}/snapshot`, signal),
    files: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<ReviewFiles>(`${reviewBase(owner, repo, s.number)}/files`, signal, sealed(s)),
    fileView: (owner: string, repo: string, s: ReviewSnapshot, path: string, view: PaneView, layout: Layout, from?: string, signal?: AbortSignal) =>
      get<ReviewFileView>(`${reviewBase(owner, repo, s.number)}/files/view${query({ path, from, view, layout })}`, signal, sealed(s)),
    status: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<ReviewStatus>(`${reviewBase(owner, repo, s.number)}/status`, signal, sealed(s)),
    threads: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<ReviewThreads>(`${reviewBase(owner, repo, s.number)}/threads`, signal, sealed(s)),
    timeline: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<ReviewTimeline>(`${reviewBase(owner, repo, s.number)}/timeline`, signal, sealed(s)),
    commits: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<ReviewCommits>(`${reviewBase(owner, repo, s.number)}/commits`, signal, sealed(s)),
    pending: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<PendingReviewView>(`${reviewBase(owner, repo, s.number)}/reviews/pending`, signal, sealed(s)),
    mergeState: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<MergeStateView>(`${reviewBase(owner, repo, s.number)}/merge-state`, signal, sealed(s)),
    viewed: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<{ paths: string[] }>(`${reviewBase(owner, repo, s.number)}/viewed`, signal, sealed(s)),
    coverage: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<CoverageSummary>(`${reviewBase(owner, repo, s.number)}/coverage`, signal, sealed(s)),

    /**
     * A write. Never throws on a refusal: every answer — done, pending, unknown, refused — is the
     * page's to word. The header no cross-site request can send, a JSON body, and the snapshot.
     */
    write(owner: string, repo: string, s: ReviewSnapshot, path: string, method: 'POST' | 'PATCH', body: unknown): Promise<WriteAnswer> {
      return send(`${reviewBase(owner, repo, s.number)}${path}`, method, sealed(s), body);
    },

    preview: async (owner: string, repo: string, s: ReviewSnapshot, body: string): Promise<string> => {
      const answer = await api.review.write(owner, repo, s, '/markdown', 'POST', { body });
      return (answer as unknown as { html?: string }).html ?? '';
    },
  },
  /** Opens a pull request: a write like any other (an operation key, never thrown), with no snapshot because there is no pull request yet. */
  createPull: (owner: string, repo: string, body: { opKey: string; base: string; head: string; title: string; body: string; draft: boolean }) =>
    send(`${repoBase(owner, repo)}/pulls`, 'POST', {}, body),
  pullTemplate: (owner: string, repo: string, base: string, signal?: AbortSignal) =>
    get<{ markdown: string | null; path: string | null }>(`${repoBase(owner, repo)}/pulls/template${query({ base })}`, signal),
  labels: (owner: string, repo: string, signal?: AbortSignal) => get<{ labels: RepoLabel[] }>(`${repoBase(owner, repo)}/labels`, signal),
  releases: (owner: string, repo: string, signal?: AbortSignal) => get<ReleaseList>(`${repoBase(owner, repo)}/releases`, signal),
  releaseView: (owner: string, repo: string, tag: string, signal?: AbortSignal) =>
    get<ReleaseView>(`${repoBase(owner, repo)}/releases/view${query({ tag })}`, signal),

  /** Sign-out: a POST with the header no cross-site request can send (the server checks it). */
  async logout(): Promise<void> {
    await fetch('/auth/logout', { method: 'POST', credentials: 'same-origin', headers: { 'X-Versio-Request': '1' } });
  },

  signInUrl(returnUrl: string): string {
    return `/auth/login${query({ returnUrl })}`;
  },
};
