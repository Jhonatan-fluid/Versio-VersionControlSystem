import { useState } from 'react';
import type { MergeStateView, ReviewSnapshot } from '../api';
import { api } from '../api';
import type { Async } from '../useAsync';
import { describe, newOpKey, sentUnder } from './ReviewWrite';

const methodWords: Record<string, string> = { MERGE: 'Create a merge commit', SQUASH: 'Squash and merge', REBASE: 'Rebase and merge' };
const methodShort: Record<string, string> = { MERGE: 'a merge commit', SQUASH: 'squash', REBASE: 'rebase' };

interface MergeBoxProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  state: Async<MergeStateView>;
  onChanged: () => void;
  onStale: () => void;
}

/** What is being confirmed, said in full: which pull request, into which branch, at which commit. */
type Pending =
  | { kind: 'merge'; method: string }
  | { kind: 'auto-on'; method: string }
  | { kind: 'auto-off' }
  | { kind: 'update' };

/**
 * The merge box (docs/web/PLAN.md, *Pull request review*, slice 6). Shown only with the merge tier
 * on. It merges only what was reviewed — the snapshot's head, which GitHub checks atomically — and
 * only when GitHub says anyone could merge it now; it never offers a way past required reviews or
 * checks, for an administrator or anyone else. Every action asks first, naming the pull request,
 * the branch it lands on and the commit.
 */
