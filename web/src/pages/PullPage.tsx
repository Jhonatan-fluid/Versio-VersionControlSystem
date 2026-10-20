import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { CoverageGap, Layout, Me, PaneView, PendingReviewView, ReviewCommits, ReviewFile, ReviewFileView, ReviewSnapshot, ReviewStatus, ReviewThread, ReviewThreads, ReviewTimeline, ThreadPlacement } from '../api';
import { ApiError, api } from '../api';
import type { Annotation } from '../components/FragmentHost';
import { FragmentHost } from '../components/FragmentHost';
import { MergeBox } from '../components/MergeBox';
import { ErrorPanel } from '../components/Pane';
import { AssigneesEditor, BaseEditor, LabelsEditor, StateActions, TitleEditor } from '../components/PullMeta';
import { ReviewBar, ReviewersEditor } from '../components/ReviewBar';
import { ActionButton, CommentBox, newOpKey } from '../components/ReviewWrite';
import { Workbench } from '../components/Workbench';
import { blobUrl, compareUrl, pullUrl, pullsUrl, treeUrl } from '../routes';
import type { Async } from '../useAsync';
import { useAsync } from '../useAsync';

type Tab = 'conversation' | 'commits' | 'checks' | 'files';

const tabs: { id: Tab; label: string }[] = [
  { id: 'conversation', label: 'Conversation' },
  { id: 'commits', label: 'Commits' },
  { id: 'checks', label: 'Checks' },
  { id: 'files', label: 'Files changed' },
];

