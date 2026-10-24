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
  /** The desktop's row (FINISH.md Step 8): the vendor's pill, the object's type, the detail phrase, the change marks. */
  toolchain?: string;
  type?: string | null;
  typeName?: string | null;
  detail?: string;
  /** unmodified, added, modified, deleted, renamed, copied, untracked, conflicted. */
  status?: string;
  rollUp?: string;
  changedDescendants?: number;
}

export interface Tree {
  commit: string;
  /** The commit the marks are against, when the tree is a comparison. */
  base?: string | null;
  isVersio: boolean;
  truncated: boolean;
  problems: string[];
  nodes: TreeNode[];
}

/** One commit as GitHub's /commit/sha shows it (Step 8). */
export interface CommitDetail {
  sha: string;
  headline: string;
  body: string;
  author: string;
  login: string | null;
  avatar: string | null;
  when: string;
  committer: string | null;
  committed: string | null;
  parents: string[];
  additions: number;
  deletions: number;
  verified: boolean | null;
  url: string | null;
  filesTruncated: boolean;
  files: { path: string; label: string; status: string; additions: number; deletions: number; previousPath: string | null; previousLabel: string | null }[];
}

export interface Tag {
  name: string;
  label: string;
  commit: string;
}

/** "Used by" (Step 3c): the objects of the same project whose source names this one, at this commit. */
export interface Usages {
  name: string;
  usedBy: { path: string; label: string; name: string; type: string }[];
  /** The search stopped at its bound: the list is what was read, not everything. */
  truncated: boolean;
  searched: number;
}

/** A comment on a commit (Step 3b): on the commit as a whole, or one GitHub placed on a line of a file. */
export interface CommitComment {
  id: string;
  author: string;
  avatar: string | null;
  html: string;
  when: string;
  path: string | null;
  pathLabel: string | null;
  line: number | null;
  url: string | null;
}

