import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { CheckAnnotation, CoverageGap, Layout, Me, PaneView, PendingReviewView, ReviewCheck, ReviewCommits, ReviewFile, ReviewFileView, ReviewSnapshot, ReviewStatus, ReviewThread, ReviewThreads, ReviewTimeline, ThreadPlacement } from '../api';
import { ApiError, api } from '../api';
import { ChangeSectionHead } from '../components/ChangeSectionHead';
import type { Annotation } from '../components/FragmentHost';
import { FragmentHost } from '../components/FragmentHost';
import { safeHref } from '../fragment';
import { FilesTree } from '../components/FilesTree';
import { CommentCard, CommitRow, EventRow, Minimized, Reactions, ReviewCard, RoleBadge, anchorOf } from '../components/Conversation';
import { MergeBox } from '../components/MergeBox';
import { ErrorPanel } from '../components/Pane';
import { PeekPane } from '../components/PeekPane';
import { AssigneesEditor, BaseEditor, LabelsEditor, LockAction, MilestoneEditor, StateActions, SubscriptionAction, TitleEditor } from '../components/PullMeta';
import { ReviewBar, ReviewersEditor } from '../components/ReviewBar';
import { ActionButton, CommentBox, ReasonAction, carryDraft, describe, newOpKey } from '../components/ReviewWrite';
import { RepoLayout } from '../components/RepoLayout';
import { Avatar, Blankslate, Flash, Icon, IssueLabel, Layout as TwoColumn, Loading, RelativeTime, SidebarSection, StateBadge, UnderlineNav, pullState, type NavItem } from '../components/ui';
import { blobUrl, compareUrl, parsePullTab, pullUrl, type PullTab } from '../routes';
import { usePageTitle } from '../title';
import type { Async } from '../useAsync';
import { useAsync } from '../useAsync';

type Tab = PullTab;

const tabs: { id: Tab; label: string }[] = [
  { id: 'conversation', label: 'Conversation' },
  { id: 'commits', label: 'Commits' },
  { id: 'checks', label: 'Checks' },
  { id: 'files', label: 'Files changed' },
];

const unavailable: Record<string, string> = {
  'no-patch': 'GitHub shows no line diff for this file (binary, or too large), so comments go on the file, not a line.',
  'list-cut': "This file is past the end of GitHub's file list for this pull request, so comments go on the file, not a line.",
  'not-in-github-diff': "GitHub's diff does not list this file although it changed, so comments go on the file, not a line.",
  'github-unavailable': "GitHub's diff for this revision could not be read, so comments go on the file, not a line.",
};

/** How often the live state is asked for while the page is visible. */
export const POLL_MS = 30_000;

/** A snapshot the server no longer accepts: the only way on is to load the pull request again. */
function needsReload(error: Error): boolean {
  return error instanceof ApiError && (error.code === 'snapshot-expired' || error.code === 'snapshot-invalid');
}

/**
 * Polls while the page is visible and once more whenever it becomes visible again. A failed poll
 * keeps the last answer: a blip must not wipe what the reviewer is reading. A new <paramref>key</paramref>
 * (another snapshot) starts empty; a new <paramref>refresh</paramref> (a write just landed) reads
 * again at once but keeps showing the last answer meanwhile, so an edit does not blank the panel.
 */
