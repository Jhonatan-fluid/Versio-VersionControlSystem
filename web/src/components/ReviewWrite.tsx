import { useEffect, useState, type MutableRefObject } from 'react';
import type { ReviewSnapshot, WriteAnswer } from '../api';
import { api } from '../api';
import { sanitize } from '../fragment';
import { Icon } from './ui';

/**
 * An operation key: when it was made, and a random part. The server reads the time to refuse a
 * retry too old to trust (docs/web/PLAN.md, C5), so expiry needs no storage on either side.
 */
export function newOpKey(): string {
  const random = (globalThis.crypto?.randomUUID?.() ?? `${Math.random()}${Math.random()}`).replace(/[^A-Za-z0-9]/g, '');
  return `${Date.now()}-${random.slice(0, 32).padEnd(16, '0')}`;
}

export interface Draft {
  body: string;
  /** Set when first submitted, kept for every retry, dropped once the outcome is known. */
  opKey: string | null;
  /** Where the held key was sent: a retry goes there and nowhere else. */
  target?: string | null;
  /** The snapshot the held key was sent under: a retry goes with it, never with a newer one. */
  sentWith?: string | null;
}

/** Drafts survive a reload, a "load the latest" and a dropped connection: losing someone's words is the failure that matters. */
export function readDraft(slot: string): Draft {
  try {
    const raw = sessionStorage.getItem(slot);
    return raw ? (JSON.parse(raw) as Draft) : { body: '', opKey: null };
  } catch {
    return { body: '', opKey: null };
  }
}