/** One annotation a check run left on the code (Step 3b): where, how bad, and the CI's words, spelled. */
export interface CheckAnnotation {
  path: string;
  pathLabel: string;
  startLine: number;
  endLine: number;
  level: string;
  message: string;
  title: string | null;
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

/** A pull request as the list shows it (docs/web/FINISH.md, B10); names spelled by the server (rule 9). */
export interface Pull {
  number: number;
  title: string;
  author: string;
  authorAvatar?: string | null;
  /** Last updated. */
  when: string;
  createdAt?: string;
  /** OPEN, CLOSED or MERGED; absent from an older server's answer. */
  state?: string;
  base: string;
  baseLabel: string;
  head: string;
  headLabel: string;
  /** For comparing: the head COMMIT, reachable here even when the branch lives in a fork. */
  headSha: string;
  draft: boolean;
  labels?: { name: string; value: string; color: string }[];
  assignees?: { login: string; avatar?: string | null }[];
  reviewDecision?: string | null;
  checkRollup?: string | null;
  comments?: number;
}

export interface PullList {
  truncated: boolean;
  /** Whether "New pull request" is offered: writes on here and a signed-in person. */
  writable?: boolean;
  pulls: Pull[];
  /** The query as the server read it, GitHub's grammar normalised. */
  query?: string;
  /** Whether GitHub's search answered (a signed-in person), or REST's list by state (anonymous). */
  searched?: boolean;
  total?: number | null;
  open?: number | null;
  closed?: number | null;
  /** The next page's cursor (a search) or number (a list), or null at the end. */
  next?: string | null;
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
  /** The server recorded this refusal as the operation's own outcome (X-Versio-Settled): nothing was written under the key. */
  settled?: boolean;
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
  /** GitHub's "+12 −3" for the file, from its patch; absent or null without one. */
  additions?: number | null;
  deletions?: number | null;
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
  /** The rest of GitHub's box (FINISH.md Step 6); absent from an older server's answer. */
  headRef?: string;
  headRefLabel?: string;
  headRefExists?: boolean;
  isCrossRepository?: boolean;
  deleteBranchOnMerge?: boolean;
  hasMergeQueue?: boolean;
  inMergeQueue?: boolean;
  queuePosition?: number | null;
}

/** The viewer's pending review: one per person per pull request, created on the commit it will approve. */
export interface PendingReviewView {
  review: {
    id: string;
    commit: string;
    onSnapshotHead: boolean;
    /** More comments than were read: said, never shown as all of them. */
    truncated?: boolean;
    /** Each draft's words and place too (Step 5), so the review panel can edit it there; absent from an older server. */
    comments: { id: string; path: string; pathLabel: string; html: string; markdown?: string; line?: number | null; startLine?: number | null }[];
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
    /** A team carries the slug REST asks and withdraws it by. */
    reviewers: { login: string; state: string; isTeam?: boolean; avatar?: string | null; slug?: string | null }[];
    /** The pull request's node, what a reaction on its description names (Step 5). */
    nodeId?: string;
    milestoneNumber?: number | null;
    /** The viewer's notifications: SUBSCRIBED, UNSUBSCRIBED, IGNORED; null when GitHub did not say. */
    viewerSubscription?: string | null;
    canReact?: boolean;
    /** GitHub refuses an author's approval: the page does not offer it. */
    viewerIsAuthor: boolean;
    /** What GitHub's header and sidebar show (docs/web/FINISH.md, B1): absent from an older server's answer. */
    additions?: number;
    deletions?: number;
    changedFiles?: number;
    commits?: number;
    comments?: number;
    createdAt?: string | null;
    closedAt?: string | null;
    mergedAt?: string | null;
    mergedBy?: string | null;
    author?: string;
    authorAvatar?: string | null;
    authorAssociation?: string;
    locked?: boolean;
    lockReason?: string | null;
    milestone?: string | null;
    /** The head's repository as owner/name when it is a fork. */
    headRepository?: string | null;
    reactions?: Reaction[];
    /** The live head's combined check state: SUCCESS, FAILURE, PENDING, ERROR, EXPECTED; null when none. */
    checkRollup?: string | null;
    /** The checks branch protection requires on this pull request. */
    requiredChecks?: string[];
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
  /** More checks than were read: shown as cut, so a failure past the bound is never hidden behind passes. */
  checksTruncated?: boolean;
  /** The LIVE head's checks in counts, once the branch moved (Step 10); null until then, or when they could not be read. */
  liveChecks?: { head: string; total: number; failed: number; running: number; truncated: boolean } | null;
  liveChecksError?: string | null;
  checks: ReviewCheck[] | null;
}

/** One kind of reaction and how many gave it; whether the viewer is among them. */
export interface Reaction {
  content: string;
  count: number;
  viewerHasReacted: boolean;
}

/** One check on the reviewed commit: a check run or a commit status, with what GitHub's checks tab shows of it. */
export interface ReviewCheck {
  name: string;
  status: string;
  conclusion: string | null;
  url: string | null;
  source: string;
  id?: number | null;
  startedAt?: string | null;
  completedAt?: string | null;
  /** The GitHub App that ran it, or the status's creator. */
  app?: string | null;
  suite?: number | null;
  title?: string | null;
  /** The run's summary, Markdown by the CI, rendered by Core; the page sanitises it like any body. */
  summary?: string | null;
  annotations?: number;
  description?: string | null;
  /** Branch protection requires it, as GitHub marks the live head's checks. */
  required?: boolean;
}

export interface ReviewComment {
  id: string;
  author: string;
  when: string;
  /** The review this comment was written in, so the conversation lists its thread under that review. */
  reviewId?: string | null;
  /** GitHub's excerpt of the diff the comment sits on: text, drawn as a code block. */
  diffHunk?: string | null;
  association?: string;
  avatar?: string | null;
  editedAt?: string | null;
  url?: string | null;
  reactions?: Reaction[];
  /** A draft of the viewer's pending review: nobody else sees it yet. */
  pending?: boolean;
  /** Hidden on GitHub, and why (SPAM, ABUSE, OFF_TOPIC, OUTDATED, DUPLICATE, RESOLVED): drawn folded. */
  minimized?: boolean;
  minimizedReason?: string | null;
  /** The suggested change the comment carries (its ```suggestion block), as text; null without one. */
  suggestion?: string | null;
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

/** An issue or pull request a timeline event points at. */
export interface TimelineReference {
  number: number;
  title: string;
  repository: string;
  url: string | null;
  isPull: boolean;
  state: string;
}

/**
 * One entry of the conversation. `kind` is one of comment, review, commit, force-push, merged,
 * closed, reopened, ready, draft, labeled, unlabeled, assigned, unassigned, review-requested,
 * review-request-removed, review-dismissed, renamed, base-changed, base-force-push, head-deleted,
 * head-restored, cross-referenced, referenced, milestoned, demilestoned, locked, unlocked,
 * auto-merge-on, auto-merge-off, queued, dequeued, deployed.
 */
export interface TimelineItem {
  kind: string;
  actor: string;
  when: string;
  html: string | null;
  text: string | null;
  state: string | null;
  sha: string | null;
  /** A comment's or a review's node id. */
  id: string | null;
  markdown: string | null;
  canEdit: boolean;
  canDelete: boolean;
  /** What the event named: a label, a person, a title, a branch, a milestone, an environment. */
  target?: string | null;
  targetColor?: string | null;
  /** What was there before: the old title, the old base, the commit before a force-push. */
  previous?: string | null;
  reference?: TimelineReference | null;
  /** A dismissal's words, a lock's reason, "closes" on a cross-reference that will close this. */
  message?: string | null;
  association?: string;
  avatar?: string | null;
  editedAt?: string | null;
  url?: string | null;
  reactions?: Reaction[];
  verified?: boolean | null;
  minimized?: boolean;
  minimizedReason?: string | null;
}

export interface ReviewTimeline {
  truncated: boolean;
  items: TimelineItem[];
}

export interface ReviewCommit {
  sha: string;
  headline: string;
  author: string;
  when: string;
  inSnapshot: boolean;
  login?: string | null;
  avatar?: string | null;
  committer?: string | null;
  body?: string;
  verified?: boolean | null;
  parents?: number;
  url?: string | null;
}

export interface ReviewCommits {
  truncated: boolean;
  headFound: boolean;
  commits: ReviewCommit[];
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
  let parsed: unknown;
  try {
    parsed = await response.json();
  } catch {
    // Not JSON: not this server's answer (it always answers JSON), so something in between spoke.
  }
  const answer = readWriteAnswer(response.status, parsed);
  return response.headers.get('X-Versio-Settled') === '1' ? { ...answer, settled: true } : answer;
}

const outcomes = ['done', 'pending', 'unknown'] as const;

/**
 * What a write's answer means, read defensively. Only this server's own answer can say a write
 * happened or was refused: an object with an outcome it knows on a 2xx, or an object naming its
 * error on a 4xx. Everything else — a proxy's 502 or 504 page, a 5xx, JSON `null`, `{}` on a 200,
 * an array, a number — may come after the request went through and GitHub did it, so it is
 * "unknown" and the caller keeps the key, exactly as for a dropped connection.
 *
 * Twice found by review (2026-10-20): first a proxy's page read as a refusal and cleared the key;
 * then, after that fix, JSON `null` threw on `.outcome` and left the caller stuck submitting, and
 * `{}` on a 200 still read as a refusal. The rule is now stated once, here, by what IS known rather
 * than by listing what is not.
 */
export function readWriteAnswer(status: number, parsed: unknown): WriteAnswer {
  const body = parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : null;
  const text = (key: string) => (body && typeof body[key] === 'string' ? (body[key] as string) : undefined);
  const outcome = outcomes.find((o) => o === body?.outcome);
  if (outcome && status >= 200 && status < 300) {
    return { ...body, status, outcome, id: text('id'), message: text('message') };
  }
  const error = text('error');
  if (!outcome && error && status >= 400 && status < 500) {
    return { ...body, status, outcome: undefined, error, message: text('message') };
  }
  // Fields the server sent besides (the Markdown preview's html) are kept; the meaning is not.
  return { ...body, status, outcome: 'unknown', error: undefined, message: 'No clear answer came back. This may or may not have been posted; refresh to check before posting it again.' };
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
  /** The tree at `ref`; with `base`, marked with what changed since it, and with `changed` only what changed. */
  tree: (owner: string, repo: string, ref: string, signal?: AbortSignal, base?: string, changed?: boolean) =>
    get<Tree>(`${repoBase(owner, repo)}/tree${query({ ref, base, changed: changed ? '1' : undefined })}`, signal),
  commit: (owner: string, repo: string, sha: string, signal?: AbortSignal) => get<CommitDetail>(`${repoBase(owner, repo)}/commit${query({ sha })}`, signal),
  tags: (owner: string, repo: string, signal?: AbortSignal) => get<{ tags: Tag[]; truncated: boolean }>(`${repoBase(owner, repo)}/tags`, signal),
  view: (owner: string, repo: string, ref: string, path: string, view: PaneView, layout: Layout, signal?: AbortSignal) =>
    get<View>(`${repoBase(owner, repo)}/view${query({ ref, path, view, layout })}`, signal),
  usages: (owner: string, repo: string, ref: string, path: string, signal?: AbortSignal) =>
    get<Usages>(`${repoBase(owner, repo)}/usages${query({ ref, path })}`, signal),
  compare: (owner: string, repo: string, base: string, head: string, signal?: AbortSignal) =>
    get<Comparison>(`${repoBase(owner, repo)}/compare${query({ base, head })}`, signal),
  /** `full`: the desktop pane's Show full file; without it a comparison is folded to its changes, as the desktop opens. */
  compareView: (owner: string, repo: string, base: string, head: string, path: string, view: PaneView, layout: Layout, signal?: AbortSignal, full?: boolean) =>
    get<View>(`${repoBase(owner, repo)}/compare/view${query({ base, head, path, view, layout, full: full ? '1' : undefined })}`, signal),
  history: (owner: string, repo: string, ref: string, path?: string, signal?: AbortSignal) =>
    get<Commit[]>(`${repoBase(owner, repo)}/history${query({ ref, path })}`, signal),
  pulls: (owner: string, repo: string, signal?: AbortSignal, q?: string, after?: string) =>
    get<PullList>(`${repoBase(owner, repo)}/pulls${query({ q, after: after && !/^\d+$/.test(after) ? after : undefined, page: after && /^\d+$/.test(after) ? after : undefined })}`, signal),
  /** The new pull request's description previewed through the server's Markdown (a write-shaped call, as the review page's preview is). */
  previewMarkdown: async (owner: string, repo: string, body: string): Promise<string> => {
    const answer = await send(`${repoBase(owner, repo)}/markdown`, 'POST', {}, { body });
    return (answer as unknown as { html?: string }).html ?? '';
  },
  pullView: (owner: string, repo: string, number: number, signal?: AbortSignal) =>
    get<PullView>(`${repoBase(owner, repo)}/pulls/view${query({ number: String(number) })}`, signal),
  review: {
    snapshot: (owner: string, repo: string, number: number, signal?: AbortSignal) =>
      get<ReviewSnapshot>(`${reviewBase(owner, repo, number)}/snapshot`, signal),
    files: (owner: string, repo: string, s: ReviewSnapshot, signal?: AbortSignal) =>
      get<ReviewFiles>(`${reviewBase(owner, repo, s.number)}/files`, signal, sealed(s)),
    fileView: (owner: string, repo: string, s: ReviewSnapshot, path: string, view: PaneView, layout: Layout, from?: string, signal?: AbortSignal, full?: boolean) =>
      get<ReviewFileView>(`${reviewBase(owner, repo, s.number)}/files/view${query({ path, from, view, layout, full: full ? '1' : undefined })}`, signal, sealed(s)),
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
    /** The annotations one check run left: listed on the Checks tab, drawn on the file's lines. */
    annotations: (owner: string, repo: string, s: ReviewSnapshot, runId: number, signal?: AbortSignal) =>
      get<{ annotations: CheckAnnotation[] }>(`${reviewBase(owner, repo, s.number)}/checks/${runId}/annotations`, signal, sealed(s)),

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
  /** A commit's comments, and whether the viewer may add one. */
  commitComments: (owner: string, repo: string, sha: string, signal?: AbortSignal) =>
    get<{ writable: boolean; comments: CommitComment[]; truncated: boolean }>(`${repoBase(owner, repo)}/commit/comments${query({ sha })}`, signal),
  /** A comment on a commit as a whole: a write like any other, with no snapshot because a commit is its own revision. */
  addCommitComment: (owner: string, repo: string, sha: string, body: { opKey: string; body: string }) =>
    send(`${repoBase(owner, repo)}/commits/${e(sha)}/comments`, 'POST', {}, body),
  /** Opens a pull request: a write like any other (an operation key, never thrown), with no snapshot because there is no pull request yet. */
  createPull: (owner: string, repo: string, body: { opKey: string; base: string; head: string; title: string; body: string; draft: boolean }) =>
    send(`${repoBase(owner, repo)}/pulls`, 'POST', {}, body),
  pullTemplate: (owner: string, repo: string, base: string, signal?: AbortSignal) =>
    get<{ markdown: string | null; path: string | null }>(`${repoBase(owner, repo)}/pulls/template${query({ base })}`, signal),
  labels: (owner: string, repo: string, signal?: AbortSignal) => get<{ labels: RepoLabel[] }>(`${repoBase(owner, repo)}/labels`, signal),
  /** The open milestones: the number is what a write names, the title is shown. */
  milestones: (owner: string, repo: string, signal?: AbortSignal) => get<{ milestones: { number: number; title: string }[] }>(`${repoBase(owner, repo)}/milestones`, signal),
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