function usePolled<T>(load: ((signal: AbortSignal) => Promise<T>) | null, key: string, refresh = 0, onExpired?: () => void): T | null {
  const [value, setValue] = useState<T | null>(null);
  const shown = useRef(key);
  const expired = useRef(onExpired);
  expired.current = onExpired;
  useEffect(() => {
    if (shown.current !== key) {
      shown.current = key;
      setValue(null);
    }
    if (!load) {
      return;
    }
    const controller = new AbortController();
    let timer = 0;
    const poll = () => {
      if (document.visibilityState !== 'hidden') {
        // A blip keeps the last answer; a snapshot the server no longer accepts is said, or a tab
        // left open past the snapshot's life froze at its last answer with no banner until the
        // first write was refused (review, 2026-10-24). And the polling stops then: every further
        // poll would be the same refused request, every thirty seconds, until the tab closed.
        load(controller.signal).then((v) => !controller.signal.aborted && setValue(v), (error: Error) => {
          if (!controller.signal.aborted && needsReload(error)) {
            window.clearInterval(timer);
            expired.current?.();
          }
        });
      }
    };
    poll();
    timer = window.setInterval(poll, POLL_MS);
    document.addEventListener('visibilitychange', poll);
    return () => {
      controller.abort();
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', poll);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, refresh]);
  return value;
}

const never = () => new Promise<never>(() => {});

/**
 * What a write control needs: where to post, how to say the page should re-read, and how to say the
 * pull request moved. Null when nothing may be written (writes off, or signed out): then no control
 * is drawn at all (S9).
 */
interface Writer {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  /** Prefix for draft slots: one pull request's drafts never land in another's boxes. */
  slots: string;
  onChanged: () => void;
  onStale: () => void;
  /** The signed-in person, drawn beside their own comment box as GitHub draws them. */
  login: string;
  avatar: string | null;
  /**
   * "Add to review", when a comment may go into the viewer's pending review: there is none yet (the
   * first creates it on the snapshot's head), or there is one on that head. One on an older commit
   * takes no more comments here (C2), so the button is not drawn.
   */
  review: { path: string; label: string } | undefined;
  /** The merge tier is on (S11): suggested changes can be applied, which is a commit. */
  canMerge: boolean;
  /** The viewer's own reaction on a subject, toggled: safe to repeat, so a fresh key per click. */
  react: (subject: string, content: string, on: boolean) => void;
  /** Suggestions set aside to commit together, by comment id (GitHub's "Add suggestion to batch"). */
  batch: ReadonlySet<string>;
  toggleBatch: (commentId: string) => void;
}

/** GitHub's reasons for hiding a comment, as the Hide form offers them. */
const hideReasons = [
  { value: 'OUTDATED', label: 'Outdated' }, { value: 'RESOLVED', label: 'Resolved' }, { value: 'DUPLICATE', label: 'Duplicate' },
  { value: 'OFF_TOPIC', label: 'Off-topic' }, { value: 'SPAM', label: 'Spam' }, { value: 'ABUSE', label: 'Abuse' },
];

/** Hide (with a reason) or unhide a comment: a write for people with write access, which GitHub decides. */
function HideTools({ writer, id, minimized }: { writer: Writer; id: string; minimized: boolean }) {
  return minimized ? (
    <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/comments/${encodeURIComponent(id)}/minimize`} payload={{ classifier: null }} label="Unhide" onDone={writer.onChanged} onStale={writer.onStale} />
  ) : (
    <ReasonAction owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/comments/${encodeURIComponent(id)}/minimize`} label="Hide" kind="choice" options={hideReasons}
      payloadFor={(classifier) => ({ classifier })} submitLabel="Hide comment" onDone={writer.onChanged} onStale={writer.onStale} />
  );
}

/**
 * A suggested change in a comment, drawn as github.com draws it: the lines it proposes under a
 * "Suggested change" header, and — with the merge tier on, for a thread still on its lines of the
 * new file — "Apply suggestion" (one commit, now) and "Add suggestion to batch" (one commit for
 * several, from the Files changed bar).
 */
function Suggestion({ text, commentId, thread, writer }: { text: string; commentId: string; thread: ReviewThread; writer: Writer | null }) {
  const lines = text.replace(/\n$/, '').split('\n');
  const applicable = thread.side === 'RIGHT' && !thread.outdated && thread.subject === 'LINE' && thread.line !== null;
  const inBatch = writer?.batch.has(commentId) ?? false;
  return (
    <div className="suggestion">
      <div className="suggestion-head">
        <span className="text-bold">Suggested change</span>
        {writer?.canMerge && applicable && (
          <span className="suggestion-tools">
            <button type="button" className="btn btn-small" onClick={() => writer.toggleBatch(commentId)} aria-pressed={inBatch}>{inBatch ? 'Remove from batch' : 'Add suggestion to batch'}</button>
            <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path="/suggestions/apply" payload={{ comments: [commentId] }} label="Apply suggestion"
              confirm={`Commit this suggestion to ${writer.snap.headLabel}, on commit ${writer.snap.headSha.slice(0, 8)}? If anything is pushed first, GitHub refuses.`} onDone={writer.onChanged} onStale={writer.onStale} button />
          </span>
        )}
        {writer?.canMerge && !applicable && <span className="text-muted text-small">{thread.outdated ? 'Cannot be applied: the lines changed since.' : 'Applies only to lines of the new file.'}</span>}
      </div>
      <pre className="suggestion-body">{lines.map((l, i) => <span key={i} className="suggestion-line">{text.length === 0 ? '' : `+${l}`}{'\n'}</span>)}{text.length === 0 && <span className="text-muted">(the lines are removed)</span>}</pre>
    </div>
  );
}

/** Where a draft may also go: the pending review, unless the viewer's is on another commit. */
export function reviewAlternative(pending: PendingReviewView | null): { path: string; label: string } | undefined {
  if (!pending || (pending.review && !pending.review.onSnapshotHead)) {
    return undefined;
  }
  return { path: '/reviews/pending/threads', label: pending.review ? 'Add to review' : 'Start a review' };
}

/** The files opened on this page, per pull request, for "not opened here" in the review dialog. */
function readOpened(slot: string): string[] {
  try {
    const raw = sessionStorage.getItem(slot);
    return raw ? (JSON.parse(raw) as string[]) : [];
  } catch {
    return [];
  }
}

/** An attribute value for a selector: quotes and backslashes escaped, so a path cannot end it. */
function attr(value: string): string {
  return value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

/**
 * Where a thread sits on a rendered drawing, from the server's placement: on the element whose
 * data-anchors names it, or — a hint — on a drawn source line of its own file. Null when the server
 * placed it nowhere: the page lists it, never guesses.
 */
export function placementSelector(p: ThreadPlacement): string | null {
  if (p.dom && /^[rn]-(new|old)-\d+$/.test(p.dom) && (p.outcome === 'exact' || p.outcome === 'moved' || p.outcome === 'line')) {
    return `[data-anchors~="${p.dom}"]`;
  }
  if (p.outcome === 'hint' && p.hintPath && p.hintLine !== null && Number.isInteger(p.hintLine)) {
    const marker = p.hintSide === 'LEFT' ? 'data-old-file-line' : 'data-file-line';
    return `[data-src-path="${attr(p.hintPath)}"] [${marker}="${p.hintLine}"]`;
  }
  return null;
}

/** Where a thread sits in a Raw text diff: GitHub's side and line are the diff rows' own markers. */
export function threadSelector(thread: ReviewThread): string | null {
  if (thread.subject !== 'LINE' || thread.line === null || !Number.isInteger(thread.line)) {
    return null;
  }
  return thread.side === 'LEFT' ? `[data-old-line="${thread.line}"]` : `[data-line="${thread.line}"]`;
}

/**
 * /owner/repo/pull/12: one pull request's review — the conversation, the commits, the checks and
 * every changed file drawn as the desktop draws it (docs/web/PLAN.md, *Pull request review*).
 *
 * Everything is read against ONE snapshot (C1): the page asks for it first and carries it on every
 * other call, so a push while the page is open never mixes two revisions. The live state is polled;
 * when the pull request moves on, the page says so and offers to reload, rather than quietly
 * showing a drawing of one commit with comments placed for another.
 */
export function PullPage({ me }: { me: Me | null }) {
  // A new pull request is a new page: the router keeps this one mounted when only the number
  // changes, and its editors (title, description, the review) hold state that belongs to one pull
  // request — what an edit replaces, a draft — which must never carry over to the next.
  const { owner = '', repo = '', number = '' } = useParams();
  return <PullReview key={`${owner}/${repo}#${number}`} me={me} />;
}

function PullReview({ me }: { me: Me | null }) {
  const { owner = '', repo = '', number = '', tab: tabParam } = useParams();
  const n = Number(number);
  const [search] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  // GitHub's /pull/N/files; ?tab=files is the shape this page wrote before and still reads.
  const tab: Tab = parsePullTab(tabParam ?? search.get('tab') ?? undefined);
  const path = search.get('path');
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
  // The desktop pane's Show full file. Clear, its default there and here, a file opens folded.
  const full = search.get('full') === '1';
  const [loads, setLoads] = useState(0);
  // Bumped by every successful write, so threads and the conversation are read again.
  const [changed, setChanged] = useState(0);
  // A write was refused because the pull request moved: the same banner as the poll's.
  const [staleWrite, setStaleWrite] = useState(false);

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const snapshot = useAsync((s) => (Number.isInteger(n) && n > 0 ? api.review.snapshot(owner, repo, n, s) : Promise.reject(new Error('Not a pull request number.'))), [owner, repo, n, loads]);
  const snap = snapshot.state === 'ok' ? snapshot.value : null;
  const token = snap?.token ?? '';

  // Read again after every write: a title, a label or a close shows at once, not at the next poll.
  const [pollExpired, setPollExpired] = useState(false);
  useEffect(() => setPollExpired(false), [token]);
  const status = usePolled(snap ? (s) => api.review.status(owner, repo, snap, s) : null, token, changed, () => setPollExpired(true));
  // The annotations the checks left (Step 3b): read once per run that has any, for the Checks tab's
  // list and for the file sections, which draw each on its line as GitHub does. A run's id comes
  // from the status; the same ids read again say nothing new, so the key is the ids themselves.
  const annotatedRuns = (status?.checks ?? []).filter((c) => c.annotations && c.id).map((c) => c.id as number);
  const annotatedKey = annotatedRuns.join(',');
  const notes = useAsync<RunNote[]>(
    async (s) => {
      if (!snap || annotatedRuns.length === 0) {
        return [];
      }
      const read = await Promise.all(annotatedRuns.map((id) => api.review.annotations(owner, repo, snap, id, s)));
      // Each note keeps its run, so the Checks tab lists a run's own and not every run's.
      return read.flatMap((r, k) => r.annotations.map((a) => ({ ...a, runId: annotatedRuns[k]! })));
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [token, annotatedKey],
  );
  const live = status?.summary ?? null;
  // The merge box's state: read only with the merge tier on, and again after every write.
  const mergeState = useAsync((s) => (snap?.canMerge && tab === 'conversation' ? api.review.mergeState(owner, repo, snap, s) : never()), [token, tab, changed]);
  const [editing, setEditing] = useState<'title' | 'base' | null>(null);
  const files = useAsync((s) => (snap ? api.review.files(owner, repo, snap, s) : never()), [token]);
  const threads = useAsync((s) => (snap ? api.review.threads(owner, repo, snap, s) : never()), [token, changed]);
  const timeline = useAsync((s) => (snap && tab === 'conversation' ? api.review.timeline(owner, repo, snap, s) : never()), [token, tab, changed]);
  const commits = useAsync((s) => (snap && tab === 'commits' ? api.review.commits(owner, repo, snap, s) : never()), [token, tab]);

  // The viewer's own review state: only with writes on, since only then can anything be done with it.
  const writable = snap?.writable === true;
  const pending = useAsync((s) => (snap && writable ? api.review.pending(owner, repo, snap, s) : never()), [token, changed, writable]);
  const viewedRead = useAsync((s) => (snap && writable ? api.review.viewed(owner, repo, snap, s) : never()), [token, writable]);
  // A mark set here is shown at once; GitHub's list is read once per snapshot.
  const [viewedNow, setViewedNow] = useState<Record<string, boolean>>({});
  useEffect(() => setViewedNow({}), [token]);
  const viewed = useMemo(() => {
    const set = new Set(viewedRead.state === 'ok' ? viewedRead.value.paths : []);
    for (const [p, on] of Object.entries(viewedNow)) {
      if (on) {
        set.add(p);
      } else {
        set.delete(p);
      }
    }
    return set;
  }, [viewedRead, viewedNow]);
  const openedSlot = `versio:opened:${owner}/${repo}#${n}`;
  const [opened, setOpened] = useState<Set<string>>(() => new Set(readOpened(openedSlot)));
  useEffect(() => {
    if (tab === 'files' && path && !opened.has(path)) {
      const next = new Set(opened).add(path);
      setOpened(next);
      try {
        sessionStorage.setItem(openedSlot, JSON.stringify([...next]));
      } catch {
        // Storage off: remembered while the page lives.
      }
    }
  }, [tab, path, opened, openedSlot]);

  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  // The owner's first look at a preview (2026-10-24) found a page with no comment box, no reply
  // buttons and nothing saying why: the session cookie had gone stale, and a reader who cannot
  // write was simply shown less. The three ways that happens each get their sentence, where
  // GitHub puts its "Sign in to comment"; while who the reader is has not come back yet, nothing.
  const readOnlyNote: ReactNode = snap?.writable || me === null ? null
    : signIn ? <><a href={signIn}>Sign in</a> to comment, review or merge.</>
    : !me.signedIn ? 'This server has no sign-in, so the pull request is read-only here: comments, reviews and the merge box need a signed-in session.'
    : 'Writes are not turned on for this repository on this server, so the pull request is read-only here.';
  const reload = () => {
    setStaleWrite(false);
    setLoads((x) => x + 1);
  };
  // Suggestions batched to commit together; a new snapshot is a new review, with nothing batched.
  const [batch, setBatch] = useState<ReadonlySet<string>>(() => new Set());
  useEffect(() => setBatch(new Set()), [token]);
  const [reactNote, setReactNote] = useState<string | null>(null);
  const writer: Writer | null = snap?.writable
    ? {
        owner, repo, snap, slots: `versio:draft:${owner}/${repo}#${n}`, onChanged: () => setChanged((x) => x + 1), onStale: () => setStaleWrite(true),
        login: me?.login ?? '?', avatar: me?.avatarUrl ?? null,
        review: reviewAlternative(pending.state === 'ok' ? pending.value : null),
        canMerge: snap.canMerge === true,
        batch,
        toggleBatch: (id) => setBatch((b) => {
          const next = new Set(b);
          if (next.has(id)) {
            next.delete(id);
          } else {
            next.add(id);
          }
          return next;
        }),
        // A reaction is the viewer's own and safe to repeat: posted with a fresh key, read back by
        // the next refresh; a refusal is said once at the top, not held against the pill.
        react: (subject, content, on) => {
          void api.review.write(owner, repo, snap, '/reactions', 'POST', { subject, content, on, opKey: newOpKey() }).then((answer) => {
            if (answer.outcome === 'done') {
              setChanged((x) => x + 1);
            } else {
              setReactNote(describe(answer));
            }
          });
        },
      }
    : null;
  const last = status?.summary?.viewerLastReviewCommit ?? null;

  function markViewed(p: string, on: boolean) {
    if (!writer) {
      return;
    }
    // Viewed is the viewer's own bookkeeping and safe to repeat: no key is held across clicks.
    setViewedNow((v) => ({ ...v, [p]: on }));
    void api.review.write(owner, repo, writer.snap, '/viewed', 'POST', { path: p, viewed: on, opKey: newOpKey() }).then((answer) => {
      if (answer.outcome !== 'done') {
        setViewedNow((v) => ({ ...v, [p]: !on }));
      }
    });
  }
  const expired = pollExpired || [files, threads, timeline, commits].some((a) => a.state === 'error' && needsReload(a.error));

  function go(nextTab: Tab, nextPath?: string, hash = '') {
    navigate(pullUrl(owner, repo, n, nextTab === 'conversation' ? undefined : nextTab, nextPath, search) + hash);
  }

  const fileThreads = (p: string) => (threads.state === 'ok' ? threads.value.threads.filter((t) => t.path === p) : []);

  const pullTitle = live?.title ?? snap?.title ?? null;
  usePageTitle(pullTitle && snap ? `${pullTitle} by ${snap.author} · Pull Request #${n}` : `Pull Request #${n}`, `${owner}/${repo}`);

  // The tabs' counts, as GitHub's: the conversation's comments and the branch's commits from the
  // live summary, the checks from the list read, the files from the inventory.
  const navItems: NavItem[] = tabs.map((t) => ({
    id: t.id,
    label: t.label,
    to: pullUrl(owner, repo, n, t.id, undefined, search),
    icon: t.id === 'conversation' ? 'comment-discussion' : t.id === 'commits' ? 'git-commit' : t.id === 'checks' ? 'check-circle' : 'diff',
    count: t.id === 'files' && files.state === 'ok' ? files.value.total
      : t.id === 'checks' && status?.checks ? status.checks.length
      : t.id === 'commits' && live?.commits !== undefined ? live.commits
      : t.id === 'conversation' && live?.comments !== undefined ? live.comments
      : null,
  }));

  // A thread's permalink (#discussion_r…) names a comment; the file holding it is the one to open.
  const hash = location.hash.startsWith('#') ? location.hash.slice(1) : '';
  const hashPath = hash && threads.state === 'ok'
    ? threads.value.threads.find((t) => t.comments.some((c) => anchorOf(c.url) === hash))?.path ?? null
    : null;
  useEffect(() => {
    if (tab === 'files' && hashPath && hashPath !== path) {
      // The hash goes with it: a GitHub-shaped permalink (/pull/12/files#discussion_r…) names no
      // path, and navigating to the file without the hash lost the comment to scroll to
      // (review, 2026-10-24).
      go('files', hashPath, location.hash);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hashPath, tab]);
  // A comment's or review's permalink on the Conversation tab: the timeline arrives after the
  // browser's one load-time fragment scroll, so the page scrolls once it is drawn (review,
  // 2026-10-24). Files changed has its own, once the thread is placed.
  useEffect(() => {
    if (tab === 'conversation' && hash && timeline.state === 'ok') {
      const target = document.getElementById(hash);
      if (target && typeof target.scrollIntoView === 'function') {
        target.scrollIntoView({ block: 'start' });
      }
    }
  }, [tab, hash, timeline.state]);
  const inPull = (p: string) => files.state === 'ok' && files.value.files.some((f) => f.path === p);
  // A link to something this pull request does not change — a type, an identifier — opens a
  // peek at the reviewed head beside the review (FINISH.md C2, "stay in context"), never the
  // object's own page in place of the review; a link to a changed file goes to its section.
  const [peek, setPeek] = useState<string | null>(null);
  useEffect(() => setPeek(null), [tab, token]);
  const open = (p: string) => (inPull(p) ? go('files', p) : setPeek(p));

  const notices = snap && (
    <>
      {(status?.stale || staleWrite || expired) && (
        <p className="notice review-stale" role="status">
          {expired
            ? 'This review was loaded too long ago to go on with. '
            : `This pull request changed after you opened it${status?.headMoved ? ' (new commits)' : ''}${status?.baseMoved ? ` (${snap.baseLabel} moved)` : ''}. What you see is still the version you opened; nothing is mixed, and your drafts are kept. `}
          <button type="button" className="button" onClick={reload}>Load the latest</button>
        </p>
      )}
      {status?.summaryError === 'sign-in' && signIn && (
        <p className="notice" role="note">
          Without signing in, this page cannot tell whether the pull request has changed since you opened it, and review threads are not shown. <a href={signIn}>Sign in</a>
        </p>
      )}
      {reactNote && (
        <p className="notice" role="status">
          Your reaction was not saved: {reactNote} <button type="button" className="link-button" onClick={() => setReactNote(null)}>Dismiss</button>
        </p>
      )}
    </>
  );

  const revision = snap && (
    <p className="review-revision" title={`base ${snap.baseSha}\nmerge-base ${snap.mergeBaseSha}\nhead ${snap.headSha}`}>
      Reviewing {snap.headSha.slice(0, 8)}, compared from where it branched ({snap.mergeBaseSha.slice(0, 8)}).
      {/* Two-dot from the reviewed commit: after a force-push it also shows what the rewrite changed, which is the point. */}
      {last && last !== snap.headSha && (
        <> <Link to={compareUrl(owner, repo, last, snap.headSha)}>Changes since your last review ({last.slice(0, 8)})</Link></>
      )}
    </p>
  );

  const reviewBar = writer && snap && (
    <ReviewBar owner={owner} repo={repo} snap={snap} slots={writer.slots} status={status} pending={pending}
      files={files.state === 'ok' ? files.value.files : []} opened={opened} viewed={viewed}
      onChanged={writer.onChanged} onStale={writer.onStale} onOpenPath={(p) => go('files', p)} />
  );

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="pulls" crumb={`Pull request #${n}`} refName={defaultBranch} wide={tab === 'files'}>
      {snapshot.state === 'loading' ? (
        <Loading what="Reading the pull request…" />
      ) : snapshot.state === 'error' ? (
        <ErrorPanel error={snapshot.error} signInUrl={signIn} />
      ) : (
        <>
          {/* GitHub's pull request header: the title with its number, the state badge and the
              "wants to merge … into … from …" line under it, then the tabs across the page. */}
          <div className="pr-head">
            <div className="pr-title-row">
              {writer && live && editing === 'title' ? (
                <TitleEditor owner={owner} repo={repo} snap={writer.snap} summary={live} onChanged={writer.onChanged} onStale={writer.onStale} onDone={() => setEditing(null)} />
              ) : (
                <h1 className="pr-title">
                  {live?.title ?? snap!.title} <span className="pr-number">#{snap!.number}</span>
                </h1>
              )}
              {writer && live?.viewerCanUpdate && editing !== 'title' && (
                <div className="pr-title-actions">
                  <button type="button" className="btn btn-small" onClick={() => setEditing('title')}>Edit</button>
                </div>
              )}
            </div>
            <div className="pr-meta">
              <StateBadge state={pullState(live ?? snap!)} />
              {writer && live && editing === 'base' ? (
                <BaseEditor owner={owner} repo={repo} snap={writer.snap} branches={branches.state === 'ok' ? branches.value.branches.map((b) => b.name) : []}
                  current={snap!.baseRef} head={snap!.headLabel} onChanged={writer.onChanged} onStale={writer.onStale} onDone={() => setEditing(null)} />
              ) : (
                <span className="pr-meta-line">
                  <Avatar login={snap!.author} url={live?.authorAvatar} size={20} />
                  <span className="text-bold">{snap!.author}</span>
                  {' '}{live?.merged ? 'merged' : 'wants to merge'}{live?.commits !== undefined ? ` ${live.commits} commit${live.commits === 1 ? '' : 's'}` : ''} into{' '}
                  <span className="branch-name" title={snap!.baseLabel}>{snap!.baseLabel}</span> from{' '}
                  <span className="branch-name" title={live?.headRepository ? `${live.headRepository}:${snap!.headLabel}` : snap!.headLabel}>{live?.headRepository ? `${live.headRepository}:` : ''}{snap!.headLabel}</span>
                  {live?.locked && <span className="row-badge" title={live.lockReason ? `Locked as ${live.lockReason.toLowerCase().replace('_', ' ')}` : 'Locked'}><Icon name="lock" size={12} /> Locked</span>}
                  {writer && live?.viewerCanUpdate && live.state === 'OPEN' && (
                    <> <button type="button" className="link-button base-edit" onClick={() => setEditing('base')}>change base</button></>
                  )}
                </span>
              )}
            </div>
            {/* Keyed by the path: the peek keeps its own trail from the path it opened on, so a second link while it is open must start a new one (review, 2026-10-24). */}
            {peek && snap && <PeekPane key={peek} owner={owner} repo={repo} refName={snap.headSha} path={peek} onClose={() => setPeek(null)} signInUrl={signIn} />}
            <div className="pr-tabs">
              <UnderlineNav items={navItems} current={tab} label="Pull request" />
              {live?.additions !== undefined && live.deletions !== undefined && (
                <Diffstat additions={live.additions} deletions={live.deletions} files={live.changedFiles} />
              )}
            </div>
          </div>
          {notices}
          {tab === 'files' ? (
            <div className="files-view">
              <aside className="files-tree">
                {files.state === 'ok' && (
                  <FilesTree files={files.value.files} active={path} onPick={(p) => go('files', p)} threadCount={(p) => fileThreads(p).length}
                    viewed={viewed} onViewed={writer ? markViewed : null} truncated={files.value.truncated} total={files.value.total} />
                )}
                {files.state === 'loading' && <Loading what="Listing the files…" />}
              </aside>
              <div className="files-main">
                {revision}
                {reviewBar}
                {writer && writer.batch.size > 0 && (
                  <div className="batch-bar" role="status">
                    <span>{writer.batch.size} suggestion{writer.batch.size === 1 ? '' : 's'} in the batch.</span>
                    <ActionButton owner={owner} repo={repo} snap={writer.snap} path="/suggestions/apply" payload={{ comments: [...writer.batch] }} label={`Commit suggestions (${writer.batch.size})`}
                      confirm={`Commit ${writer.batch.size} suggestion${writer.batch.size === 1 ? '' : 's'} to ${snap!.headLabel} in one commit, on ${snap!.headSha.slice(0, 8)}? If anything is pushed first, GitHub refuses.`}
                      onDone={() => { setBatch(new Set()); writer.onChanged(); }} onStale={writer.onStale} button />
                    <button type="button" className="link-button" onClick={() => setBatch(new Set())}>Clear</button>
                  </div>
                )}
                {/* Every file, stacked, as GitHub stacks them: each section loads its drawing as it
                    comes into view, the one the URL names opens first and is scrolled to. A link
                    to another file of this pull request goes to that file's section; to anything
                    else, to the object at the reviewed commit. */}
                {files.state === 'ok' && files.value.files.length === 0 && <Blankslate icon="diff" heading="No files changed">This pull request changes nothing.</Blankslate>}
                {files.state === 'ok' && snap && files.value.files.map((f) => (
                  <FileSection
                    key={f.path}
                    writer={writer}
                    snap={snap}
                    owner={owner}
                    repo={repo}
                    file={f}
                    active={f.path === path}
                    threads={fileThreads(f.path)}
                    threadsState={threads}
                    initialView={view}
                    initialLayout={layout}
                    initialFull={full}
                    // A layer the pull request does not change (the .xml beside a changed .st) peeks, as a type link does: nothing leaves the review.
                    onLayer={open}
                    onOpenPath={open}
                    signInUrl={signIn}
                    viewed={viewed.has(f.path)}
                    onViewed={(on) => markViewed(f.path, on)}
                    hash={hash}
                    notes={notes.state === 'ok' ? notes.value : []}
                  />
                ))}
                {files.state === 'ok' && files.value.truncated && <p className="text-muted">The first {files.value.files.length} of {files.value.total} files are shown; the rest are on GitHub.</p>}
              </div>
            </div>
          ) : (
            <TwoColumn
              main={
                <>
                  {revision}
                  {reviewBar}
                  {tab === 'conversation' && (
                    <Conversation snap={snap!} live={live} timeline={timeline} writer={writer} onOpenPath={open} readOnly={readOnlyNote}
                      threads={threads.state === 'ok' ? threads.value.threads : []}
                      fileLink={(p) => pullUrl(owner, repo, n, 'files', p, search)}
                      renderThread={(t) => <Thread thread={t} writer={writer} onOpenPath={open} />}
                      // GitHub's order: the merge box closes the timeline, and the comment box comes after it.
                      mergeBox={
                        <>
                          <MergeSummary live={live} status={status} />
                          {writer && snap!.canMerge && <MergeBox owner={owner} repo={repo} snap={writer.snap} state={mergeState} onChanged={writer.onChanged} onStale={writer.onStale} />}
                        </>
                      } />
                  )}
                  {tab === 'commits' && <Commits commits={commits} />}
                  {tab === 'checks' && <Checks status={status} onOpenPath={open} notes={notes} fileLink={(p) => pullUrl(owner, repo, n, 'files', p, search)} />}
                </>
              }
              sidebar={<Summary status={status} writer={writer} />}
            />
          )}
        </>
      )}
    </RepoLayout>
  );
}

/** The live state, beside every tab but the file list: what the pull request is, now. */
function Summary({ status, writer }: { status: ReviewStatus | null; writer: Writer | null }) {
  if (!status) {
    return <Loading what="Reading the pull request's state…" />;
  }
  const s = status.summary;
  const checks = status.checks ?? [];
  const failed = checks.filter((c) => c.conclusion && !['success', 'neutral', 'skipped'].includes(c.conclusion)).length;
  const running = checks.filter((c) => c.status !== 'completed').length;
  const decision: Record<string, string> = { APPROVED: 'Approved', CHANGES_REQUESTED: 'Changes requested', REVIEW_REQUIRED: 'Review required' };
  // GitHub's right column: Reviewers, Labels, Assignees, each a small heading over its content.
  // The state badge is in the header, so it is not repeated here (it was, until 2026-10-23).
  return (
    <div className="review-summary">
      {s ? (
        <>
          {(writer && s.viewerCanUpdate) || s.reviewDecision || s.mergeable === 'CONFLICTING' ? (
            <SidebarSection heading="State">
              {s.reviewDecision && <p>{decision[s.reviewDecision] ?? s.reviewDecision}</p>}
              {s.mergeable === 'CONFLICTING' && <p className="text-danger">Has conflicts with its base</p>}
              {writer && s.viewerCanUpdate && <StateActions owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />}
              {writer && s.viewerCanUpdate && <LockAction owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />}
            </SidebarSection>
          ) : null}
          <SidebarSection heading="Reviewers">
            {writer ? (
              <ReviewersEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} reviewers={s.reviewers} onChanged={writer.onChanged} />
            ) : s.reviewers.length > 0 ? (
              <ul className="row-list">
                {s.reviewers.map((r) => (
                  <li key={r.login}>{r.login} <span className="text-muted">{r.state.toLowerCase().replace('_', ' ')}</span></li>
                ))}
              </ul>
            ) : (
              <p className="text-muted">No reviews</p>
            )}
          </SidebarSection>
          <SidebarSection heading="Labels">
            {writer && s.viewerCanUpdate ? (
              <LabelsEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />
            ) : s.labels.length > 0 ? (
              <p className="review-labels">
                {s.labels.map((l) => (
                  // The colour is six hex digits or empty: the server checked it, because it ends up here.
                  <IssueLabel key={l.value} name={l.name} color={l.color} />
                ))}
              </p>
            ) : (
              <p className="text-muted">None yet</p>
            )}
          </SidebarSection>
          {writer && s.viewerCanUpdate ? (
            <SidebarSection heading="Milestone">
              <MilestoneEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />
            </SidebarSection>
          ) : s.milestone ? (
            <SidebarSection heading="Milestone">
              <p><Icon name="milestone" className="text-muted" /> {s.milestone}</p>
            </SidebarSection>
          ) : null}
          {writer && s.viewerSubscription !== undefined && s.viewerSubscription !== null && (
            <SidebarSection heading="Notifications">
              <SubscriptionAction owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />
            </SidebarSection>
          )}
          <SidebarSection heading="Assignees">
            {writer && s.viewerCanUpdate ? (
              <AssigneesEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />
            ) : s.assignees.length > 0 ? (
              <p>{s.assignees.join(', ')}</p>
            ) : (
              <p className="text-muted">No one</p>
            )}
          </SidebarSection>
        </>
      ) : (
        <SidebarSection heading="State">
          <p className="text-muted">State unavailable{status.summaryError === 'sign-in' ? ' without signing in' : ''}.</p>
        </SidebarSection>
      )}
      {status.checks && (
        <SidebarSection heading="Checks">
          <p className="text-muted">
            {checks.length === 0 ? 'No checks' : `${checks.length} check${checks.length === 1 ? '' : 's'}`}
            {failed > 0 && <>, <span className="text-danger">{failed} failed</span></>}
            {running > 0 && `, ${running} running`}
            {status.checksTruncated && <>, <span className="text-danger">more on GitHub than shown</span></>}
          </p>
        </SidebarSection>
      )}
    </div>
  );
}

/** GitHub's "+12 −3" with its five-block bar, beside the tabs. */
function Diffstat({ additions, deletions, files }: { additions: number; deletions: number; files?: number }) {
  const total = additions + deletions;
  const blocks = total === 0 ? 0 : Math.max(1, Math.round((additions / total) * 5));
  return (
    <span className="diffstat pr-diffstat" title={`${additions} addition${additions === 1 ? '' : 's'} and ${deletions} deletion${deletions === 1 ? '' : 's'}${files !== undefined ? ` in ${files} file${files === 1 ? '' : 's'}` : ''}`}>
      <span className="diffstat-add">+{additions}</span>
      <span className="diffstat-del">−{deletions}</span>
      <span className="diffstat-bar" aria-hidden="true">
        {[0, 1, 2, 3, 4].map((k) => <span key={k} className={total === 0 ? '' : k < blocks ? 'is-add' : 'is-del'} />)}
      </span>
    </span>
  );
}

/**
 * What GitHub's merge box says to everyone, drawn before any button: the review decision, the
 * checks' combined state, conflicts. The buttons themselves are the merge tier's (MergeBox).
 */
function MergeSummary({ live, status }: { live: ReviewStatus['summary']; status: ReviewStatus | null }) {
  if (!live) {
    return null;
  }
  const checks = status?.checks ?? [];
  const failed = checks.filter((c) => c.conclusion && !['success', 'neutral', 'skipped'].includes(c.conclusion)).length;
  const running = checks.filter((c) => c.status !== 'completed').length;
  const rows: { tone: 'success' | 'danger' | 'attention' | 'muted'; icon: 'check-circle-fill' | 'x-circle-fill' | 'dot-fill' | 'clock'; text: string; detail?: string; list?: ReviewCheck[] }[] = [];
  if (live.reviewDecision === 'APPROVED') {
    rows.push({ tone: 'success', icon: 'check-circle-fill', text: 'Changes approved', detail: `${live.reviewers.filter((r) => r.state === 'APPROVED').length} approving review` });
  } else if (live.reviewDecision === 'CHANGES_REQUESTED') {
    rows.push({ tone: 'danger', icon: 'x-circle-fill', text: 'Changes requested', detail: `${live.reviewers.filter((r) => r.state === 'CHANGES_REQUESTED').map((r) => r.login).join(', ')} requested changes` });
  } else if (live.reviewDecision === 'REVIEW_REQUIRED') {
    rows.push({ tone: 'attention', icon: 'dot-fill', text: 'Review required', detail: 'At least one approving review is required by reviewers with write access.' });
  }
  if (checks.length > 0) {
    const required = checks.filter((c) => c.required);
    const requiredFailed = required.filter((c) => c.conclusion && !['success', 'neutral', 'skipped'].includes(c.conclusion)).length;
    // Each check under the summary, as GitHub's "Show all checks" lists them (Step 6).
    rows.push(failed > 0
      ? { tone: 'danger', icon: 'x-circle-fill', text: requiredFailed > 0 ? 'Some required checks were not successful' : 'Some checks were not successful', detail: `${failed} failing${running > 0 ? `, ${running} in progress` : ''}, ${checks.length - failed - running} successful`, list: checks }
      : running > 0
        ? { tone: 'attention', icon: 'clock', text: "Some checks haven't completed yet", detail: `${running} in progress, ${checks.length - running} successful`, list: checks }
        : { tone: 'success', icon: 'check-circle-fill', text: 'All checks have passed', detail: `${checks.length} successful check${checks.length === 1 ? '' : 's'}`, list: checks });
  }
  if (live.mergeable === 'CONFLICTING') {
    rows.push({ tone: 'danger', icon: 'x-circle-fill', text: 'This branch has conflicts that must be resolved', detail: 'Merge the base branch into this branch, or rebase it, then push.' });
  } else if (live.state === 'OPEN' && !live.merged) {
    rows.push({ tone: 'success', icon: 'check-circle-fill', text: 'This branch has no conflicts with the base branch' });
  }
  if (rows.length === 0) {
    return null;
  }
  return (
    <div className="merge-summary box">
      {rows.map((r) => (
        <div key={r.text} className={`merge-summary-row tone-${r.tone}`}>
          <Icon name={r.icon} className="merge-summary-icon" />
          <div className="merge-summary-text">
            <div className="text-bold">{r.text}</div>
            {r.detail && <div className="text-muted text-small">{r.detail}</div>}
            {r.list && (
              <details className="merge-summary-checks">
                <summary className="link-button">Show all checks</summary>
                <ul className="row-list">
                  {r.list.map((c, i) => {
                    const state = c.status !== 'completed' ? 'running' : c.conclusion ?? 'unknown';
                    const icon = state === 'success' ? 'check-circle-fill' : state === 'running' ? 'dot-fill' : state === 'neutral' || state === 'skipped' ? 'skip' : 'x-circle-fill';
                    return (
                      <li key={`${c.source}-${c.id ?? c.name}-${i}`} className={`check-${state} merge-summary-check`}>
                        <span className="check-mark"><Icon name={icon} size={14} /></span>
                        <span className="check-name">{c.name}</span>
                        {c.required && <span className="row-badge check-required">Required</span>}
                        <span className="text-muted text-small">{state === 'running' ? 'in progress' : state.replace('_', ' ')}</span>
                        {safeHref(c.url) && <a href={safeHref(c.url)!} target="_blank" rel="noopener noreferrer">Details</a>}
                      </li>
                    );
                  })}
                </ul>
              </details>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}

interface ConversationProps {
  snap: ReviewSnapshot;
  live: ReviewStatus['summary'];
  timeline: Async<ReviewTimeline>;
  writer: Writer | null;
  onOpenPath: (p: string) => void;
  /** Every review thread, so a review's own threads can be listed under it. */
  threads: ReviewThread[];
  fileLink: (path: string) => string;
  renderThread: (thread: ReviewThread) => ReactNode;
  /** The merge box, drawn where GitHub draws it: after the timeline, before the comment box. */
  mergeBox: ReactNode;
  /**
   * Why there is no comment box, for a reader who cannot write — drawn where the box would be,
   * as GitHub draws "Sign in to comment" there. Nothing while the writer is there, and nothing
   * while who the reader is has not been answered yet.
   */
  readOnly?: ReactNode;
}

function Conversation({ snap, live, timeline, writer, onOpenPath, threads, fileLink, renderThread, mergeBox, readOnly }: ConversationProps) {
  const [editing, setEditing] = useState<string | null>(null);
  // The description as it is now, once the live state is read; the snapshot's until then.
  const description = live ? live.html : snap.html;
  // The text an edit replaces is fixed when editing starts, and kept with the draft: the live
  // summary is polled, so taking it at submit would name someone else's newer text as the one
  // replaced and overwrite it. A draft that survived a reload keeps the text it was written
  // against, so the server can refuse it if that text has changed since.
  const [from, setFrom] = useState<string | null>(null);
  const fromSlot = writer ? `${writer.slots}:description:from` : '';
  function startEditing() {
    let kept: string | null = null;
    try {
      kept = sessionStorage.getItem(fromSlot);
      sessionStorage.setItem(fromSlot, kept ?? live!.markdown);
    } catch {
      // Storage off: the text is held as long as the page is.
    }
    setFrom(kept ?? live!.markdown);
    setEditing('description');
  }
  function stopEditing() {
    try {
      sessionStorage.removeItem(fromSlot);
    } catch {
      // Storage off.
    }
    setEditing(null);
  }
  // A review's threads are the ones whose first comment names it; the rest (written on
  // github.com outside a review, or whose review the server did not name) stay on Files changed.
  const threadsOf = (reviewId: string | null) => (reviewId ? threads.filter((t) => t.comments[0]?.reviewId === reviewId) : []);
  const descriptionTools = writer && live?.viewerCanUpdate && editing !== 'description'
    ? <button type="button" className="link-button" onClick={startEditing}>Edit</button>
    : undefined;
  // The viewer's reaction on something: only with a writer, and on the description only when
  // GitHub says they may react to it and named its node.
  const reactOn = (subject: string | null | undefined) => (writer && subject ? (content: string, on: boolean) => writer.react(subject, content, on) : undefined);
  // "Close with comment": the comment box's own submit, then the state change, each its own
  // operation with its own key. The close goes only once the comment is posted, as on GitHub.
  const submitComment = useRef<(() => Promise<boolean>) | null>(null);
  const [closing, setClosing] = useState(false);
  const [closeNote, setCloseNote] = useState<string | null>(null);
  async function changeState(open: boolean, withComment: boolean) {
    if (!writer || closing) {
      return;
    }
    setClosing(true);
    setCloseNote(null);
    const posted = withComment ? await (submitComment.current?.() ?? Promise.resolve(false)) : true;
    if (!posted) {
      setClosing(false);
      return;
    }
    const answer = await api.review.write(writer.owner, writer.repo, writer.snap, '/state', 'POST', { open, opKey: newOpKey() });
    setClosing(false);
    if (answer.outcome === 'done') {
      writer.onChanged();
    } else {
      if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
        writer.onStale();
      }
      setCloseNote(describe(answer));
    }
  }
  const [hasDraft, setHasDraft] = useState(false);
  const canClose = writer && live && live.viewerCanUpdate && !live.merged;
  return (
    <>
      {/* GitHub's first card is the description, by the author, with the body's reactions. */}
      <CommentCard author={snap.author} avatar={live?.authorAvatar} association={live?.authorAssociation} when={live?.createdAt ?? snap.when}
        verb={live?.draft ?? snap.draft ? 'opened this draft' : 'opened this'} tools={descriptionTools} reactions={live?.reactions}
        onReact={live?.canReact ? reactOn(live.nodeId) : undefined}>
        {writer && from !== null && editing === 'description' ? (
          // It names the text it replaces: someone else's edit meanwhile is refused, never overwritten.
          <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:description`} path="/edit"
            payload={{ expectBody: from }} initial={from} submitLabel="Save" placeholder="Describe the change"
            onCancel={stopEditing} onDone={() => { stopEditing(); writer.onChanged(); }} onStale={writer.onStale} />
        ) : description ? (
          <FragmentHost html={description} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />
        ) : (
          <p className="text-muted"><em>No description provided.</em></p>
        )}
      </CommentCard>
      {timeline.state === 'loading' && <Loading what="Reading the conversation…" />}
      {timeline.state === 'error' && <p className="text-muted">{timeline.error.message}</p>}
      {timeline.state === 'ok' && (
        <ol className="timeline">
          {timeline.value.items.map((item, i) => {
            if (item.kind === 'comment') {
              const tools = writer && item.id && editing !== item.id ? (
                <>
                  {item.canEdit && <button type="button" className="link-button" onClick={() => setEditing(item.id)}>Edit</button>}
                  {/* Hiding is for people with write access; GitHub refuses the rest in its own words. */}
                  <HideTools writer={writer} id={item.id} minimized={item.minimized === true} />
                  {item.canDelete && <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/comments/${encodeURIComponent(item.id)}/delete`} label="Delete" confirm="Delete this comment on GitHub?" onDone={writer.onChanged} />}
                </>
              ) : undefined;
              return (
                <li key={item.id ?? i} className="timeline-item timeline-comment">
                  <CommentCard author={item.actor} avatar={item.avatar} association={item.association} when={item.when} editedAt={item.editedAt} url={item.url} anchor={anchorOf(item.url)} tools={tools} reactions={item.reactions}
                    onReact={reactOn(item.id)} minimized={item.minimized === true} minimizedReason={item.minimizedReason}>
                    {writer && item.id && editing === item.id ? (
                      <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:edit:${item.id}`} path={`/comments/${encodeURIComponent(item.id)}`} method="PATCH"
                        initial={item.markdown ?? ''} submitLabel="Save" onCancel={() => setEditing(null)} onDone={() => { setEditing(null); writer.onChanged(); }} onStale={writer.onStale} />
                    ) : (
                      item.html && <FragmentHost html={item.html} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />
                    )}
                  </CommentCard>
                </li>
              );
            }
            if (item.kind === 'review') {
              // An approval or a request for changes can be dismissed by anyone with write access (GitHub
              // decides); the summary is the author's to edit. Both as GitHub's own page offers them.
              const dismissable = item.state === 'APPROVED' || item.state === 'CHANGES_REQUESTED';
              const tools = writer && item.id && editing !== item.id && (item.canEdit || dismissable) ? (
                <>
                  {item.canEdit && <button type="button" className="link-button" onClick={() => setEditing(item.id)}>Edit</button>}
                  {dismissable && (
                    <ReasonAction owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/reviews/${encodeURIComponent(item.id)}/dismiss`} label="Dismiss review" kind="text"
                      placeholder="Why is this review being dismissed?" payloadFor={(body) => ({ body })} submitLabel="Dismiss review" onDone={writer.onChanged} onStale={writer.onStale} />
                  )}
                </>
              ) : undefined;
              const editor = writer && item.id && editing === item.id ? (
                <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:edit-review:${item.id}`} path={`/reviews/${encodeURIComponent(item.id)}`} method="PATCH"
                  initial={item.markdown ?? ''} submitLabel="Save" onCancel={() => setEditing(null)} onDone={() => { setEditing(null); writer.onChanged(); }} onStale={writer.onStale} />
              ) : undefined;
              return <ReviewCard key={item.id ?? i} item={item} threads={threadsOf(item.id)} renderThread={renderThread} onOpenPath={onOpenPath} fileLink={fileLink} tools={tools} editor={editor} onReact={reactOn(item.id)} />;
            }
            if (item.kind === 'commit') {
              return <CommitRow key={`${item.sha}-${i}`} item={item} />;
            }
            return <EventRow key={i} item={item} baseLabel={snap.baseLabel} />;
          })}
        </ol>
      )}
      {timeline.state === 'ok' && timeline.value.truncated && <p className="text-muted">The conversation continues on GitHub.</p>}
      {mergeBox}
      {!writer && readOnly && (
        <div className="comment-row comment-row-new">
          <div className="comment comment-read-only" role="note">
            <div className="comment-body text-muted">{readOnly}</div>
          </div>
        </div>
      )}
      {writer && (
        <div className="comment-row comment-row-new">
          <Avatar login={writer.login} url={writer.avatar} size={40} />
          <div className="comment comment-new">
            <div className="comment-head"><span className="text-bold">Add a comment</span></div>
            <div className="comment-body" onInput={(e) => setHasDraft(((e.target as HTMLTextAreaElement).value ?? '').trim().length > 0)}>
              {live?.locked && <p className="notice" role="note"><Icon name="lock" size={12} /> This conversation is locked{live.lockReason ? ` as ${live.lockReason.toLowerCase().replace('_', ' ')}` : ''}. Only people with write access can comment.</p>}
              <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:conversation`} path="/comments"
                submitLabel="Comment" onDone={() => { setHasDraft(false); writer.onChanged(); }} onStale={writer.onStale} submitRef={submitComment} />
              {canClose && (
                // GitHub's buttons beside Comment: close (with the comment, when one is written), or reopen.
                <div className="comment-actions comment-state-actions">
                  {closeNote && <span className="comment-note-inline" role="status">{closeNote}</span>}
                  {live.state === 'OPEN' ? (
                    <button type="button" className="btn btn-small" disabled={closing} onClick={() => void changeState(false, hasDraft)}>
                      <Icon name="git-pull-request-closed" className="btn-icon text-danger" /> {hasDraft ? 'Close with comment' : 'Close pull request'}
                    </button>
                  ) : (
                    <button type="button" className="btn btn-small" disabled={closing} onClick={() => void changeState(true, hasDraft)}>
                      <Icon name="git-pull-request" className="btn-icon text-success" /> {hasDraft ? 'Reopen with comment' : 'Reopen pull request'}
                    </button>
                  )}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/** The duration GitHub prints beside a check: "4m 12s", "38s". */
function duration(startedAt?: string | null, completedAt?: string | null): string | null {
  if (!startedAt) {
    return null;
  }
  const end = completedAt ? new Date(completedAt) : new Date();
  const seconds = Math.round((end.getTime() - new Date(startedAt).getTime()) / 1000);
  if (!Number.isFinite(seconds) || seconds < 0) {
    return null;
  }
  const m = Math.floor(seconds / 60);
  return m > 0 ? `${m}m ${seconds % 60}s` : `${seconds}s`;
}

function Commits({ commits }: { commits: Async<ReviewCommits> }) {
  const [open, setOpen] = useState<Set<string>>(new Set());
  if (commits.state !== 'ok') {
    return commits.state === 'loading' ? <Loading what="Reading the commits…" /> : <p className="text-muted">{commits.error.message}</p>;
  }
  const c = commits.value;
  const toggle = (sha: string) => setOpen((s) => {
    const next = new Set(s);
    if (next.has(sha)) {
      next.delete(sha);
    } else {
      next.add(sha);
    }
    return next;
  });
  return (
    <>
      {!c.headFound && <p className="notice" role="note">The commit you are reviewing is no longer on this branch (it was force-pushed). None of these commits is the one shown.</p>}
      {c.commits.length === 0 && <Blankslate icon="git-commit" heading="No commits">This branch has no commits of its own.</Blankslate>}
      {c.commits.length > 0 && (
        <ul className="commits commit-group">
          {c.commits.map((x) => (
            <li key={x.sha} className={`commit-row${x.inSnapshot ? '' : ' commit-new'}`}>
              <div className="commit-main">
                <div className="commit-subject-line">
                  <span className="commit-subject">{x.headline}</span>
                  {x.body && (
                    <button type="button" className="btn btn-small commit-expand" aria-expanded={open.has(x.sha)} onClick={() => toggle(x.sha)} title="The rest of the message">
                      <Icon name="kebab-horizontal" size={12} />
                    </button>
                  )}
                  {x.verified === true && <span className="verified-badge" title="This commit was signed with a verified signature">Verified</span>}
                  {(x.parents ?? 1) > 1 && <span className="row-badge" title="A merge commit">Merge</span>}
                  {!x.inSnapshot && c.headFound && <span className="row-badge commit-new-badge">new since you opened this review</span>}
                </div>
                {open.has(x.sha) && x.body && <pre className="commit-body">{x.body}</pre>}
                <div className="commit-byline">
                  <Avatar login={x.login ?? x.author} url={x.avatar} size={16} />
                  <span className="text-bold">{x.login ?? x.author}</span>
                  {x.committer && <span>authored and <span className="text-bold">{x.committer}</span> committed</span>}
                  {!x.committer && <span>committed</span>}
                  <RelativeTime when={x.when} />
                </div>
              </div>
              <div className="commit-side">
                {safeHref(x.url) ? <a href={safeHref(x.url)!} target="_blank" rel="noopener noreferrer" className="commit-sha" title="This commit on GitHub">{x.sha.slice(0, 7)}</a> : <code className="commit-sha" title={x.sha}>{x.sha.slice(0, 7)}</code>}
              </div>
            </li>
          ))}
        </ul>
      )}
      {c.truncated && <p className="text-muted">GitHub lists the first 250 commits; the rest are on GitHub.</p>}
    </>
  );
}

/** The icon, colour and word for a check's state, as GitHub's checks page draws them. */
function checkLook(c: ReviewCheck): { icon: 'check-circle-fill' | 'x-circle-fill' | 'skip' | 'dot-fill' | 'clock' | 'circle-slash' | 'alert'; tone: string; word: string } {
  if (c.status !== 'completed') {
    return { icon: c.status === 'queued' || c.status === 'pending' ? 'clock' : 'dot-fill', tone: 'check-running', word: c.status === 'in_progress' ? 'In progress' : c.status === 'queued' ? 'Queued' : 'Pending' };
  }
  switch (c.conclusion) {
    case 'success': return { icon: 'check-circle-fill', tone: 'check-success', word: 'Successful' };
    case 'neutral': return { icon: 'skip', tone: 'check-neutral', word: 'Neutral' };
    case 'skipped': return { icon: 'skip', tone: 'check-skipped', word: 'Skipped' };
    case 'cancelled': return { icon: 'circle-slash', tone: 'check-cancelled', word: 'Cancelled' };
    case 'timed_out': return { icon: 'x-circle-fill', tone: 'check-timed_out', word: 'Timed out' };
    case 'action_required': return { icon: 'alert', tone: 'check-action_required', word: 'Action required' };
    case 'stale': return { icon: 'alert', tone: 'check-neutral', word: 'Stale' };
    default: return { icon: 'x-circle-fill', tone: 'check-failure', word: c.conclusion === 'failure' ? 'Failing' : c.conclusion === 'error' ? 'Error' : c.conclusion ?? 'Unknown' };
  }
}

/** A check's annotation with the run it came from (the read is per run; the page keeps the id). */
type RunNote = CheckAnnotation & { runId: number };

/** One annotation a check left, as the Checks tab lists it and a file section draws it on the line. */
function CheckNote({ note, fileLink }: { note: CheckAnnotation; fileLink?: (p: string) => string }) {
  const level = note.level === 'failure' ? 'danger' : note.level === 'warning' ? 'attention' : 'accent';
  const lines = note.startLine === note.endLine ? `L${note.startLine}` : `L${note.startLine}–L${note.endLine}`;
  const icon = note.level === 'failure' ? 'x-circle-fill' : note.level === 'warning' ? 'alert' : 'info';
  return (
    <div className={`check-note check-note-${level}`} role="note">
      <span className={`check-note-mark text-${level}`} aria-hidden="true"><Icon name={icon} /></span>
      <div className="check-note-body">
        <div className="check-note-where text-small">
          <span className="text-bold">{note.level === 'failure' ? 'Error' : note.level === 'warning' ? 'Warning' : 'Notice'}</span>
          {/* To the file's section of Files changed, where the note is drawn under its line; a
              #L hash would mean nothing there (the sections scroll to comment permalinks only). */}
          {fileLink ? <> in <Link to={fileLink(note.path)}>{note.pathLabel}:{lines}</Link></> : <> on {lines}</>}
          {note.title && <>: {note.title}</>}
        </div>
        {/* The CI's words, spelled by the server: text, never markup. */}
        <pre className="check-note-text">{note.message}</pre>
      </div>
    </div>
  );
}

function Checks({ status, onOpenPath, notes, fileLink }: { status: ReviewStatus | null; onOpenPath: (p: string) => void; notes: Async<RunNote[]>; fileLink: (p: string) => string }) {
  const [open, setOpen] = useState<string | null>(null);
  if (!status) {
    return <Loading what="Reading the checks…" />;
  }
  if (!status.checks) {
    return <Flash scheme="warning">The checks could not be read{status.checksError === 'sign-in' ? ' without signing in' : ''}.</Flash>;
  }
  if (status.checks.length === 0) {
    return <Blankslate icon="check-circle" heading="No checks ran on the version you are reviewing">A check run or a commit status posted on the reviewed commit shows here.</Blankslate>;
  }
  const checks = status.checks;
  const failed = checks.filter((c) => c.conclusion && !['success', 'neutral', 'skipped'].includes(c.conclusion)).length;
  const running = checks.filter((c) => c.status !== 'completed').length;
  const skipped = checks.filter((c) => c.conclusion && ['neutral', 'skipped'].includes(c.conclusion)).length;
  const successful = checks.length - failed - running - skipped;
  // Grouped as GitHub groups them: by the app that ran them, the suite within it.
  const groups = new Map<string, ReviewCheck[]>();
  for (const c of checks) {
    const key = c.app ?? (c.source === 'status' ? 'Commit statuses' : 'Checks');
    groups.set(key, [...(groups.get(key) ?? []), c]);
  }
  const key = (c: ReviewCheck, i: number) => `${c.source}-${c.id ?? c.name}-${i}`;
  return (
    <>
      {/* The list is the reviewed commit's; once the branch moved, the latest commit's checks are
          said in counts (Step 10), so a red new commit is not hidden behind a green reviewed one. */}
      {status.headMoved && status.liveChecks && (
        <Flash scheme={status.liveChecks.failed > 0 ? 'danger' : status.liveChecks.running > 0 ? 'warning' : 'default'}>
          The branch moved since you opened this review. On its latest commit ({status.liveChecks.head.slice(0, 7)}):{' '}
          {status.liveChecks.total === 0 ? 'no checks yet' : `${status.liveChecks.total} check${status.liveChecks.total === 1 ? '' : 's'}, ${status.liveChecks.failed} failing, ${status.liveChecks.running} in progress`}
          {status.liveChecks.truncated ? ', more on GitHub' : ''}. The list below is the reviewed commit's.
        </Flash>
      )}
      {status.headMoved && !status.liveChecks && (
        <Flash scheme="warning">The branch moved since you opened this review, and the latest commit's checks could not be read. The list below is the reviewed commit's.</Flash>
      )}
      {status.checksTruncated && (
        <p className="notice" role="note">GitHub lists more checks than were read here. A failing one may be among those not shown: see them all on GitHub.</p>
      )}
      <div className="checks-summary">
        {failed > 0 && <span className="text-danger"><Icon name="x-circle-fill" /> {failed} failing</span>}
        {running > 0 && <span className="text-attention"><Icon name="dot-fill" /> {running} in progress</span>}
        {skipped > 0 && <span className="text-muted"><Icon name="skip" /> {skipped} skipped</span>}
        {successful > 0 && <span className="text-success"><Icon name="check-circle-fill" /> {successful} successful</span>}
        {status.summary?.requiredChecks && status.summary.requiredChecks.length > 0 && (
          <span className="text-muted">{status.summary.requiredChecks.length} required</span>
        )}
      </div>
      {[...groups.entries()].map(([app, list]) => (
        <div key={app} className="box checks-group">
          <div className="box-header"><span className="text-bold">{app}</span><span className="text-muted">{list.length}</span></div>
          <ul className="row-list checks">
            {list.map((c, i) => {
              const { icon, tone, word } = checkLook(c);
              const id = key(c, i);
              const took = duration(c.startedAt, c.completedAt);
              const expandable = !!(c.title || c.summary || c.description || c.annotations);
              return (
                <li key={id} className={`check ${tone}`}>
                  <div className="check-line">
                    <span className="check-mark" aria-hidden="true"><Icon name={icon} /></span>
                    <span className="check-name">
                      {c.name}
                      {c.required && <span className="row-badge check-required" title="Branch protection requires this check">Required</span>}
                    </span>
                    <span className="text-muted text-small">{word}{took ? ` · ${took}` : ''}</span>
                    {c.annotations ? <span className="text-muted text-small" title="Annotations the run left on the code">{c.annotations} annotation{c.annotations === 1 ? '' : 's'}</span> : null}
                    {expandable && (
                      <button type="button" className="btn btn-small" aria-expanded={open === id} onClick={() => setOpen(open === id ? null : id)}>
                        {open === id ? 'Hide' : 'Show'} output
                      </button>
                    )}
                    {/* https only, checked by the server; opened, never fetched or framed here. */}
                    {safeHref(c.url) && <a href={safeHref(c.url)!} target="_blank" rel="noopener noreferrer" className="btn btn-small">Details</a>}
                  </div>
                  {open === id && (
                    <div className="check-output">
                      {c.title && <div className="text-bold">{c.title}</div>}
                      {c.description && <div>{c.description}</div>}
                      {c.summary && <FragmentHost html={c.summary} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />}
                      {/* The run's own annotations, from the read shared with the file sections
                          (every run's are read together, each note keeping its run) — the server
                          reads at most a hundred per run and says nothing of more. */}
                      {c.annotations ? (
                        <div className="check-annotations">
                          {notes.state === 'loading' && <Loading what="Reading the annotations…" />}
                          {notes.state === 'error' && <p className="text-danger">{notes.error.message}</p>}
                          {notes.state === 'ok' && !notes.value.some((a) => a.runId === c.id) && <p className="text-muted">GitHub listed no annotations for this run.</p>}
                          {notes.state === 'ok' && notes.value.filter((a) => a.runId === c.id).map((a, j) => <CheckNote key={`${a.path}:${a.startLine}:${j}`} note={a} fileLink={fileLink} />)}
                        </div>
                      ) : null}
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </>
  );
}

interface FileSectionProps {
  writer: Writer | null;
  snap: ReviewSnapshot;
  owner: string;
  repo: string;
  file: ReviewFile;
  /** The file the URL names: opened first and scrolled to. */
  active: boolean;
  threads: ReviewThread[];
  threadsState: Async<ReviewThreads>;
  initialView: PaneView;
  initialLayout: Layout;
  initialFull: boolean;
  onLayer: (path: string) => void;
  onOpenPath: (path: string) => void;
  signInUrl: string | null;
  viewed: boolean;
  onViewed: (on: boolean) => void;
  /** The URL's fragment, for scrolling to a comment's permalink once its thread is placed. */
  hash: string;
  /** Every annotation the checks left on this pull request; the section draws those on its own lines. */
  notes: CheckAnnotation[];
}

/**
 * One file of the stacked Files changed: GitHub's sticky header — chevron, the object's name and
 * where it lives, +N −M, Viewed, the view switches — over the drawing, which is asked for only when
 * the section is near the viewport (an 85-file pull request must not ask for 85 drawings at once)
 * and never again once read. A viewed file folds, as on GitHub; the chevron reopens it.
 */
function FileSection({ writer, snap, owner, repo, file, active, threads, threadsState, initialView, initialLayout, initialFull, onLayer, onOpenPath, signInUrl, viewed, onViewed, hash, notes }: FileSectionProps) {
  const [view, setView] = useState<PaneView>(initialView);
  const [layout, setLayout] = useState<Layout>(initialLayout);
  const [full, setFull] = useState(initialFull);
  const [open, setOpen] = useState(!viewed || active);
  const [near, setNear] = useState(active);
  const host = useRef<HTMLElement>(null);
  const path = file.path;

  // A viewed mark folds the file; unmarking opens it again (GitHub's rule). The chevron overrides either.
  useEffect(() => setOpen(!viewed || active), [viewed, active]);

  // Load when near the viewport. Without IntersectionObserver (the tests' jsdom) everything loads at once.
  useEffect(() => {
    if (near || !open) {
      return;
    }
    const el = host.current;
    if (!el || typeof IntersectionObserver === 'undefined') {
      setNear(true);
      return;
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        setNear(true);
        observer.disconnect();
      }
    }, { rootMargin: '600px 0px' });
    observer.observe(el);
    return () => observer.disconnect();
  }, [near, open]);

  useEffect(() => {
    // Optional: the tests' jsdom draws no layout and has no scrollIntoView.
    if (active && host.current && typeof host.current.scrollIntoView === 'function') {
      host.current.scrollIntoView({ block: 'start' });
    }
  }, [active]);

  // Read once it is near AND open, and kept from then on: folding (ticking Viewed) and opening
  // again must not ask for the drawing again — with `open` a dependency and the loader answering
  // never() while folded, every toggle re-read the most expensive thing on the page (review,
  // 2026-10-24).
  const [wanted, setWanted] = useState(false);
  useEffect(() => {
    if (near && open) {
      setWanted(true);
    }
  }, [near, open]);
  // A new snapshot (a push, "load the latest") while the file is folded: not wanted until it is
  // opened again, or every file ever opened re-read its drawing on every push, folded or not —
  // 85 reads per push on a fully viewed review (review of the review, 2026-10-24).
  useEffect(() => {
    if (!open) {
      setWanted(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [snap.token]);
  const result = useAsync(
    (s) => (wanted ? api.review.fileView(owner, repo, snap, path, view, layout, file.previousPath ?? undefined, s, full) : never()),
    [snap.token, path, view, layout, full, wanted],
  );

  const segments = file.label.split('/');
  const name = file.object ?? segments[segments.length - 1] ?? file.label;
  return (
    <section ref={host} className={`file-review${open ? '' : ' is-folded'}${viewed ? ' is-viewed' : ''}`} data-file={path} id={`diff-${encodeURIComponent(path)}`}>
      <ChangeSectionHead open={open} onToggle={() => setOpen(!open)} status={file.change} name={name} label={file.label}
        renamedFrom={file.change === 'renamed' ? file.previousLabel : null} additions={file.additions} deletions={file.deletions}
        view={view} onView={setView} layout={layout} onLayout={setLayout} full={full} onFull={setFull}
        extraTools={writer && (
          <label className="review-viewed-label">
            <input type="checkbox" checked={viewed} onChange={(e) => onViewed(e.target.checked)} /> Viewed
          </label>
        )} />
      {open && (
        <div className="file-review-body">
          <FileReview writer={writer} path={path} file={file} result={result} threads={threads} threadsState={threadsState} view={view} onView={setView}
            onLayer={onLayer} onOpenPath={onOpenPath} signInUrl={signInUrl} onUnfold={() => setFull(true)} hash={hash} notes={notes} />
        </div>
      )}
    </section>
  );
}

interface FileReviewProps {
  writer: Writer | null;
  path: string;
  file: ReviewFile | null;
  result: Async<ReviewFileView>;
  threads: ReviewThread[];
  threadsState: Async<ReviewThreads>;
  view: PaneView;
  onView: (v: PaneView) => void;
  onLayer: (path: string) => void;
  onOpenPath: (path: string) => void;
  signInUrl: string | null;
  /** A fold row was clicked: the section asks for the whole file. */
  onUnfold: () => void;
  /** The URL's fragment: a comment's permalink to scroll to once placed. */
  hash: string;
  /** The checks' annotations on this pull request: those on this file's lines are drawn under them. */
  notes?: CheckAnnotation[];
}

/** A gap's place, in words: a whole file, or a stretch of lines on one side. */
export function gapWords(gap: CoverageGap): string {
  const where = gap.start === 0 ? 'the whole file' : gap.start === gap.end ? `line ${gap.start}` : `lines ${gap.start}–${gap.end}`;
  const why = ({
    'not-shown': 'changed, but not shown as changed',
    'no-evidence': 'this kind of drawing cannot yet say what it shows',
    'not-drawn': 'not drawn',
    'too-large': 'too large to check',
  } as Record<string, string>)[gap.reason] ?? gap.reason;
  return `${gap.pathLabel}, ${gap.side === 'old' ? 'as it was' : 'as it is'}, ${where}: ${why}`;
}

/**
 * One changed file: its drawing, what the page cannot vouch for about that drawing, and its review
 * threads. On Raw, a thread sits under its line; anything that cannot be placed — outdated, on the
 * file, or on a line this drawing does not show — is listed above, never dropped.
 */
function FileReview({ writer, path, file, result, threads, threadsState, view, onView, onLayer, onOpenPath, signInUrl, onUnfold, hash, notes = [] }: FileReviewProps) {
  const [placed, setPlaced] = useState<string[]>([]);
  // A new comment being written. On Raw, on a diff line (the diff's own numbers are file lines).
  // On a drawing, on an element the server will resolve (a member row, a network), or on a drawn
  // source line of the file its block names — a code body under a table is another file. An
  // element drawn on both sides (a modified row) has the other side's anchor as `alt`, so the
  // comment can be moved to the row as it was (Step 3b).
  type Draft =
    | { kind: 'line'; side: 'LEFT' | 'RIGHT'; line: number; file: string; startLine?: number; text?: string }
    | { kind: 'anchor'; dom: string; alt?: string };
  const [drafting, setDrafting] = useState<Draft | null>(null);
  const [fileComment, setFileComment] = useState(false);
  // Resolved threads are shown by default, as this page always showed them; the toggle hides
  // them (GitHub folds them). Hidden means not placed: the row has no slot for them.
  const [showResolved, setShowResolved] = useState(true);
  useEffect(() => setDrafting(null), [path, view]);
  // A shift-click on a second line of the same file and side makes the draft a range, as a
  // shift-click does on GitHub: GitHub takes a range inside one hunk on one side, which the
  // server checks before posting (HunkLines.Accepts). The line's text rides along for a
  // suggested change to start from; a range has none, since only the last line's text is known.
  function pickLine(side: 'LEFT' | 'RIGHT', line: number, file: string, shift: boolean, text?: string) {
    setDrafting((d) => {
      if (shift && d && d.kind === 'line' && d.side === side && d.file === file && d.line !== line) {
        const [from, to] = d.line < line ? [d.startLine ?? d.line, line] : [line, d.line];
        return { kind: 'line', side, file, line: to, startLine: from };
      }
      return { kind: 'line', side, line, file, text };
    });
  }
  const canLine = writer !== null && view === 'raw' && file?.inline === true;
  const canDraw = writer !== null && view === 'rendered';
  const placements = result.state === 'ok' ? result.value.placements : [];
  const shown = showResolved ? threads : threads.filter((t) => !t.resolved);
  const layers = result.state === 'ok' ? result.value.view.layers : [];
  const annotations: Annotation[] = useMemo(() => {
    const byId = new Map(shown.map((t) => [t.id, t]));
    // A check's annotation sits under its first line, new side: on Raw when it is this file's,
    // on a drawing when it is on any file the drawing shows (a code body under a table is its
    // own file). One that finds no drawn line is simply not drawn: the Checks tab lists them all.
    const checkNotes: Annotation[] = notes.flatMap((a, i) => {
      if (view === 'raw' ? a.path !== path : !layers.includes(a.path)) {
        return [];
      }
      const selector = view === 'raw' ? `[data-line="${a.startLine}"]` : `[data-src-path="${attr(a.path)}"] [data-file-line="${a.startLine}"]`;
      return [{ key: `note:${i}`, selector, content: <CheckNote note={a} /> }];
    });
    const placedThreads: Annotation[] = view === 'raw'
      ? shown.flatMap((t) => {
          const selector = threadSelector(t);
          return selector ? [{ key: t.id, selector, content: <Thread thread={t} writer={writer} onOpenPath={onOpenPath} /> }] : [];
        })
      : placements.flatMap((p) => {
          const thread = byId.get(p.threadId);
          const selector = placementSelector(p);
          return thread && selector
            ? [{ key: thread.id, selector, content: <Thread thread={thread} writer={writer} onOpenPath={onOpenPath} moved={p.outcome === 'moved'} /> }]
            : [];
        });
    if (drafting && writer) {
      const where = drafting.kind === 'anchor' ? drafting.dom : `${drafting.file}:${drafting.side}:${drafting.startLine ?? ''}-${drafting.line}`;
      const selector = drafting.kind === 'anchor'
        ? `[data-anchors~="${drafting.dom}"]`
        : view === 'raw'
          ? (drafting.side === 'LEFT' ? `[data-old-line="${drafting.line}"]` : `[data-line="${drafting.line}"]`)
          : `[data-src-path="${attr(drafting.file)}"] [${drafting.side === 'LEFT' ? 'data-old-file-line' : 'data-file-line'}="${drafting.line}"]`;
      const what = drafting.kind === 'anchor'
        ? `New comment on this ${drafting.dom.startsWith('n-') ? 'network' : 'member'}${drafting.dom.includes('-old-') ? ', as it was' : ''}`
        : drafting.startLine
          ? `New comment on ${drafting.side === 'LEFT' ? 'old' : 'new'} lines ${drafting.startLine}–${drafting.line}`
          : `New comment on ${drafting.side === 'LEFT' ? 'old' : 'new'} line ${drafting.line}`;
      placedThreads.push({
        key: `draft:${where}`,
        selector,
        content: (
          <div className="thread thread-draft">
            <header className="thread-head muted">
              {what}
              {drafting.kind === 'anchor' && drafting.alt && (
                <button type="button" className="link-button thread-side-switch" onClick={() => {
                  // The other side is another box (its own slot, its own operation): the words
                  // typed so far go with the person, as long as nothing was sent yet.
                  carryDraft(`${writer.slots}:draft:${path}:${where}`, `${writer.slots}:draft:${path}:${drafting.alt}`);
                  setDrafting({ kind: 'anchor', dom: drafting.alt!, alt: drafting.dom });
                }}>
                  comment on it as it {drafting.alt.includes('-old-') ? 'was' : 'is'} instead
                </button>
              )}
            </header>
            <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:draft:${path}:${where}`}
              path={drafting.kind === 'anchor' ? '/anchor-comments' : '/line-comments'}
              payload={drafting.kind === 'anchor'
                ? { path, from: file?.previousPath ?? undefined, dom: drafting.dom }
                : { path: drafting.file, line: drafting.line, side: drafting.side, ...(drafting.startLine ? { startLine: drafting.startLine } : {}) }}
              submitLabel="Comment" alternative={writer.review} onCancel={() => setDrafting(null)}
              // A suggestion is a change to the new file's lines: offered on a new-side line whose text is known.
              suggestFrom={drafting.kind === 'line' && drafting.side === 'RIGHT' && !drafting.startLine && drafting.text !== undefined ? drafting.text : null}
              onDone={() => { setDrafting(null); writer.onChanged(); }} onStale={writer.onStale} />
          </div>
        ),
      });
    }
    return [...checkNotes, ...placedThreads];
  }, [shown, view, onOpenPath, drafting, writer, path, placements, file, notes, layers]);
  const unplaced = shown.filter((t) => !placed.includes(t.id));
  const resolvedCount = threads.filter((t) => t.resolved).length;

  // A permalink to a comment: once its thread is placed, scroll to it.
  useEffect(() => {
    const target = hash && placed.length > 0 ? document.getElementById(hash) : null;
    if (target && typeof target.scrollIntoView === 'function') {
      target.scrollIntoView({ block: 'center' });
    }
  }, [hash, placed]);

  if (result.state === 'loading') {
    return <Loading what="Drawing the file…" />;
  }
  if (result.state === 'error') {
    return <ErrorPanel error={result.error} signInUrl={signInUrl} />;
  }
  const v = result.value.view;
  return (
    <div className="file-review-inner">
      {v.layers.length > 1 && (
        <div className="layers" aria-label="Files of this object">
          {v.layers.map((layer, i) => (
            <button key={layer} type="button" className={`chip${layer === v.path ? ' is-on' : ''}`} onClick={() => onLayer(layer)} title={v.layerLabels[i] ?? layer}>
              {(v.layerLabels[i] ?? layer).slice((v.layerLabels[i] ?? layer).lastIndexOf('/') + 1)}
            </button>
          ))}
        </div>
      )}
      {/* C4: a drawing is "complete" only when every changed line of the object is one it names as
          changed. Anything less is said, with each stretch it cannot vouch for, every time — an
          approval of the drawing is an approval of what it shows. */}
      {result.value.coverage === 'complete' && (
        <p className="muted review-complete" role="note">Every change in this object is shown in the drawing.</p>
      )}
      {result.value.coverage === 'partial' && (
        <div className="notice review-partial" role="note">
          The drawing may not show every change in this file. Check <button type="button" className="link-button" onClick={() => onView('raw')}>Raw</button> before approving.
          {result.value.gaps.length > 0 && (
            <ul className="review-gaps">
              {result.value.gaps.map((g) => (
                <li key={`${g.path}:${g.side}:${g.start}-${g.end}`}>
                  <button type="button" className="link-button" onClick={() => (g.path === path ? onView('raw') : onLayer(g.path))}>{gapWords(g)}</button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {file?.change === 'renamed' && <p className="notice" role="note">Renamed from {file.previousLabel}. The drawing does not yet compare against the old path.</p>}
      {file && !file.inline && <p className="notice" role="note">{unavailable[file.inlineUnavailable ?? ''] ?? ''}</p>}
      {v.notices.map((note) => (
        <p key={note} className="notice" role="note">{note}</p>
      ))}
      {threadsState.state === 'error' && <p className="muted">Review threads are not shown: {threadsState.error.message}</p>}
      {resolvedCount > 0 && (
        <p className="text-small file-resolved-toggle">
          <label><input type="checkbox" checked={showResolved} onChange={(e) => setShowResolved(e.target.checked)} /> Show {resolvedCount} resolved thread{resolvedCount === 1 ? '' : 's'}</label>
        </p>
      )}
      {writer && (
        <div className="file-comment">
          {canLine && <span className="muted">Click a line number to comment on that line; shift-click another for a range. </span>}
          {canDraw && <span className="muted">Click a member's name, a network's heading or a line number to comment on it. </span>}
          {writer && view === 'raw' && file && !file.inline && <span className="muted">Comments on this file go on the whole file. </span>}
          {fileComment ? (
            <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:file:${path}`} path="/line-comments" payload={{ path }}
              submitLabel="Comment on the file" alternative={writer.review} onCancel={() => setFileComment(false)} onDone={() => { setFileComment(false); writer.onChanged(); }} onStale={writer.onStale} />
          ) : (
            <button type="button" className="link-button" onClick={() => setFileComment(true)}>Comment on the whole file</button>
          )}
        </div>
      )}
      {unplaced.length > 0 && (
        <section className="unplaced" aria-label="Comments not on a line shown here">
          <h3 className="panel-title">
            {/* Whatever the server could not place on something drawn (an outdated line, a file
                comment, an element that is gone or ambiguous) is listed here, never guessed. */}
            {view === 'raw' ? 'Comments not on a line shown here' : 'Comments not on anything drawn here'}
          </h3>
          {unplaced.map((t) => <Thread key={t.id} thread={t} writer={writer} onOpenPath={onOpenPath} />)}
        </section>
      )}
      <div className={canDraw ? 'can-comment' : undefined}>
      <FragmentHost html={v.html} path={v.path} onOpenPath={onOpenPath} line={null} onLine={() => {}} annotations={annotations} onPlaced={setPlaced} onUnfold={onUnfold}
        onRowPick={canLine ? (row) => {
          // A context row has both numbers: it is commented on the new side, as GitHub does.
          const side = row.line !== null ? 'RIGHT' : 'LEFT';
          const line = row.line ?? row.oldLine;
          if (line !== null) {
            pickLine(side, line, path, row.shift, row.text);
          }
        } : canDraw ? (row) => {
          // A drawn source line names its file line on each side it exists on; the new side wins
          // for an unchanged line, as on Raw. A row whose block names no file cannot be commented.
          const side = row.fileLine !== null ? 'RIGHT' : 'LEFT';
          const line = row.fileLine ?? row.oldFileLine;
          if (line !== null && row.srcPath) {
            pickLine(side, line, row.srcPath, row.shift, row.text);
          }
        } : undefined}
        onAnchorPick={canDraw ? (ids) => {
          // An element standing for both sides is commented on as it is now, with the older side
          // offered in the draft's header; one only on the older side (removed) as it was.
          const dom = ids.find((id) => id.includes('-new-')) ?? ids[0];
          if (dom) {
            setDrafting({ kind: 'anchor', dom, alt: ids.find((id) => id !== dom) });
          }
        } : undefined} />
      </div>
    </div>
  );
}

function Thread({ thread, writer, onOpenPath, moved = false }: { thread: ReviewThread; writer: Writer | null; onOpenPath: (p: string) => void; moved?: boolean }) {
  const [replying, setReplying] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const { owner = '', repo = '' } = useParams();
  // An outdated thread's line is gone from the diff: GitHub keeps the hunk the comment was written
  // on, and shows it (Step 3b) with the way to the file at that commit. Spelled lines from the
  // server, drawn as text.
  const first = thread.comments[0];
  const hunk = thread.outdated && first?.diffHunk ? first.diffHunk : null;
  const at = thread.outdated && first?.originalCommit ? first.originalCommit : null;
  const where = thread.subject === 'FILE' ? 'on the file'
    : thread.line !== null ? `${thread.side === 'LEFT' ? 'old' : 'new'} line ${thread.startLine && thread.startLine !== thread.line ? `${thread.startLine}–` : ''}${thread.line}`
    : `was on line ${thread.originalLine ?? '?'}`;
  return (
    <article className={`thread${thread.resolved ? ' is-resolved' : ''}`} data-thread={thread.id}>
      <header className="thread-head muted">
        {thread.resolved && <span className="row-badge">resolved</span>}
        {thread.outdated && <span className="row-badge">outdated</span>}
        {/* Found by its content after the element was renumbered: said, so nobody reads it as written here. */}
        {moved && <span className="row-badge" title="Written on this element under another number; found again by its content">moved here</span>}
        {where}
        {writer && (thread.resolved ? thread.canUnresolve : thread.canResolve) && (
          <span className="thread-tools">
            <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/threads/${encodeURIComponent(thread.id)}/resolve`}
              payload={{ resolved: !thread.resolved }} label={thread.resolved ? 'Unresolve' : 'Resolve'} onDone={writer.onChanged} />
          </span>
        )}
      </header>
      {hunk && (
        <div className="thread-hunk">
          <pre className="thread-hunk-code">{hunk}</pre>
          {at && owner && (
            <Link className="text-small" to={`${blobUrl(owner, repo, at, thread.path)}${thread.originalLine ? `#L${thread.originalLine}` : ''}`}>
              View the file at {at.slice(0, 7)}, where this was written
            </Link>
          )}
        </div>
      )}
      {thread.comments.map((c) => {
        const body = writer && editing === c.id ? (
          <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:edit:${c.id}`} path={`/comments/${encodeURIComponent(c.id)}`} method="PATCH"
            initial={c.markdown ?? ''} submitLabel="Save" onCancel={() => setEditing(null)} onDone={() => { setEditing(null); writer.onChanged(); }} onStale={writer.onStale} />
        ) : (
          <>
            <FragmentHost html={c.html} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />
            {c.suggestion !== undefined && c.suggestion !== null && <Suggestion text={c.suggestion} commentId={c.id} thread={thread} writer={writer} />}
          </>
        );
        return (
          <div key={c.id} className={`thread-comment${c.pending ? ' is-pending' : ''}${c.minimized ? ' is-minimized' : ''}`} id={anchorOf(c.url) ?? undefined}>
            <span className="thread-meta">
              <Avatar login={c.author} url={c.avatar} size={16} />
              <strong>{c.author}</strong> <RelativeTime when={c.when} />
              {c.editedAt && <span className="text-muted" title={`Edited ${new Date(c.editedAt).toLocaleString()}`}> · edited</span>}
              <RoleBadge association={c.association} />
              {c.pending && <span className="row-badge" title="A draft of your review: nobody else sees it until you submit">Pending</span>}
              {writer && editing !== c.id && (
                <span className="comment-tools">
                  {c.canEdit && <button type="button" className="link-button" onClick={() => setEditing(c.id)}>Edit</button>}
                  {!c.pending && <HideTools writer={writer} id={c.id} minimized={c.minimized === true} />}
                  {c.canDelete && <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/comments/${encodeURIComponent(c.id)}/delete`} label="Delete" confirm="Delete this comment on GitHub?" onDone={writer.onChanged} />}
                </span>
              )}
            </span>
            {c.minimized ? <Minimized reason={c.minimizedReason}>{body}</Minimized> : body}
            <Reactions reactions={c.reactions} onToggle={writer ? (content, on) => writer.react(c.id, content, on) : undefined} />
          </div>
        );
      })}
      {thread.truncated && <p className="muted">More replies on GitHub.</p>}
      {writer && thread.canReply && (replying ? (
        <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:reply:${thread.id}`} path={`/threads/${encodeURIComponent(thread.id)}/replies`}
          submitLabel="Reply" placeholder="Reply" onCancel={() => setReplying(false)} onDone={() => { setReplying(false); writer.onChanged(); }} onStale={writer.onStale} />
      ) : (
        <button type="button" className="link-button thread-reply" onClick={() => setReplying(true)}>Reply</button>
      ))}
    </article>
  );
}