export function writeDraft(slot: string, draft: Draft | null) {
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
 * Moves the words of one box to another that has none yet — a draft on a member row switched to
 * the row as it was (Step 3b). Only while no key is held: once sent, the text is that operation's
 * payload and belongs to its box; and never over words the other box already has.
 */
export function carryDraft(fromSlot: string, toSlot: string): void {
  const from = readDraft(fromSlot);
  const to = readDraft(toSlot);
  if (from.opKey === null && from.body && !to.body && !to.opKey) {
    writeDraft(toSlot, { body: from.body, opKey: null });
    writeDraft(fromSlot, null);
  }
}

/**
 * The snapshot a write goes under: the one its held key was first sent under, when there is one,
 * otherwise the page's. The token is all that differs; the server reads every revision from it.
 *
 * Every held key is saved together with the snapshot it went under, in the same state update, so a
 * key without one does not arise. Drafts saved before that were once detected and refused a retry
 * ("unrecoverable", review 2026-10-20); the branch was never deployed with any, so that went
 * (2026-10-22). The tests that retry after a reload onto a newer revision are what guard this.
 */
export function sentUnder(snap: ReviewSnapshot, sentWith: string | null | undefined): ReviewSnapshot {
  return sentWith && sentWith !== snap.token ? { ...snap, token: sentWith } : snap;
}

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
  /**
   * The line a suggested change starts from (Step 5): the toolbar's "Suggest a change" puts it in a
   * ```suggestion block for the person to edit, as github.com's toolbar does.
   */
  suggestFrom?: string | null;
  /**
   * Lets the page submit this box itself — "Close with comment" posts the comment, then closes.
   * The function resolves true once the server said the comment is posted.
   */
  submitRef?: MutableRefObject<(() => Promise<boolean>) | null>;
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

function DraftBox({ owner, repo, snap, slot, path, method = 'POST', payload, initial, submitLabel, placeholder, onDone, onCancel, onStale, alternative, suggestFrom, submitRef }: CommentBoxProps) {
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
  async function submit(to: string = path): Promise<boolean> {
    if (!draft.body.trim() || busy || (draft.opKey !== null && draft.target && draft.target !== to)) {
      return false;
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
      return true;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale?.();
    }
    if (releasesKey(answer, draft.opKey !== null)) {
      setDraft((d) => ({ ...d, opKey: null, sentWith: null }));
    }
    setNote(describe(answer));
    return false;
  }

  // The page's own submit ("Close with comment"): the same submit, so the key rules are the same.
  useEffect(() => {
    if (submitRef) {
      submitRef.current = () => submit();
      return () => {
        submitRef.current = null;
      };
    }
  });

  async function showPreview() {
    setPreview(preview === null ? await api.review.preview(owner, repo, snap, draft.body) : null);
  }

  // GitHub's suggestion block: the line as it is, for the person to change. Appended to the draft
  // on its own lines, never inserted into a word.
  function suggest() {
    const block = '```suggestion\n' + (suggestFrom ?? '') + '\n```';
    setDraft({ body: draft.body.trim() ? `${draft.body.replace(/\s+$/, '')}\n\n${block}\n` : `${block}\n`, opKey: null });
  }

  // Once submitted, the text is the key's payload: changing it would make the retry a different
  // write, which the server refuses. Editing again means starting over with a new key.
  const locked = draft.opKey !== null;
  return (
    <div className="comment-box">
      {suggestFrom !== undefined && suggestFrom !== null && preview === null && (
        <div className="comment-toolbar">
          <button type="button" className="btn btn-invisible btn-small" disabled={locked} onClick={suggest} title="Insert a suggestion: the line as it is, for you to change">
            <Icon name="light-bulb" className="btn-icon" /> Suggest a change
          </button>
        </div>
      )}
      {preview === null ? (
        <textarea
          value={draft.body}
          placeholder={placeholder ?? 'Leave a comment'}
          aria-label={placeholder ?? 'Leave a comment'}
          readOnly={locked}
          onChange={(e) => setDraft({ body: e.target.value, opKey: null })}
          rows={4}
        />
      ) : (
        <div className="comment-preview fragment" dangerouslySetInnerHTML={{ __html: sanitize(preview) }} />
      )}
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
        <button type="button" className="button" onClick={() => submit(locked && draft.target ? draft.target : path)} disabled={busy || !draft.body.trim()}>
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
  /** Drawn as a button rather than a link, GitHub's small button. */
  button?: boolean;
  /** The pull request moved: said by the page, as for a comment box. */
  onStale?: () => void;
}

/** One choice of a reason form: GitHub's word, and what the person reads. */
export interface ReasonOption {
  value: string | null;
  label: string;
}

interface ReasonProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  path: string;
  /** The button's words, and the form's heading. */
  label: string;
  /** Words to type (a dismissal's reason), or a choice (a lock's, a hidden comment's). */
  kind: 'text' | 'choice';
  options?: ReasonOption[];
  /** The request body for the value given. */
  payloadFor: (value: string | null) => Record<string, unknown>;
  /** The submit button's words. */
  submitLabel: string;
  placeholder?: string;
  onDone: () => void;
  onStale?: () => void;
}

/**
 * A write that needs a reason first — dismissing a review, hiding a comment, locking — as a
 * small form that opens under its button (GitHub's dialog). The key rules are ActionButton's:
 * one key per form while the outcome is unknown, released on done or on a first refusal.
 */
export function ReasonAction({ owner, repo, snap, path, label, kind, options, payloadFor, submitLabel, placeholder, onDone, onStale }: ReasonProps) {
  const [open, setOpen] = useState(false);
  const [value, setValue] = useState<string | null>(kind === 'choice' ? options?.[0]?.value ?? null : '');
  const [opKey, setOpKey] = useState<string | null>(null);
  const [sentWith, setSentWith] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  async function run() {
    if (busy || (kind === 'text' && !(value ?? '').trim())) {
      return;
    }
    const key = opKey ?? newOpKey();
    const under = sentUnder(snap, opKey !== null ? sentWith : null);
    setOpKey(key);
    setSentWith(under.token);
    setBusy(true);
    const answer = await api.review.write(owner, repo, under, path, 'POST', { ...payloadFor(kind === 'text' ? (value ?? '').trim() : value), opKey: key });
    setBusy(false);
    if (answer.outcome === 'done') {
      setOpKey(null);
      setNote(null);
      setOpen(false);
      setValue(kind === 'choice' ? options?.[0]?.value ?? null : '');
      onDone();
      return;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale?.();
    }
    if (releasesKey(answer, opKey !== null)) {
      setOpKey(null);
    }
    setNote(describe(answer));
  }

  return (
    <span className="reason-action">
      <button type="button" className="link-button" onClick={() => setOpen(!open)} aria-expanded={open}>{label}</button>
      {open && (
        <form className="reason-form" onSubmit={(e) => { e.preventDefault(); void run(); }} aria-label={label}>
          {kind === 'text' ? (
            <textarea value={value ?? ''} onChange={(e) => setValue(e.target.value)} placeholder={placeholder} rows={3} readOnly={opKey !== null} aria-label={`${label}: reason`} />
          ) : (
            <select value={value ?? ''} onChange={(e) => setValue(e.target.value === '' ? null : e.target.value)} disabled={opKey !== null} aria-label={`${label}: reason`}>
              {(options ?? []).map((o) => <option key={o.value ?? ''} value={o.value ?? ''}>{o.label}</option>)}
            </select>
          )}
          {note && <p className="notice comment-note" role="status">{note}</p>}
          <div className="comment-actions">
            <button type="button" className="btn btn-small" onClick={() => { setOpen(false); setNote(null); }} disabled={busy}>Cancel</button>
            <button type="submit" className="btn btn-small btn-danger" disabled={busy || (kind === 'text' && !(value ?? '').trim())}>{busy ? 'Working...' : opKey !== null ? 'Try again' : submitLabel}</button>
          </div>
        </form>
      )}
    </span>
  );
}

/**
 * A one-click write (resolve, delete). Its key lives as long as the button does: a second click
 * after an unknown outcome is the same operation, reconciled by the server, not a second one.
 */
export function ActionButton({ owner, repo, snap, path, payload, label, confirm, onDone, button = false, onStale }: ActionProps) {
  // The held key is the key of ONE write: this path with this payload. The callers derive the
  // payload from polled state and invert it (Resolve becomes Unresolve, Close becomes Reopen),
  // and a write whose answer was lost may well have happened — so when the poll flips the
  // payload while a key is held, the key is for the other write and is let go, rather than
  // sent again with the opposite payload (found in review, 2026-10-24; MergeBox keys its held
  // key the same way).
  const signature = JSON.stringify([path, payload ?? null]);
  const [held, setHeld] = useState<{ key: string; sentWith: string | null; for: string } | null>(null);
  const holding = held !== null && held.for === signature ? held : null;
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  async function run() {
    if (busy || (confirm && !window.confirm(confirm))) {
      return;
    }
    const key = holding?.key ?? newOpKey();
    const under = sentUnder(snap, holding?.sentWith ?? null);
    setHeld({ key, sentWith: under.token, for: signature });
    setBusy(true);
    const answer = await api.review.write(owner, repo, under, path, 'POST', { ...payload, opKey: key });
    setBusy(false);
    if (answer.outcome === 'done') {
      setHeld(null);
      setNote(null);
      onDone();
      return;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale?.();
    }
    if (releasesKey(answer, holding !== null)) {
      setHeld(null);
    }
    setNote(describe(answer));
  }

  return (
    <>
      <button type="button" className={button ? 'btn btn-small' : 'link-button'} onClick={run} disabled={busy}>{busy ? '...' : label}</button>
      {note && <span className="comment-note-inline" role="status">{note}</span>}
    </>
  );
}
