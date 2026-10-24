import { useEffect, useState } from 'react';
import type { CoverageSummary, PendingReviewView, ReviewFile, ReviewSnapshot, ReviewStatus, WriteAnswer } from '../api';
import { api } from '../api';
import { sanitize } from '../fragment';
import type { Async } from '../useAsync';
import { useAsync } from '../useAsync';
import { ActionButton, CommentBox, describe, newOpKey, releasesKey, sentUnder } from './ReviewWrite';
import { Avatar, Counter, Icon } from './ui';

export type ReviewEvent = 'COMMENT' | 'APPROVE' | 'REQUEST_CHANGES';

type Pending = NonNullable<PendingReviewView['review']>;

/**
 * Where a review goes, from the viewer's pending review (docs/web/PLAN.md, C2):
 * - none: one call that creates and submits it on the snapshot's head;
 * - one created on the snapshot's head: that review, submitted;
 * - one created on an older commit: the historical route, which names that commit — the reviewer
 *   approves what they were shown the name of, never the head by implication.
 */
export function reviewTarget(pending: Pending | null): { path: string; commit?: string; historical: boolean } {
  if (!pending) {
    return { path: '/reviews', historical: false };
  }
  const id = encodeURIComponent(pending.id);
  return pending.onSnapshotHead
    ? { path: `/reviews/${id}/submit`, historical: false }
    : { path: `/reviews/${id}/submit-historical`, commit: pending.commit, historical: true };
}

/**
 * What the dialog says before an approval (C4): files never opened here, and files whose drawing
 * may not show every change. It informs and never blocks — the reviewer may well have read Raw.
 */
export function approvalNotes(files: ReviewFile[], opened: ReadonlySet<string>, viewed: ReadonlySet<string>, coverage: CoverageSummary | null) {
  const unopened = files.filter((f) => !opened.has(f.path) && !viewed.has(f.path)).map((f) => f.path);
  const unsure = coverage
    ? coverage.files.filter((f) => f.coverage !== 'complete').map((f) => f.path)
    : files.map((f) => f.path);
  return { unopened, unsure, coverageKnown: coverage !== null };
}

interface ReviewBarProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  slots: string;
  status: ReviewStatus | null;
  pending: Async<PendingReviewView>;
  files: ReviewFile[];
  opened: ReadonlySet<string>;
  viewed: ReadonlySet<string>;
  onChanged: () => void;
  onStale: () => void;
  onOpenPath: (path: string) => void;
}

const never = () => new Promise<never>(() => {});

/**
 * The review: the viewer's pending one if there is one, and the dialog that submits it — as
 * Comment, Approve or Request changes, always under the viewer's own name.
 */