export function MergeBox({ owner, repo, snap, state, onChanged, onStale }: MergeBoxProps) {
  const [method, setMethod] = useState<string | null>(null);
  const [title, setTitle] = useState('');
  const [message, setMessage] = useState('');
  const [pending, setPending] = useState<Pending | null>(null);
  const [busy, setBusy] = useState(false);
  // Auto-merge's own yes: that it may merge commits pushed after this review.
  const [accepts, setAccepts] = useState(false);
  // The key of an action whose outcome is not known, with what it was and the snapshot it went
  // under: confirming the same action again is the same operation, which the server reconciles.
  // A fresh key per confirmation made a merge that had happened, its answer lost, come back as
  // "This pull request is not open" — a failure reported for a success (found in review, 2026-10-20).
  const [held, setHeld] = useState<{ id: string; key: string; sentWith: string } | null>(null);
  const [note, setNote] = useState<string | null>(null);

  if (state.state === 'loading') {
    return <section className="merge-box muted">Reading whether this can be merged...</section>;
  }
  if (state.state === 'error') {
    return <section className="merge-box muted">Whether this can be merged could not be read: {state.error.message}</section>;
  }
  const m = state.value;
  if (!m.tier) {
    return null;
  }
  if (m.error) {
    return <section className="merge-box notice" role="note">GitHub could not say whether this can be merged, so no merge is offered.</section>;
  }
  if (m.merged) {
    return (
      <section className="merge-box">
        <p>
          <strong>Merged</strong>{m.mergeCommit && <> as <code>{m.mergeCommit.slice(0, 8)}</code></>}.
          <span className="muted"> The branch is left as it is: delete it on GitHub, or let the repository's "Automatically delete head branches" do it.</span>
        </p>
      </section>
    );
  }
  if (m.state !== 'OPEN') {
    return null;
  }
  const chosen = method && m.methods.includes(method) ? method : m.methods[0] ?? null;
  const at = snap.headSha.slice(0, 8);
  const where = `#${snap.number} (${snap.headLabel} → ${snap.baseLabel})`;
  // The live head is not the reviewed one: nothing here would merge what was reviewed.
  const moved = m.headSha !== snap.headSha;

  async function run(p: Pending) {
    setBusy(true);
    setNote(null);
    const path = p.kind === 'merge' ? '/merge' : p.kind === 'update' ? '/update-branch' : '/auto-merge';
    const body = p.kind === 'merge'
      ? { method: p.method, title: title.trim() || undefined, message: message.trim() || undefined }
      : p.kind === 'auto-on' ? { enable: true, method: p.method, acceptsLaterCommits: true }
      : p.kind === 'auto-off' ? { enable: false }
      : {};
    const id = JSON.stringify([path, body]);
    const same = held?.id === id ? held : null;
    const key = same?.key ?? newOpKey();
    const under = sentUnder(snap, same?.sentWith);
    setHeld({ id, key, sentWith: under.token });
    const answer = await api.review.write(owner, repo, under, path, 'POST', { ...body, opKey: key });
    setBusy(false);
    setPending(null);
    if (answer.outcome !== 'unknown' && answer.outcome !== 'pending') {
      setHeld(null);
    }
    if (answer.outcome === 'done') {
      onChanged();
      return;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale();
    }
    setNote(describe(answer));
  }

  const question = !pending ? null
    : pending.kind === 'merge' ? `Merge ${where} at commit ${at} by ${methodShort[pending.method] ?? pending.method}? This cannot be undone from here.`
    // Said as it is (found by review, 2026-10-20): GitHub turns auto-merge off only for a push by
    // someone without write access. It is not a merge of this commit, and the first wording said it was.
    : pending.kind === 'auto-on' ? `Have GitHub merge ${where} later, by ${methodShort[pending.method] ?? pending.method}, whenever its required reviews and checks pass. It merges what the branch holds then: commits pushed after your review of ${at} by anyone with write access are merged too, unreviewed unless the repository requires a fresh approval for them.`
    : pending.kind === 'auto-off' ? `Turn off auto-merge for ${where}?`
    : `Bring ${snap.baseLabel} into ${snap.headLabel}? This adds a commit to the branch, so the review is then of an older commit and the page asks you to reload.`;

  return (
    <section className="merge-box" aria-label="Merge">
      {moved && <p className="notice" role="note">This pull request has new commits since you opened it. Load the latest to merge it.</p>}
      {!moved && m.mergeable && (
        <div className="merge-ready">
          <p><strong>Can be merged.</strong> <span className="muted">The commit merged is the one you reviewed, {at}; if anything is pushed first, GitHub refuses.</span></p>
          <div className="merge-fields">
            <select value={chosen ?? ''} onChange={(e) => setMethod(e.target.value)} aria-label="How to merge" disabled={busy}>
              {m.methods.map((x) => <option key={x} value={x}>{methodWords[x] ?? x}</option>)}
            </select>
            {chosen !== 'REBASE' && (
              <>
                <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Commit title (GitHub's if empty)" aria-label="Commit title" maxLength={256} disabled={busy} />
                <textarea className="comment-input" value={message} onChange={(e) => setMessage(e.target.value)} placeholder="Commit message (GitHub's if empty)" aria-label="Commit message" disabled={busy} />
              </>
            )}
          </div>
          {chosen && <button type="button" className="button" disabled={busy} onClick={() => setPending({ kind: 'merge', method: chosen })}>Merge...</button>}
        </div>
      )}
      {!moved && !m.mergeable && (
        <p><strong>Not mergeable now.</strong> <span className="muted">{m.blocked}</span></p>
      )}
      {!moved && m.mergeStateStatus === 'BEHIND' && (
        <button type="button" className="button-quiet" disabled={busy} onClick={() => setPending({ kind: 'update' })}>Update branch...</button>
      )}
      {m.autoMerge ? (
        <p className="merge-auto">
          Auto-merge is on ({methodShort[m.autoMerge.method] ?? m.autoMerge.method}), set by {m.autoMerge.by}. GitHub merges what the branch holds when the requirements pass, including later pushes by people with write access.
          {m.canDisableAutoMerge && <button type="button" className="link-button" disabled={busy} onClick={() => setPending({ kind: 'auto-off' })}>Turn off</button>}
        </p>
      ) : (
        !moved && !m.mergeable && m.autoMergeAllowed && chosen && (
          <button type="button" className="button-quiet" disabled={busy} onClick={() => setPending({ kind: 'auto-on', method: chosen })}>Merge automatically when ready...</button>
        )
      )}
      {pending && question && (
        <div className="merge-confirm" role="alertdialog" aria-label="Confirm">
          <p>{question}</p>
          {pending.kind === 'auto-on' && (
            <label className="merge-accept">
              <input type="checkbox" checked={accepts} onChange={(e) => setAccepts(e.target.checked)} /> I understand it may merge commits pushed after my review
            </label>
          )}
          <div className="comment-actions">
            <button type="button" className="button-quiet" onClick={() => { setPending(null); setAccepts(false); }} disabled={busy}>Cancel</button>
            <button type="button" className="button" onClick={() => run(pending)} disabled={busy || (pending.kind === 'auto-on' && !accepts)}>{busy ? 'Working...' : 'Confirm'}</button>
          </div>
        </div>
      )}
      {note && <p className="notice comment-note" role="status">{note}</p>}
    </section>
  );
}
