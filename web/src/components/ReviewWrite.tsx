import { useEffect, useState } from 'react';
import type { ReviewSnapshot, WriteAnswer } from '../api';
import { api } from '../api';
import { sanitize } from '../fragment';

/**
 * An operation key: when it was made, and a random part. The server reads the time to refuse a
 * retry too old to trust (docs/web/PLAN.md, C5), so expiry needs no storage on either side.
 */
export function newOpKey(): string {
  const random = (globalThis.crypto?.randomUUID?.() ?? `${Math.random()}${Math.random()}`).replace(/[^A-Za-z0-9]/g, '');
  return `${Date.now()}-${random.slice(0, 32).padEnd(16, '0')}`;
}

interface Draft {
  body: string;
  /** Set when first submitted, kept for every retry, dropped once the outcome is known. */
  opKey: string | null;
  /** Where the held key was sent: a retry goes there and nowhere else. */
  target?: string | null;
  /** The snapshot the held key was sent under: a retry goes with it, never with a newer one. */
  sentWith?: string | null;
}

/** Drafts survive a reload, a "load the latest" and a dropped connection: losing someone's words is the failure that matters. */
function readDraft(slot: string): Draft {
  try {
    const raw = sessionStorage.getItem(slot);
    return raw ? (JSON.parse(raw) as Draft) : { body: '', opKey: null };
  } catch {
    return { body: '', opKey: null };
  }
}

function writeDraft(slot: string, draft: Draft | null) {
  try {
    if (draft === null || (!draft.body && !draft.opKey)) {
      sessionStorage.removeItem(slot);
    } else {
      sessionStorage.setItem(slot, JSON.stringify(draft));
    }
  } catch {
    // Private mode or storage off: the draft lives as long as the page does.
  }
}

/**
 * The snapshot a write goes under: the one its held key was first sent under, when there is one,
 * otherwise the page's. The token is all that differs; the server reads every revision from it.
 */
export function sentUnder(snap: ReviewSnapshot, sentWith: string | null | undefined): ReviewSnapshot {
  return sentWith && sentWith !== snap.token ? { ...snap, token: sentWith } : snap;
}

/**
 * A held key saved without the snapshot it was sent under: an operation from before revisions were
 * recorded with keys. Retrying it under the page's snapshot could apply it to a revision the person
 * never saw — an approval of a newer commit — and the revision it WAS for is not known, so it is
 * not retried at all. The person checks GitHub and, if need be, submits again deliberately (found
 * by review, 2026-10-20: sentUnder quietly substituted today's snapshot).
 */
export function unrecoverable(opKey: string | null | undefined, sentWith: string | null | undefined): boolean {
  return Boolean(opKey) && !sentWith;
}

export const UNRECOVERABLE = 'This was submitted by an earlier version of Versio, which did not record which version of the pull request it was for, so it cannot be retried safely. Check on GitHub whether it went through; if it did not, choose Edit and submit it again.';

/** What the person is told about a write that did not simply succeed. */
export function describe(answer: WriteAnswer): string {
  if (answer.outcome === 'unknown') {
    return answer.message ?? 'This may or may not have been posted. Refresh to check before posting it again.';
  }
  if (answer.outcome === 'pending') {
    return 'Still being posted. Try again in a moment to see how it ended.';
  }
  return answer.message ?? 'That did not work. Try again.';
}

/**
 * Whether an answer lets the operation's key go, so the next submission is a new operation.
 *
 * Done: yes. Unknown or pending: no — the write may have happened, or is happening. A refusal:
 * on a FIRST attempt, yes — nothing else was ever sent, so nothing was posted, and keeping the key
 * locked the box behind a "Try again" that could only meet the same refusal (seen in the preview,
 * 2026-10-17). On a RETRY, only if the server says the refusal is the operation's recorded outcome
 * (`settled`): a 429, a sign-in, a stale snapshot say only that THIS request did nothing, while the
 * first may have succeeded. Letting the key go there sent the next submission under a new key,
 * past reconciliation, and could post twice (found by review, 2026-10-20).
 */
export function releasesKey(answer: WriteAnswer, retry: boolean): boolean {
  if (answer.outcome === 'done') {
    return true;
  }
  if (answer.outcome === 'unknown' || answer.outcome === 'pending') {
    return false;
  }
  return !retry || answer.settled === true;
}

interface CommentBoxProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  /** Where the draft is kept: one per place a comment can be written. */
  slot: string;
  path: string;
  method?: 'POST' | 'PATCH';
  /** The rest of the write's body; the text and the operation key are added here. */
  payload?: Record<string, unknown>;
  initial?: string;
  submitLabel: string;
  placeholder?: string;
  onDone: () => void;
  onCancel?: () => void;
  /** The pull request moved: the page offers to load the latest. */
  onStale?: () => void;
  /**
   * A second place the same comment can go — "Add to review", into the viewer's pending review —
   * with the same payload. Offered only while no key is held (see submit).
   */
  alternative?: { path: string; label: string };
}

/**
 * A comment being written: the text, a preview drawn by the server's Markdown, and the outcome of
 * posting it said plainly. The draft is never cleared until the server says the comment is posted.
 */
export function CommentBox(props: CommentBoxProps) {
  // One box per draft. React reuses a component when only its props change — the file comment box
  // stays mounted as the person moves from file to file — and the box's state then held one file's
  // draft while saving it under the next file's slot: "Pump draft" overwrote Valve's (found by
  // review, 2026-10-20). Keyed by its slot, a new draft is a new box that reads its own.
  return <DraftBox key={props.slot} {...props} />;
}