export function ReviewBar({ owner, repo, snap, slots, status, pending, files, opened, viewed, onChanged, onStale, onOpenPath }: ReviewBarProps) {
  const [open, setOpen] = useState(false);
  const [submitted, setSubmitted] = useState<string | null>(null);
  // The drafts of the pending review, each editable in place (Step 5): which one is being edited.
  const [editing, setEditing] = useState<string | null>(null);
  const [draftsOpen, setDraftsOpen] = useState(false);
  // Read again right after a submit, not at the next poll: a push that landed while the review was
  // being submitted is what the reviewer most needs told (C2 — the race we narrow but cannot close).
  const [movedSince, setMovedSince] = useState(false);
  const review = pending.state === 'ok' ? pending.value.review : null;
  const author = status?.summary?.viewerIsAuthor === true;
  // Drawing every changed file is the server's most expensive read: asked for only when the dialog
  // that totals it up is open, once per snapshot.
  const coverage = useAsync((s) => (open ? api.review.coverage(owner, repo, snap, s) : never()), [open, snap.token]);

  return (
    <section className="review-bar" aria-label="Your review">
      <div className="review-bar-row">
        {pending.state === 'loading' && <span className="muted">Reading your review...</span>}
        {pending.state === 'error' && <span className="muted">Your pending review could not be read: {pending.error.message}</span>}
        {pending.state === 'ok' && !review && <span className="muted">No review started. Comments added to a review stay private until you submit it.</span>}
        {review && (
          <span>
            Your pending review: {review.truncated ? 'more than ' : ''}{review.comments.length} comment{review.comments.length === 1 ? '' : 's'}{review.truncated ? ' (the rest are on GitHub)' : ''}
            {!review.onSnapshotHead && (
              <strong className="review-old"> on commit <code>{review.commit.slice(0, 8)}</code>, not the one you are reviewing</strong>
            )}
          </span>
        )}
        <span className="review-bar-tools">
          {review && (
            <ActionButton owner={owner} repo={repo} snap={snap} path={`/reviews/${encodeURIComponent(review.id)}/discard`} label="Discard"
              confirm="Discard your pending review and its comments? This cannot be undone." onDone={onChanged} />
          )}
          {pending.state === 'ok' && (
            // GitHub's "Review changes" button with its pending count; the dialog opens under it.
            <span className="review-changes">
              <button type="button" className="btn btn-primary" onClick={() => setOpen(!open)} aria-expanded={open}>
                {review ? 'Finish your review' : 'Review changes'}
              </button>
              {review && review.comments.length > 0 && <Counter scheme="primary">{review.comments.length}</Counter>}
              <Icon name="triangle-down" className="text-muted" />
            </span>
          )}
        </span>
      </div>
      {review && review.comments.length > 0 && (
        <div className="review-pending-comments">
          <button type="button" className="link-button" onClick={() => setDraftsOpen(!draftsOpen)} aria-expanded={draftsOpen}>
            {draftsOpen ? 'Hide your drafts' : 'Show your drafts'}
          </button>
          {draftsOpen && (
            <ul className="row-list">
              {review.comments.map((c) => (
                <li key={c.id} className="review-draft">
                  <div className="review-draft-head">
                    <button type="button" className="link-button" onClick={() => onOpenPath(c.path)}>{c.pathLabel}</button>
                    {c.line != null && <span className="text-muted"> line {c.startLine != null && c.startLine !== c.line ? `${c.startLine}–` : ''}{c.line}</span>}
                    {c.markdown !== undefined && editing !== c.id && (
                      <span className="comment-tools">
                        <button type="button" className="link-button" onClick={() => setEditing(c.id)}>Edit</button>
                        <ActionButton owner={owner} repo={repo} snap={snap} path={`/comments/${encodeURIComponent(c.id)}/delete`} label="Delete" confirm="Delete this draft?" onDone={onChanged} />
                      </span>
                    )}
                  </div>
                  {editing === c.id ? (
                    // A draft is a review comment of the viewer's own: the same edit route as any comment, on a pending one.
                    <CommentBox owner={owner} repo={repo} snap={snap} slot={`${slots}:edit:${c.id}`} path={`/comments/${encodeURIComponent(c.id)}`} method="PATCH"
                      initial={c.markdown ?? ''} submitLabel="Save" onCancel={() => setEditing(null)} onDone={() => { setEditing(null); onChanged(); }} onStale={onStale} />
                  ) : (
                    <div className="review-draft-body fragment" dangerouslySetInnerHTML={{ __html: sanitize(c.html) }} />
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {submitted && (
        <p className="notice" role="status">
          {submitted}
          {(movedSince || status?.headMoved) && ' New commits arrived since you opened this review: your review stays on the commit you were shown, and GitHub shows it there.'}
        </p>
      )}
      {open && pending.state === 'ok' && (
        // Keyed by its draft's slot, as CommentBox is: one pull request's review never saved under another's.
        <ReviewDialog key={`${slots}:review`} owner={owner} repo={repo} snap={snap} slot={`${slots}:review`} pending={review} author={author}
          notes={coverage.state === 'loading' ? null : approvalNotes(files, opened, viewed, coverage.state === 'ok' ? coverage.value : null)} onOpenPath={onOpenPath} onStale={onStale}
          onCancel={() => setOpen(false)}
          onDone={(what) => {
            setOpen(false);
            setSubmitted(what);
            setMovedSince(false);
            onChanged();
            api.review.status(owner, repo, snap).then((s) => setMovedSince(s.headMoved), () => {});
          }} />
      )}
    </section>
  );
}

const eventWords: Record<ReviewEvent, string> = { COMMENT: 'Comment', APPROVE: 'Approve', REQUEST_CHANGES: 'Request changes' };

interface ReviewDialogProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  slot: string;
  pending: Pending | null;
  author: boolean;
  /** Null while the coverage summary is being read. */
  notes: ReturnType<typeof approvalNotes> | null;
  onOpenPath: (path: string) => void;
  onStale: () => void;
  onCancel: () => void;
  onDone: (what: string) => void;
}

interface Held {
  body: string;
  event: ReviewEvent;
  /** As in CommentBox: kept while the outcome is unknown, so a retry is the same operation. */
  opKey: string | null;
  /**
   * Where the held key was sent — the route, and for a historical review its commit — kept with
   * the key. A retry goes there and nowhere else: worked out again, it changes under the person.
   * A submit that succeeded with its answer lost leaves no pending review, so after a reload the
   * target became "/reviews", a different operation in the ledger, and a second review (found by
   * review, 2026-10-20).
   */
  target?: Target | null;
  /**
   * The snapshot the held key was sent under. A retry goes with it: sent with the page's newer one,
   * after a push and a reload, an approval that never reached the server would approve a commit the
   * person never saw (found by review, 2026-10-20). The server binds the revision into the
   * operation, so the new one would be refused as a different operation anyway.
   */
  sentWith?: string | null;
  /** That snapshot's head, which the message after submitting names: the commit reviewed, not the page's now. */
  sentHead?: string | null;
}

type Target = ReturnType<typeof reviewTarget>;

function readHeld(slot: string): Held {
  try {
    const raw = sessionStorage.getItem(slot);
    if (raw) {
      return JSON.parse(raw) as Held;
    }
  } catch {
    // Storage off: the dialog starts empty.
  }
  return { body: '', event: 'COMMENT', opKey: null };
}

function ReviewDialog({ owner, repo, snap, slot, pending, author, notes, onOpenPath, onStale, onCancel, onDone }: ReviewDialogProps) {
  const [held, setHeld] = useState<Held>(() => readHeld(slot));
  const [busy, setBusy] = useState(false);
  // A key kept over a reload means an earlier submission's outcome is not known: said, so "Try again" is understood as checking first.
  const [note, setNote] = useState<string | null>(() => (readHeld(slot).opKey ? 'Your last submission may or may not have gone through. Try again checks first, and never submits twice.' : null));
  const target: Target = held.opKey !== null && held.target ? held.target : reviewTarget(pending);
  // GitHub refuses an author's approval or request for changes on their own pull request: not offered.
  const events: ReviewEvent[] = author ? ['COMMENT'] : ['COMMENT', 'APPROVE', 'REQUEST_CHANGES'];
  const event = events.includes(held.event) ? held.event : 'COMMENT';
  const locked = held.opKey !== null;
  // Words are needed where the server needs them: for a request for changes, and for a comment
  // only as a one-shot review (a pending review's comments are its content). Asked of the target
  // the key was sent to, not of whether a pending review exists now: after a lost answer and a
  // reload there is none, and asking that locked a retry behind a rule it never had to meet
  // (found by review, 2026-10-20).
  const needsWords = event === 'REQUEST_CHANGES' || (event === 'COMMENT' && target.path === '/reviews');

  useEffect(() => {
    try {
      if (!held.body && !held.opKey) {
        sessionStorage.removeItem(slot);
      } else {
        sessionStorage.setItem(slot, JSON.stringify(held));
      }
    } catch {
      // Storage off.
    }
  }, [slot, held]);

  async function submit() {
    if (busy || (needsWords && !held.body.trim())) {
      return;
    }
    const opKey = held.opKey ?? newOpKey();
    const under = sentUnder(snap, held.opKey !== null ? held.sentWith : null);
    const head = held.opKey !== null && held.sentHead ? held.sentHead : snap.headSha;
    setHeld({ ...held, event, opKey, target, sentWith: under.token, sentHead: head });
    setBusy(true);
    setNote(null);
    const answer: WriteAnswer = await api.review.write(owner, repo, under, target.path, 'POST',
      { event, body: held.body, opKey, ...(target.commit ? { commit: target.commit } : {}) });
    setBusy(false);
    if (answer.outcome === 'done') {
      try {
        sessionStorage.removeItem(slot);
      } catch {
        // Storage off.
      }
      setHeld({ body: '', event: 'COMMENT', opKey: null, target: null, sentWith: null });
      onDone(`${eventWords[event]} submitted on ${(target.commit ?? head).slice(0, 8)}.`);
      return;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale();
    }
    if (releasesKey(answer, held.opKey !== null)) {
      setHeld((h) => ({ ...h, opKey: null, target: null, sentWith: null }));
    }
    setNote(describe(answer));
  }

  return (
    <div className="review-dialog comment-box" role="group" aria-label="Submit your review">
      {target.historical && (
        <p className="notice" role="note">
          This review was started on commit <code>{target.commit!.slice(0, 8)}</code>, not on the one you are reviewing now
          (<code>{snap.headSha.slice(0, 8)}</code>). It is submitted as a review of <code>{target.commit!.slice(0, 8)}</code>,
          and GitHub shows it there. To review the latest, discard it and start again.
        </p>
      )}
      {!notes && <p className="muted">Checking which drawings show every change...</p>}
      {notes && notes.unopened.length > 0 && (
        <details className="review-notes">
          <summary>{notes.unopened.length} file{notes.unopened.length === 1 ? '' : 's'} not opened here</summary>
          <ul className="row-list">
            {notes.unopened.map((p) => <li key={p}><button type="button" className="link-button" onClick={() => onOpenPath(p)}>{p}</button></li>)}
          </ul>
        </details>
      )}
      {notes && notes.unsure.length > 0 && (
        <details className="review-notes">
          <summary>
            {notes.coverageKnown
              ? `${notes.unsure.length} file${notes.unsure.length === 1 ? '' : 's'} with changes the drawing may not show — check Raw`
              : 'Whether the drawings show every change could not be checked — check Raw'}
          </summary>
          <ul className="row-list">
            {notes.unsure.map((p) => <li key={p}><button type="button" className="link-button" onClick={() => onOpenPath(p)}>{p}</button></li>)}
          </ul>
        </details>
      )}
      <textarea className="comment-input" value={held.body} disabled={locked} placeholder="Leave a comment"
        onChange={(e) => setHeld({ ...held, body: e.target.value })} aria-label="Review comment" />
      <fieldset className="review-events" disabled={locked}>
        <legend className="visually-hidden">Review</legend>
        {events.map((e) => (
          <label key={e}>
            <input type="radio" name={`${slot}:event`} value={e} checked={event === e} onChange={() => setHeld({ ...held, event: e })} /> {eventWords[e]}
          </label>
        ))}
        {author && <span className="muted"> You opened this pull request, so GitHub accepts only a comment from you.</span>}
      </fieldset>
      {note && <p className="notice comment-note" role="status">{note}</p>}
      <div className="comment-actions">
        {locked && !busy && (
          <button type="button" className="button-quiet" onClick={() => { setHeld({ ...held, opKey: null, target: null, sentWith: null }); setNote(null); }}>Edit</button>
        )}
        <button type="button" className="button-quiet" onClick={onCancel}>Cancel</button>
        <button type="button" className="button" onClick={submit} disabled={busy || (needsWords && !held.body.trim())}>
          {busy ? 'Submitting...' : locked ? 'Try again' : `Submit: ${eventWords[event]}`}
        </button>
      </div>
    </div>
  );
}

type ReviewerRow = { login: string; state: string; isTeam?: boolean; avatar?: string | null; slug?: string | null };

/** GitHub's icon beside a reviewer's standing. */
function reviewerMark(state: string): { icon: 'check' | 'x' | 'comment' | 'dot-fill' | 'eye'; tone: string; words: string } {
  switch (state) {
    case 'APPROVED': return { icon: 'check', tone: 'text-success', words: 'approved these changes' };
    case 'CHANGES_REQUESTED': return { icon: 'x', tone: 'text-danger', words: 'requested changes' };
    case 'COMMENTED': return { icon: 'comment', tone: 'text-muted', words: 'left review comments' };
    case 'DISMISSED': return { icon: 'x', tone: 'text-muted', words: 'review was dismissed' };
    default: return { icon: 'dot-fill', tone: 'text-attention', words: 'awaiting requested review' };
  }
}

/**
 * Who is asked to review: people by login, teams by slug (Step 5); a request withdrawn, a reviewer
 * who already reviewed asked again (GitHub's "re-request"). Safe to repeat, so no key is held
 * across clicks beyond the one in flight.
 */
export function ReviewersEditor({ owner, repo, snap, reviewers, onChanged }: { owner: string; repo: string; snap: ReviewSnapshot; reviewers: ReviewerRow[]; onChanged: () => void }) {
  const [login, setLogin] = useState('');
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  async function change(body: { add?: string[]; remove?: string[]; addTeams?: string[]; removeTeams?: string[] }) {
    setBusy(true);
    setNote(null);
    const answer = await api.review.write(owner, repo, snap, '/reviewers', 'POST', { ...body, opKey: newOpKey() });
    setBusy(false);
    if (answer.outcome === 'done') {
      setLogin('');
      onChanged();
    } else {
      setNote(describe(answer));
    }
  }

  // "org/team" or "team:slug" names a team by its slug; anything else is a person's login.
  function ask() {
    const typed = login.trim();
    if (!typed) {
      return;
    }
    const team = typed.startsWith('team:') ? typed.slice('team:'.length) : typed.includes('/') ? typed.slice(typed.lastIndexOf('/') + 1) : null;
    void change(team ? { addTeams: [team] } : { add: [typed] });
  }

  return (
    <div className="reviewers-editor">
      <ul className="row-list">
        {reviewers.map((r) => {
          const mark = reviewerMark(r.state);
          const teamSlug = r.isTeam ? r.slug ?? null : null;
          // A person is named by login; a team only by its slug, when the server gave one.
          const canName = !r.isTeam || teamSlug !== null;
          return (
            <li key={r.login} className="reviewer-row">
              {r.isTeam ? <Icon name="person" className="text-muted" /> : <Avatar login={r.login} url={r.avatar} size={20} />}
              <span className="reviewer-name">{r.login}</span>
              <span className={`reviewer-mark ${mark.tone}`} title={mark.words}><Icon name={mark.icon} size={14} /></span>
              {canName && r.state === 'REQUESTED' && (
                <button type="button" className="link-button" disabled={busy} onClick={() => change(r.isTeam ? { removeTeams: [teamSlug!] } : { remove: [r.login] })} aria-label={`Stop asking ${r.login}`}>remove</button>
              )}
              {canName && r.state !== 'REQUESTED' && (
                <button type="button" className="link-button" disabled={busy} onClick={() => change(r.isTeam ? { addTeams: [teamSlug!] } : { add: [r.login] })} aria-label={`Re-request review from ${r.login}`} title="Re-request review"><Icon name="sync" size={14} /></button>
              )}
            </li>
          );
        })}
      </ul>
      <form className="reviewers-add" onSubmit={(e) => { e.preventDefault(); ask(); }}>
        <input value={login} onChange={(e) => setLogin(e.target.value)} placeholder="Login, or org/team" aria-label="Ask for a review from" disabled={busy} />
        <button type="submit" className="btn btn-small" disabled={busy || !login.trim()}>Ask</button>
      </form>
      {note && <p className="notice comment-note" role="status">{note}</p>}
    </div>
  );
}