const mark: Record<ReviewFile['change'], string> = { added: 'A', modified: 'M', deleted: 'D', renamed: 'R' };

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
function usePolled<T>(load: ((signal: AbortSignal) => Promise<T>) | null, key: string, refresh = 0): T | null {
  const [value, setValue] = useState<T | null>(null);
  const shown = useRef(key);
  useEffect(() => {
    if (shown.current !== key) {
      shown.current = key;
      setValue(null);
    }
    if (!load) {
      return;
    }
    const controller = new AbortController();
    const poll = () => {
      if (document.visibilityState !== 'hidden') {
        load(controller.signal).then((v) => !controller.signal.aborted && setValue(v), () => {});
      }
    };
    poll();
    const timer = window.setInterval(poll, POLL_MS);
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
  /**
   * "Add to review", when a comment may go into the viewer's pending review: there is none yet (the
   * first creates it on the snapshot's head), or there is one on that head. One on an older commit
   * takes no more comments here (C2), so the button is not drawn.
   */
  review: { path: string; label: string } | undefined;
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
  const { owner = '', repo = '', number = '' } = useParams();
  const n = Number(number);
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const tab: Tab = (tabs.find((t) => t.id === search.get('tab'))?.id ?? 'conversation');
  const path = search.get('path');
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
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
  const status = usePolled(snap ? (s) => api.review.status(owner, repo, snap, s) : null, token, changed);
  const live = status?.summary ?? null;
  // The merge box's state: read only with the merge tier on, and again after every write.
  const mergeState = useAsync((s) => (snap?.canMerge && tab === 'conversation' ? api.review.mergeState(owner, repo, snap, s) : never()), [token, tab, changed]);
  const [editing, setEditing] = useState<'title' | 'base' | null>(null);
  const files = useAsync((s) => (snap ? api.review.files(owner, repo, snap, s) : never()), [token]);
  const threads = useAsync((s) => (snap ? api.review.threads(owner, repo, snap, s) : never()), [token, changed]);
  const timeline = useAsync((s) => (snap && tab === 'conversation' ? api.review.timeline(owner, repo, snap, s) : never()), [token, tab, changed]);
  const commits = useAsync((s) => (snap && tab === 'commits' ? api.review.commits(owner, repo, snap, s) : never()), [token, tab]);
  // A renamed file is drawn against its old path, which only the files list (from GitHub) knows.
  const from = files.state === 'ok' ? files.value.files.find((f) => f.path === path)?.previousPath ?? undefined : undefined;
  const fileView = useAsync(
    (s) => (snap && tab === 'files' && path && files.state !== 'loading' ? api.review.fileView(owner, repo, snap, path, view, layout, from, s) : never()),
    [token, tab, path, view, layout, from, files.state],
  );

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
  const reload = () => {
    setStaleWrite(false);
    setLoads((x) => x + 1);
  };
  const writer: Writer | null = snap?.writable
    ? {
        owner, repo, snap, slots: `versio:draft:${owner}/${repo}#${n}`, onChanged: () => setChanged((x) => x + 1), onStale: () => setStaleWrite(true),
        review: reviewAlternative(pending.state === 'ok' ? pending.value : null),
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
  const expired = [files, threads, timeline, commits, fileView].some((a) => a.state === 'error' && needsReload(a.error));

  function go(nextTab: Tab, nextPath?: string) {
    navigate(pullUrl(owner, repo, n, nextTab === 'conversation' ? undefined : nextTab, nextPath, search));
  }

  function setParam(key: string, value: string, fallback: string) {
    const next = new URLSearchParams(search);
    if (value === fallback) {
      next.delete(key);
    } else {
      next.set(key, value);
    }
    setSearch(next);
  }

  const fileThreads = (p: string) => (threads.state === 'ok' ? threads.value.threads.filter((t) => t.path === p) : []);

  return (
    <Workbench
      me={me}
      owner={owner}
      repo={repo}
      refName={defaultBranch}
      defaultBranch={defaultBranch}
      branches={branches.state === 'ok' ? branches.value.branches : []}
      branchesTruncated={branches.state === 'ok' && branches.value.truncated}
      onRef={(r) => navigate(treeUrl(owner, repo, r))}
      rail="pulls"
      panelTitle={snap ? `Merge request #${snap.number}` : 'Merge request'}
      panel={
        <>
          <nav className="review-tabs" aria-label="Review">
            {tabs.map((t) => (
              <button key={t.id} type="button" className={`tree-row${t.id === tab ? ' is-selected' : ''}`} aria-current={t.id === tab ? 'page' : undefined} onClick={() => go(t.id)}>
                {t.label}
                {t.id === 'files' && files.state === 'ok' && <span className="muted review-count">{files.value.total}</span>}
              </button>
            ))}
          </nav>
          {tab === 'files' && files.state === 'ok' && (
            <>
              {files.value.truncated && <p className="muted pad">The first {files.value.files.length} of {files.value.total} files are listed.</p>}
              <ul className="changes">
                {files.value.files.length === 0 && <li className="muted pad">This pull request changes no files.</li>}
                {files.value.files.map((f) => {
                  const count = fileThreads(f.path).length;
                  return (
                    <li key={f.path}>
                      <button type="button" className={`tree-row${f.path === path ? ' is-selected' : ''}`} onClick={() => go('files', f.path)} title={f.label}>
                        <span className={`change change-${f.change}`}>{mark[f.change]}</span>
                        <span className="tree-name">{f.object ?? f.label.slice(f.label.lastIndexOf('/') + 1)}</span>
                        {!f.inline && <span className="muted review-flag" title={unavailable[f.inlineUnavailable ?? ''] ?? ''}>file only</span>}
                        {count > 0 && <span className="review-count" title={`${count} review thread${count === 1 ? '' : 's'}`}>{count}</span>}
                      </button>
                      {writer && (
                        <input type="checkbox" className="review-viewed" checked={viewed.has(f.path)} onChange={(e) => markViewed(f.path, e.target.checked)}
                          aria-label={`Viewed ${f.label}`} title="Viewed" />
                      )}
                    </li>
                  );
                })}
              </ul>
            </>
          )}
          {tab !== 'files' && <Summary status={status} writer={writer} />}
        </>
      }
    >
      {snapshot.state === 'loading' ? (
        <section className="pane pane-empty" aria-busy="true">Loading...</section>
      ) : snapshot.state === 'error' ? (
        <ErrorPanel error={snapshot.error} signInUrl={signIn} />
      ) : (
        <section className="pane">
          <header className="pane-head">
            <div className="pane-title">
              <span className="pane-label">Merge request</span>
              {writer && live && editing === 'title' ? (
                <TitleEditor owner={owner} repo={repo} snap={writer.snap} summary={live} onChanged={writer.onChanged} onStale={writer.onStale} onDone={() => setEditing(null)} />
              ) : (
                <h1>
                  {live?.title ?? snap!.title}
                  {writer && live?.viewerCanUpdate && <button type="button" className="link-button title-edit" onClick={() => setEditing('title')}>Edit</button>}
                </h1>
              )}
              {writer && live && editing === 'base' ? (
                <BaseEditor owner={owner} repo={repo} snap={writer.snap} branches={branches.state === 'ok' ? branches.value.branches.map((b) => b.name) : []}
                  current={snap!.baseRef} head={snap!.headLabel} onChanged={writer.onChanged} onStale={writer.onStale} onDone={() => setEditing(null)} />
              ) : (
                <>
                  <span className="pane-folder">#{snap!.number} · {snap!.headLabel} {'→'} {snap!.baseLabel}</span>
                  {/* Outside the folder line, which cuts long branch names with an ellipsis and took the link with them. */}
                  {writer && live?.viewerCanUpdate && live.state === 'OPEN' && (
                    <button type="button" className="link-button base-edit" onClick={() => setEditing('base')}>change base</button>
                  )}
                </>
              )}
            </div>
            <div className="pane-tools">
              <Link className="button-quiet" to={pullsUrl(owner, repo, n)}>All merge requests</Link>
            </div>
          </header>
          {(status?.stale || staleWrite || expired) && (
            <p className="notice review-stale" role="status">
              {expired
                ? 'This review was loaded too long ago to go on with. '
                : `This pull request changed after you opened it${status?.headMoved ? ' (new commits)' : ''}${status?.baseMoved ? ` (${snap!.baseLabel} moved)` : ''}. What you see is still the version you opened; nothing is mixed, and your drafts are kept. `}
              <button type="button" className="button" onClick={reload}>Load the latest</button>
            </p>
          )}
          {status?.summaryError === 'sign-in' && signIn && (
            <p className="notice" role="note">
              Without signing in, this page cannot tell whether the pull request has changed since you opened it, and review threads are not shown. <a href={signIn}>Sign in</a>
            </p>
          )}
          <p className="muted review-revision" title={`base ${snap!.baseSha}\nmerge-base ${snap!.mergeBaseSha}\nhead ${snap!.headSha}`}>
            Reviewing {snap!.headSha.slice(0, 8)}, compared from where it branched ({snap!.mergeBaseSha.slice(0, 8)}).
            {/* Two-dot from the reviewed commit: after a force-push it also shows what the rewrite changed, which is the point. */}
            {last && last !== snap!.headSha && (
              <> <Link to={compareUrl(owner, repo, last, snap!.headSha)}>Changes since your last review ({last.slice(0, 8)})</Link></>
            )}
          </p>
          {writer && (
            <ReviewBar owner={owner} repo={repo} snap={snap!} slots={writer.slots} status={status} pending={pending}
              files={files.state === 'ok' ? files.value.files : []} opened={opened} viewed={viewed}
              onChanged={writer.onChanged} onStale={writer.onStale} onOpenPath={(p) => go('files', p)} />
          )}
          {tab === 'conversation' && <Conversation snap={snap!} live={live} timeline={timeline} writer={writer} onOpenPath={(p) => navigate(blobUrl(owner, repo, snap!.headSha, p))} />}
          {tab === 'conversation' && writer && snap!.canMerge && (
            <MergeBox owner={owner} repo={repo} snap={writer.snap} state={mergeState} onChanged={writer.onChanged} onStale={writer.onStale} />
          )}
          {tab === 'commits' && <Commits commits={commits} />}
          {tab === 'checks' && <Checks status={status} />}
          {tab === 'files' && (
            path ? (
              <FileReview
                writer={writer}
                path={path}
                file={files.state === 'ok' ? files.value.files.find((f) => f.path === path) ?? null : null}
                result={fileView}
                threads={fileThreads(path)}
                threadsState={threads}
                view={view}
                layout={layout}
                onView={(v) => setParam('view', v, 'rendered')}
                onLayout={(l) => setParam('layout', l, 'unified')}
                onLayer={(layer) => go('files', layer)}
                onOpenPath={(p) => navigate(blobUrl(owner, repo, snap!.headSha, p))}
                signInUrl={signIn}
                viewed={viewed.has(path)}
                onViewed={(on) => markViewed(path, on)}
              />
            ) : (
              <p className="muted">Pick a changed file.</p>
            )
          )}
        </section>
      )}
    </Workbench>
  );
}

/** The live state, beside every tab but the file list: what the pull request is, now. */
function Summary({ status, writer }: { status: ReviewStatus | null; writer: Writer | null }) {
  if (!status) {
    return <p className="muted pad">Reading the pull request's state...</p>;
  }
  const s = status.summary;
  const checks = status.checks ?? [];
  const failed = checks.filter((c) => c.conclusion && !['success', 'neutral', 'skipped'].includes(c.conclusion)).length;
  const running = checks.filter((c) => c.status !== 'completed').length;
  return (
    <div className="pad review-summary">
      {s ? (
        <>
          <p><span className={`state state-${s.merged ? 'merged' : s.state.toLowerCase()}`}>{s.merged ? 'Merged' : s.draft ? 'Draft' : s.state === 'OPEN' ? 'Open' : 'Closed'}</span></p>
          {writer && s.viewerCanUpdate && <StateActions owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />}
          {s.reviewDecision && <p>{({ APPROVED: 'Approved', CHANGES_REQUESTED: 'Changes requested', REVIEW_REQUIRED: 'Review required' } as Record<string, string>)[s.reviewDecision] ?? s.reviewDecision}</p>}
          {s.mergeable === 'CONFLICTING' && <p className="error-text">Has conflicts with its base</p>}
          {writer ? (
            <>
              <p className="panel-title">Reviewers</p>
              <ReviewersEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} reviewers={s.reviewers} onChanged={writer.onChanged} />
            </>
          ) : s.reviewers.length > 0 && (
            <>
              <p className="panel-title">Reviewers</p>
              <ul className="row-list">
                {s.reviewers.map((r) => (
                  <li key={r.login}>{r.login} <span className="muted">{r.state.toLowerCase().replace('_', ' ')}</span></li>
                ))}
              </ul>
            </>
          )}
          {writer && s.viewerCanUpdate ? (
            <>
              <p className="panel-title">Labels</p>
              <LabelsEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />
              <p className="panel-title">Assignees</p>
              <AssigneesEditor owner={writer.owner} repo={writer.repo} snap={writer.snap} summary={s} onChanged={writer.onChanged} onStale={writer.onStale} />
            </>
          ) : (
            <>
              {s.labels.length > 0 && (
                <p className="review-labels">
                  {s.labels.map((l) => (
                    // The colour is six hex digits or empty: the server checked it, because it ends up here.
                    <span key={l.value} className="label" style={l.color ? { borderColor: `#${l.color}` } : undefined}>{l.name}</span>
                  ))}
                </p>
              )}
              {s.assignees.length > 0 && <p className="muted">Assigned to {s.assignees.join(', ')}</p>}
            </>
          )}
        </>
      ) : (
        <p className="muted">State unavailable{status.summaryError === 'sign-in' ? ' without signing in' : ''}.</p>
      )}
      {status.checks && (
        <p className="muted">
          {checks.length === 0 ? 'No checks' : `${checks.length} check${checks.length === 1 ? '' : 's'}`}
          {failed > 0 && <>, <span className="error-text">{failed} failed</span></>}
          {running > 0 && `, ${running} running`}
        </p>
      )}
    </div>
  );
}

function Conversation({ snap, live, timeline, writer, onOpenPath }: { snap: ReviewSnapshot; live: ReviewStatus['summary']; timeline: Async<ReviewTimeline>; writer: Writer | null; onOpenPath: (p: string) => void }) {
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
  return (
    <>
      <p className="muted">{(live ? live.draft : snap.draft) ? 'Draft, ' : ''}opened by {snap.author}, updated {new Date(snap.when).toLocaleString()}</p>
      {writer && from !== null && editing === 'description' ? (
        // It names the text it replaces: someone else's edit meanwhile is refused, never overwritten.
        <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:description`} path="/edit"
          payload={{ expectBody: from }} initial={from} submitLabel="Save" placeholder="Describe the change"
          onCancel={stopEditing} onDone={() => { stopEditing(); writer.onChanged(); }} onStale={writer.onStale} />
      ) : (
        <>
          {description ? <FragmentHost html={description} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} /> : <p className="muted">No description.</p>}
          {writer && live?.viewerCanUpdate && <button type="button" className="link-button" onClick={startEditing}>Edit the description</button>}
        </>
      )}
      <h2 className="review-heading">Conversation</h2>
      {timeline.state === 'loading' && <p className="muted">Loading...</p>}
      {timeline.state === 'error' && <p className="muted">{timeline.error.message}</p>}
      {timeline.state === 'ok' && (
        <ol className="timeline">
          {timeline.value.items.length === 0 && <li className="muted">Nothing yet.</li>}
          {timeline.value.items.map((item, i) => (
            <li key={i} className={`timeline-${item.kind}`}>
              <span className="timeline-meta">
                <strong>{item.actor}</strong>{' '}
                {item.kind === 'review' ? ({ APPROVED: 'approved', CHANGES_REQUESTED: 'requested changes', COMMENTED: 'reviewed', DISMISSED: 'review dismissed' } as Record<string, string>)[item.state ?? ''] ?? 'reviewed'
                  : item.kind === 'commit' ? 'committed'
                  : item.kind === 'force-push' ? 'force-pushed'
                  : item.kind === 'comment' ? 'commented'
                  : item.kind === 'ready' ? 'marked this ready for review'
                  : item.kind === 'draft' ? 'converted this to a draft'
                  : item.kind}{' '}
                <span className="muted">{new Date(item.when).toLocaleString()}</span>
              </span>
              {item.text && <span className="timeline-text">{item.text} {item.sha && <code className="muted">{item.sha.slice(0, 8)}</code>}</span>}
              {writer && item.id && editing === item.id ? (
                <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:edit:${item.id}`} path={`/comments/${encodeURIComponent(item.id)}`} method="PATCH"
                  initial={item.markdown ?? ''} submitLabel="Save" onCancel={() => setEditing(null)} onDone={() => { setEditing(null); writer.onChanged(); }} onStale={writer.onStale} />
              ) : (
                item.html && <FragmentHost html={item.html} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />
              )}
              {writer && item.id && editing !== item.id && (item.canEdit || item.canDelete) && (
                <span className="comment-tools">
                  {item.canEdit && <button type="button" className="link-button" onClick={() => setEditing(item.id)}>Edit</button>}
                  {item.canDelete && <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/comments/${encodeURIComponent(item.id)}/delete`} label="Delete" confirm="Delete this comment on GitHub?" onDone={writer.onChanged} />}
                </span>
              )}
            </li>
          ))}
        </ol>
      )}
      {timeline.state === 'ok' && timeline.value.truncated && <p className="muted">The conversation continues on GitHub.</p>}
      {writer && (
        <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:conversation`} path="/comments"
          submitLabel="Comment" onDone={writer.onChanged} onStale={writer.onStale} />
      )}
    </>
  );
}

function Commits({ commits }: { commits: Async<ReviewCommits> }) {
  if (commits.state !== 'ok') {
    return <p className="muted">{commits.state === 'loading' ? 'Loading...' : commits.error.message}</p>;
  }
  const c = commits.value;
  return (
    <>
      {!c.headFound && <p className="notice" role="note">The commit you are reviewing is no longer on this branch (it was force-pushed). None of these commits is the one shown.</p>}
      <ul className="commits">
        {c.commits.map((x) => (
          <li key={x.sha} className={x.inSnapshot ? '' : 'commit-new'}>
            <span className="commit-subject">{x.headline}</span>
            <span className="muted row-meta">
              {x.author} · {new Date(x.when).toLocaleString()} · <code>{x.sha.slice(0, 8)}</code>
              {!x.inSnapshot && c.headFound && <> · <strong>new since you opened this review</strong></>}
            </span>
          </li>
        ))}
      </ul>
      {c.truncated && <p className="muted">GitHub lists the first 250 commits; the rest are on GitHub.</p>}
    </>
  );
}

function Checks({ status }: { status: ReviewStatus | null }) {
  if (!status) {
    return <p className="muted">Loading...</p>;
  }
  if (!status.checks) {
    return <p className="muted">The checks could not be read.</p>;
  }
  if (status.checks.length === 0) {
    return <p className="muted">No checks ran on the version you are reviewing.</p>;
  }
  return (
    <ul className="row-list checks">
      {status.checks.map((c, i) => (
        <li key={`${c.source}-${c.name}-${i}`} className={`check check-${c.conclusion ?? c.status}`}>
          <span className="check-mark" aria-hidden="true">{c.status !== 'completed' ? '●' : c.conclusion === 'success' ? '✓' : ['neutral', 'skipped'].includes(c.conclusion ?? '') ? '–' : '✗'}</span>
          <span className="check-name">{c.name}</span>
          <span className="muted">{c.status !== 'completed' ? c.status.replace('_', ' ') : c.conclusion}</span>
          {/* https only, checked by the server; opened, never fetched or framed here. */}
          {c.url && <a href={c.url} target="_blank" rel="noopener noreferrer">Details</a>}
        </li>
      ))}
    </ul>
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
  layout: Layout;
  onView: (v: PaneView) => void;
  onLayout: (l: Layout) => void;
  onLayer: (path: string) => void;
  onOpenPath: (path: string) => void;
  signInUrl: string | null;
  viewed: boolean;
  onViewed: (on: boolean) => void;
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
function FileReview({ writer, path, file, result, threads, threadsState, view, layout, onView, onLayout, onLayer, onOpenPath, signInUrl, viewed, onViewed }: FileReviewProps) {
  const [placed, setPlaced] = useState<string[]>([]);
  // A new comment being written. On Raw, on a diff line (the diff's own numbers are file lines).
  // On a drawing, on an element the server will resolve (a member row, a network), or on a drawn
  // source line of the file its block names — a code body under a table is another file.
  type Draft =
    | { kind: 'line'; side: 'LEFT' | 'RIGHT'; line: number; file: string }
    | { kind: 'anchor'; dom: string };
  const [drafting, setDrafting] = useState<Draft | null>(null);
  const [fileComment, setFileComment] = useState(false);
  useEffect(() => setDrafting(null), [path, view]);
  const canLine = writer !== null && view === 'raw' && file?.inline === true;
  const canDraw = writer !== null && view === 'rendered';
  const placements = result.state === 'ok' ? result.value.placements : [];
  const annotations: Annotation[] = useMemo(() => {
    const byId = new Map(threads.map((t) => [t.id, t]));
    const placedThreads: Annotation[] = view === 'raw'
      ? threads.flatMap((t) => {
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
      const where = drafting.kind === 'anchor' ? drafting.dom : `${drafting.file}:${drafting.side}:${drafting.line}`;
      const selector = drafting.kind === 'anchor'
        ? `[data-anchors~="${drafting.dom}"]`
        : view === 'raw'
          ? (drafting.side === 'LEFT' ? `[data-old-line="${drafting.line}"]` : `[data-line="${drafting.line}"]`)
          : `[data-src-path="${attr(drafting.file)}"] [${drafting.side === 'LEFT' ? 'data-old-file-line' : 'data-file-line'}="${drafting.line}"]`;
      const what = drafting.kind === 'anchor'
        ? `New comment on this ${drafting.dom.startsWith('n-') ? 'network' : 'member'}${drafting.dom.includes('-old-') ? ', as it was' : ''}`
        : `New comment on ${drafting.side === 'LEFT' ? 'old' : 'new'} line ${drafting.line}`;
      placedThreads.push({
        key: `draft:${where}`,
        selector,
        content: (
          <div className="thread thread-draft">
            <header className="thread-head muted">{what}</header>
            <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:draft:${path}:${where}`}
              path={drafting.kind === 'anchor' ? '/anchor-comments' : '/line-comments'}
              payload={drafting.kind === 'anchor'
                ? { path, from: file?.previousPath ?? undefined, dom: drafting.dom }
                : { path: drafting.file, line: drafting.line, side: drafting.side }}
              submitLabel="Comment" alternative={writer.review} onCancel={() => setDrafting(null)}
              onDone={() => { setDrafting(null); writer.onChanged(); }} onStale={writer.onStale} />
          </div>
        ),
      });
    }
    return placedThreads;
  }, [threads, view, onOpenPath, drafting, writer, path, placements, file]);
  const unplaced = threads.filter((t) => !placed.includes(t.id));

  if (result.state === 'loading') {
    return <p className="muted" aria-busy="true">Loading...</p>;
  }
  if (result.state === 'error') {
    return <ErrorPanel error={result.error} signInUrl={signInUrl} />;
  }
  const v = result.value.view;
  return (
    <div className="file-review">
      <div className="pane-head">
        <div className="pane-title" title={v.path}>
          <span className="pane-label">DIFF</span>
          <h2 className="review-file">{v.title}</h2>
        </div>
        <div className="pane-tools">
          {writer && (
            <label className="review-viewed-label">
              <input type="checkbox" checked={viewed} onChange={(e) => onViewed(e.target.checked)} /> Viewed
            </label>
          )}
          <div className="segmented" role="tablist" aria-label="View">
            <button type="button" role="tab" aria-selected={view === 'rendered'} onClick={() => onView('rendered')}>Rendered</button>
            <button type="button" role="tab" aria-selected={view === 'raw'} onClick={() => onView('raw')}>Raw</button>
          </div>
          <div className="segmented" role="group" aria-label="Layout">
            <button type="button" aria-pressed={layout === 'unified'} onClick={() => onLayout('unified')}>Unified</button>
            <button type="button" aria-pressed={layout === 'split'} onClick={() => onLayout('split')}>Side by side</button>
          </div>
        </div>
      </div>
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
      {writer && (
        <div className="file-comment">
          {canLine && <span className="muted">Click a line number to comment on that line. </span>}
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
      <FragmentHost html={v.html} path={v.path} onOpenPath={onOpenPath} line={null} onLine={() => {}} annotations={annotations} onPlaced={setPlaced}
        onRowPick={canLine ? (row) => {
          // A context row has both numbers: it is commented on the new side, as GitHub does.
          const side = row.line !== null ? 'RIGHT' : 'LEFT';
          const line = row.line ?? row.oldLine;
          if (line !== null) {
            setDrafting({ kind: 'line', side, line, file: path });
          }
        } : canDraw ? (row) => {
          // A drawn source line names its file line on each side it exists on; the new side wins
          // for an unchanged line, as on Raw. A row whose block names no file cannot be commented.
          const side = row.fileLine !== null ? 'RIGHT' : 'LEFT';
          const line = row.fileLine ?? row.oldFileLine;
          if (line !== null && row.srcPath) {
            setDrafting({ kind: 'line', side, line, file: row.srcPath });
          }
        } : undefined}
        onAnchorPick={canDraw ? (ids) => {
          // An element standing for both sides is commented on as it is now; one only on the older
          // side (removed) as it was.
          const dom = ids.find((id) => id.includes('-new-')) ?? ids[0];
          if (dom) {
            setDrafting({ kind: 'anchor', dom });
          }
        } : undefined} />
      </div>
    </div>
  );
}

function Thread({ thread, writer, onOpenPath, moved = false }: { thread: ReviewThread; writer: Writer | null; onOpenPath: (p: string) => void; moved?: boolean }) {
  const [replying, setReplying] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
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
      {thread.comments.map((c) => (
        <div key={c.id} className="thread-comment">
          <span className="thread-meta">
            <strong>{c.author}</strong> <span className="muted">{new Date(c.when).toLocaleString()}</span>
            {writer && editing !== c.id && (
              <span className="comment-tools">
                {c.canEdit && <button type="button" className="link-button" onClick={() => setEditing(c.id)}>Edit</button>}
                {c.canDelete && <ActionButton owner={writer.owner} repo={writer.repo} snap={writer.snap} path={`/comments/${encodeURIComponent(c.id)}/delete`} label="Delete" confirm="Delete this comment on GitHub?" onDone={writer.onChanged} />}
              </span>
            )}
          </span>
          {writer && editing === c.id ? (
            <CommentBox owner={writer.owner} repo={writer.repo} snap={writer.snap} slot={`${writer.slots}:edit:${c.id}`} path={`/comments/${encodeURIComponent(c.id)}`} method="PATCH"
              initial={c.markdown ?? ''} submitLabel="Save" onCancel={() => setEditing(null)} onDone={() => { setEditing(null); writer.onChanged(); }} onStale={writer.onStale} />
          ) : (
            <FragmentHost html={c.html} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />
          )}
        </div>
      ))}
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
