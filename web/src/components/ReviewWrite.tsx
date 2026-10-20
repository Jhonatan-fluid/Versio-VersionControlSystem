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
 * Whether the key must be kept for a retry: only when the write may have happened (unknown) or is
 * still happening (pending). After a refusal nothing was posted, so the text is the person's to
 * change again and the next submission is a new operation — keeping the key there locked the box
 * behind a "Try again" that could only meet the same refusal (seen in the preview, 2026-10-17).
 */
function keepKey(answer: WriteAnswer): boolean {
  return answer.outcome === 'unknown' || answer.outcome === 'pending';
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
export function CommentBox({ owner, repo, snap, slot, path, method = 'POST', payload, initial, submitLabel, placeholder, onDone, onCancel, onStale, alternative }: CommentBoxProps) {
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
    if (!draft.body.trim() || busy || (draft.opKey !== null && draft.target && draft.target !== to)) {
      return;
    }
    const opKey = draft.opKey ?? newOpKey();
    setDraft({ ...draft, opKey, target: to });
    setBusy(true);
    setNote(null);
    const answer = await api.review.write(owner, repo, snap, to, method, { ...payload, body: draft.body, opKey });
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
    if (!keepKey(answer)) {
      setDraft((d) => ({ ...d, opKey: null }));
    }
    setNote(describe(answer));
  }

  async function showPreview() {
    setPreview(preview === null ? await api.review.preview(owner, repo, snap, draft.body) : null);
  }

  // Once submitted, the text is the key's payload: changing it would make the retry a different
  // write, which the server refuses. Editing again means starting over with a new key.
  const locked = draft.opKey !== null;
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
      {note && <p className="notice comment-note" role="status">{note}</p>}
      <div className="comment-actions">
        <button type="button" className="button-quiet" onClick={showPreview} disabled={!draft.body.trim()}>{preview === null ? 'Preview' : 'Write'}</button>
        {locked && !busy && (
          <button type="button" className="button-quiet" onClick={() => { setDraft({ ...draft, opKey: null }); setNote(null); }} title="Edit the text again; a new submission follows">
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
}

/**
 * A one-click write (resolve, delete). Its key lives as long as the button does: a second click
 * after an unknown outcome is the same operation, reconciled by the server, not a second one.
 */
export function ActionButton({ owner, repo, snap, path, payload, label, confirm, onDone }: ActionProps) {
  const [opKey, setOpKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  async function run() {
    if (busy || (confirm && !window.confirm(confirm))) {
      return;
    }
    const key = opKey ?? newOpKey();
    setOpKey(key);
    setBusy(true);
    const answer = await api.review.write(owner, repo, snap, path, 'POST', { ...payload, opKey: key });
    setBusy(false);
    if (answer.outcome === 'done') {
      setOpKey(null);
      setNote(null);
      onDone();
      return;
    }
    if (!keepKey(answer)) {
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