function DraftBox({ owner, repo, snap, slot, path, method = 'POST', payload, initial, submitLabel, placeholder, onDone, onCancel, onStale, alternative }: CommentBoxProps) {
  const [draft, setDraft] = useState<Draft>(() => {
    const kept = readDraft(slot);
    return kept.body || kept.opKey ? kept : { body: initial ?? '', opKey: null };
  });
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [preview, setPreview] = useState<string | null>(null);

  useEffect(() => writeDraft(slot, draft), [slot, draft]);

  // A held key belongs to the target it was sent to. Posting the same words somewhere else is a
  // different operation, and the first may already have happened — so while a key is held only a
  // retry of that target is offered, never the other button.
  async function submit(to: string = path) {
    if (!draft.body.trim() || busy || orphan || (draft.opKey !== null && draft.target && draft.target !== to)) {
      return;
    }
    const opKey = draft.opKey ?? newOpKey();
    // A retry goes under the snapshot the key was first sent under, which the server binds into
    // the operation: after a push and a reload it is never applied to the new revision instead.
    const under = sentUnder(snap, draft.opKey !== null ? draft.sentWith : null);
    setDraft({ ...draft, opKey, target: to, sentWith: under.token });
    setBusy(true);
    setNote(null);
    const answer = await api.review.write(owner, repo, under, to, method, { ...payload, body: draft.body, opKey });
    setBusy(false);
    if (answer.outcome === 'done') {
      writeDraft(slot, null);
      setDraft({ body: '', opKey: null });
      setPreview(null);
      onDone();
      return;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale?.();
    }
    if (releasesKey(answer, draft.opKey !== null)) {
      setDraft((d) => ({ ...d, opKey: null, sentWith: null }));
    }
    setNote(describe(answer));
  }

  async function showPreview() {
    setPreview(preview === null ? await api.review.preview(owner, repo, snap, draft.body) : null);
  }

  // Once submitted, the text is the key's payload: changing it would make the retry a different
  // write, which the server refuses. Editing again means starting over with a new key.
  const locked = draft.opKey !== null;
  const orphan = unrecoverable(draft.opKey, draft.sentWith);
  return (
    <div className="comment-box">
      {preview === null ? (
        <textarea
          value={draft.body}
          placeholder={placeholder ?? 'Leave a comment'}
          readOnly={locked}
          onChange={(e) => setDraft({ body: e.target.value, opKey: null })}
          rows={4}
        />
      ) : (
        <div className="comment-preview fragment" dangerouslySetInnerHTML={{ __html: sanitize(preview) }} />
      )}
      {orphan && <p className="notice comment-note" role="status">{UNRECOVERABLE}</p>}
      {note && <p className="notice comment-note" role="status">{note}</p>}
      <div className="comment-actions">
        <button type="button" className="button-quiet" onClick={showPreview} disabled={!draft.body.trim()}>{preview === null ? 'Preview' : 'Write'}</button>
        {locked && !busy && (
          <button type="button" className="button-quiet" onClick={() => { setDraft({ ...draft, opKey: null, sentWith: null }); setNote(null); }} title="Edit the text again; a new submission follows">
            Edit text
          </button>
        )}
        {onCancel && <button type="button" className="button-quiet" onClick={() => { writeDraft(slot, null); onCancel(); }}>Cancel</button>}
        {alternative && !locked && (
          <button type="button" className="button-quiet" onClick={() => submit(alternative.path)} disabled={busy || !draft.body.trim()}>
            {alternative.label}
          </button>
        )}
        <button type="button" className="button" onClick={() => submit(locked && draft.target ? draft.target : path)} disabled={busy || orphan || !draft.body.trim()}>
          {busy ? 'Posting...' : locked ? `Try again` : submitLabel}
        </button>
      </div>
    </div>
  );
}

interface ActionProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  path: string;
  payload?: Record<string, unknown>;
  label: string;
  confirm?: string;
  onDone: () => void;
}

/**
 * A one-click write (resolve, delete). Its key lives as long as the button does: a second click
 * after an unknown outcome is the same operation, reconciled by the server, not a second one.
 */
export function ActionButton({ owner, repo, snap, path, payload, label, confirm, onDone }: ActionProps) {
  const [opKey, setOpKey] = useState<string | null>(null);
  const [sentWith, setSentWith] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  async function run() {
    if (busy || (confirm && !window.confirm(confirm))) {
      return;
    }
    const key = opKey ?? newOpKey();
    const under = sentUnder(snap, opKey !== null ? sentWith : null);
    setOpKey(key);
    setSentWith(under.token);
    setBusy(true);
    const answer = await api.review.write(owner, repo, under, path, 'POST', { ...payload, opKey: key });
    setBusy(false);
    if (answer.outcome === 'done') {
      setOpKey(null);
      setNote(null);
      onDone();
      return;
    }
    if (releasesKey(answer, opKey !== null)) {
      setOpKey(null);
    }
    setNote(describe(answer));
  }

  return (
    <>
      <button type="button" className="link-button" onClick={run} disabled={busy}>{busy ? '...' : label}</button>
      {note && <span className="comment-note-inline" role="status">{note}</span>}
    </>
  );
}
