import { useEffect, useRef, useState } from 'react';
import type { MergeStateView, ReviewSnapshot } from '../api';
import { api } from '../api';
import type { Async } from '../useAsync';
import { describe, newOpKey, releasesKey, sentUnder } from './ReviewWrite';
import { Icon } from './ui';

const methodWords: Record<string, string> = { MERGE: 'Create a merge commit', SQUASH: 'Squash and merge', REBASE: 'Rebase and merge' };
const primaryWords: Record<string, string> = { MERGE: 'Merge pull request', SQUASH: 'Squash and merge', REBASE: 'Rebase and merge' };
const methodShort: Record<string, string> = { MERGE: 'a merge commit', SQUASH: 'squash', REBASE: 'rebase' };
const methodWhat: Record<string, string> = {
  MERGE: 'All commits from this branch will be added to the base branch via a merge commit.',
  SQUASH: 'The commits from this branch will be combined into one commit in the base branch.',
  REBASE: 'The commits from this branch will be rebased and added to the base branch.',
};

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
  | { kind: 'update'; method: 'MERGE' | 'REBASE' }
  | { kind: 'delete-branch' }
  | { kind: 'restore-branch' }
  | { kind: 'enqueue' }
  | { kind: 'dequeue' };

/**
 * The merge box (docs/web/PLAN.md, *Pull request review*, slice 6; GitHub's shape since
 * FINISH.md Step 6). Shown only with the merge tier on. It merges only what was reviewed — the
 * snapshot's head, which GitHub checks atomically — and only when GitHub says anyone could merge
 * it now; it never offers a way past required reviews or checks, for an administrator or anyone
 * else. Every action asks first, naming the pull request, the branch it lands on and the commit.
 *
 * The split button is GitHub's: the primary half merges by the chosen method, the caret opens the
 * method menu. "Update branch" has the same shape, with "Update with rebase" in its menu. After a
 * merge or a close the box offers the head branch's deletion — with the race said, because
 * GitHub's ref delete has no expected commit (the owner's decision, 2026-10-23) — or its
 * restoration; a repository that deletes branches itself is said instead. A base branch with a
 * merge queue gets "Add to merge queue" in place of the merge.
 */
export function MergeBox({ owner, repo, snap, state, onChanged, onStale }: MergeBoxProps) {
  const [method, setMethod] = useState<string | null>(null);
  const [menu, setMenu] = useState<'merge' | 'update' | null>(null);
  const [title, setTitle] = useState('');
  const [message, setMessage] = useState('');
  const [pending, setPending] = useState<Pending | null>(null);
  const confirmBox = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (pending) {
      confirmBox.current?.focus();
    }
  }, [pending]);
  const [busy, setBusy] = useState(false);
  // Auto-merge's own yes: that it may merge commits pushed after this review.
  const [accepts, setAccepts] = useState(false);
  // The key of an action whose outcome is not known, with what it was and the snapshot it went
  // under: confirming the same action again is the same operation, which the server reconciles.
  // A fresh key per confirmation made a merge that had happened, its answer lost, come back as
  // "This pull request is not open" — a failure reported for a success (found in review, 2026-10-20).
  // One per action: trying another (or the same with another message) must not drop the first's
  // unknown outcome — a single slot did, and the "not open" misreport came back (found in review,
  // 2026-10-21).
  const [held, setHeld] = useState<Record<string, { key: string; sentWith: string }>>({});
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
  const chosen = method && m.methods.includes(method) ? method : m.methods[0] ?? null;
  const at = snap.headSha.slice(0, 8);
  const where = `#${snap.number} (${snap.headLabel} → ${snap.baseLabel})`;
  // The live head is not the reviewed one: nothing here would merge what was reviewed.
  const moved = m.headSha !== snap.headSha;
  const branch = m.headRefLabel ?? snap.headLabel;

  async function run(p: Pending) {
    setBusy(true);
    setNote(null);
    const path = p.kind === 'merge' ? '/merge'
      : p.kind === 'update' ? '/update-branch'
      : p.kind === 'delete-branch' ? '/delete-branch'
      : p.kind === 'restore-branch' ? '/restore-branch'
      : p.kind === 'enqueue' || p.kind === 'dequeue' ? '/merge-queue'
      : '/auto-merge';
    const body = p.kind === 'merge'
      ? { method: p.method, title: title.trim() || undefined, message: message.trim() || undefined }
      : p.kind === 'auto-on' ? { enable: true, method: p.method, acceptsLaterCommits: true }
      : p.kind === 'auto-off' ? { enable: false }
      : p.kind === 'update' ? { method: p.method }
      : p.kind === 'enqueue' ? { enqueue: true }
      : p.kind === 'dequeue' ? { enqueue: false }
      : {};
    const id = JSON.stringify([path, body]);
    const same = held[id] ?? null;
    const key = same?.key ?? newOpKey();
    const under = sentUnder(snap, same?.sentWith);
    setHeld((h) => ({ ...h, [id]: { key, sentWith: under.token } }));
    const answer = await api.review.write(owner, repo, under, path, 'POST', { ...body, opKey: key });
    setBusy(false);
    setPending(null);
    if (releasesKey(answer, same !== null)) {
      setHeld((h) => {
        const { [id]: _, ...rest } = h;
        return rest;
      });
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
    : pending.kind === 'update' ? (pending.method === 'REBASE'
      ? `Rebase ${snap.headLabel} onto ${snap.baseLabel}? Every commit of the branch is rewritten, so the review is then of older commits and the page asks you to reload. If the answer is lost, whether it happened cannot be told from here.`
      : `Bring ${snap.baseLabel} into ${snap.headLabel}? This adds a commit to the branch, so the review is then of an older commit and the page asks you to reload.`)
    // The race, said: GitHub's ref delete has no expected commit (FINISH.md Step 6, the owner's decision).
    : pending.kind === 'delete-branch' ? `Delete the branch ${branch}? It is deleted only if it is still at ${at}, the commit this pull request ${m.merged ? 'merged' : 'ended on'}; a push landing in the moment between that check and the delete would be lost. The branch can be restored from here afterwards.`
    : pending.kind === 'restore-branch' ? `Restore the branch ${branch} at ${at}?`
    : pending.kind === 'enqueue' ? `Add ${where} at ${at} to the merge queue? GitHub merges it in turn once its requirements pass; a push first makes GitHub refuse.`
    : `Remove ${where} from the merge queue?`;

  const closed = m.merged || m.state !== 'OPEN';
  return (
    <section className="merge-box" aria-label="Merge">
      {closed ? (
        <>
          <p>
            <strong>{m.merged ? 'Merged' : 'Closed'}</strong>{m.merged && m.mergeCommit && <> as <code>{m.mergeCommit.slice(0, 8)}</code></>}.
            {m.merged && m.deleteBranchOnMerge && <span className="muted"> This repository deletes head branches itself after a merge.</span>}
          </p>
          {/* The branch after the fact: a fork's is not ours; the repository's own setting does the job without the race. */}
          {!m.isCrossRepository && m.headRef && !(m.merged && m.deleteBranchOnMerge && !m.headRefExists) && (
            <div className="merge-branch">
              {m.headRefExists !== false ? (
                <>
                  <button type="button" className="btn btn-small" disabled={busy} onClick={() => setPending({ kind: 'delete-branch' })}>Delete branch</button>
                  <span className="muted"> {branch} is still there.</span>
                </>
              ) : (
                <>
                  <button type="button" className="btn btn-small" disabled={busy} onClick={() => setPending({ kind: 'restore-branch' })}>Restore branch</button>
                  <span className="muted"> {branch} was deleted.</span>
                </>
              )}
            </div>
          )}
        </>
      ) : (
        <>
          {moved && <p className="notice" role="note">This pull request has new commits since you opened it. Load the latest to merge it.</p>}
          {!moved && m.inMergeQueue && (
            <p className="merge-queued">
              <Icon name="git-merge" className="text-muted" /> <strong>In the merge queue</strong>{m.queuePosition != null && <>, position {m.queuePosition}</>}. GitHub merges it in turn.
              <button type="button" className="link-button" disabled={busy} onClick={() => setPending({ kind: 'dequeue' })}>Remove from queue</button>
            </p>
          )}
          {!moved && !m.inMergeQueue && m.hasMergeQueue && (
            <div className="merge-ready">
              <p><strong>This base branch has a merge queue.</strong> <span className="muted">Pull requests are merged in turn, at {at} unless pushed first.</span></p>
              <button type="button" className="btn btn-primary" disabled={busy} onClick={() => setPending({ kind: 'enqueue' })}>Add to merge queue</button>
            </div>
          )}
          {!moved && !m.hasMergeQueue && m.mergeable && (
            <div className="merge-ready">
              <p><strong>Can be merged.</strong> <span className="muted">The commit merged is the one you reviewed, {at}; if anything is pushed first, GitHub refuses. {chosen && methodWhat[chosen]}</span></p>
              {chosen && (
                <div className="merge-split">
                  <span className="btn-group">
                    <button type="button" className="btn btn-primary" disabled={busy} onClick={() => { setMenu(null); setPending({ kind: 'merge', method: chosen }); }}>{primaryWords[chosen] ?? chosen}</button>
                    {m.methods.length > 1 && (
                      <button type="button" className="btn btn-primary merge-method-caret" aria-label="Choose how to merge" aria-expanded={menu === 'merge'} disabled={busy} onClick={() => setMenu(menu === 'merge' ? null : 'merge')}>
                        <Icon name="triangle-down" />
                      </button>
                    )}
                  </span>
                  {menu === 'merge' && (
                    <div className="merge-method-menu" role="group" aria-label="How to merge">
                      {m.methods.map((x) => (
                        <label key={x} className="merge-method-option">
                          <input type="radio" name="merge-method" value={x} checked={chosen === x} onChange={() => { setMethod(x); setMenu(null); }} />
                          <span><span className="text-bold">{methodWords[x] ?? x}</span><br /><span className="muted">{methodWhat[x]}</span></span>
                        </label>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
          {!moved && !m.hasMergeQueue && !m.mergeable && !m.inMergeQueue && (
            <p><strong>{m.mergeStateStatus === 'BLOCKED' ? 'Merging is blocked.' : 'Not mergeable now.'}</strong> <span className="muted">{m.blocked}</span></p>
          )}
          {!moved && m.mergeStateStatus === 'BEHIND' && (
            <div className="merge-split">
              <span className="btn-group">
                <button type="button" className="btn btn-small" disabled={busy} onClick={() => { setMenu(null); setPending({ kind: 'update', method: 'MERGE' }); }}>Update branch</button>
                <button type="button" className="btn btn-small merge-method-caret" aria-label="Choose how to update" aria-expanded={menu === 'update'} disabled={busy} onClick={() => setMenu(menu === 'update' ? null : 'update')}>
                  <Icon name="triangle-down" />
                </button>
              </span>
              {menu === 'update' && (
                <div className="merge-method-menu" role="group" aria-label="How to update">
                  <button type="button" className="merge-method-option link-button" onClick={() => { setMenu(null); setPending({ kind: 'update', method: 'MERGE' }); }}>
                    <span><span className="text-bold">Update with merge commit</span><br /><span className="muted">A merge of {snap.baseLabel} into {snap.headLabel}.</span></span>
                  </button>
                  <button type="button" className="merge-method-option link-button" onClick={() => { setMenu(null); setPending({ kind: 'update', method: 'REBASE' }); }}>
                    <span><span className="text-bold">Update with rebase</span><br /><span className="muted">The branch rebased onto {snap.baseLabel}: every commit rewritten.</span></span>
                  </button>
                </div>
              )}
            </div>
          )}
          {m.autoMerge ? (
            <p className="merge-auto">
              Auto-merge is on ({methodShort[m.autoMerge.method] ?? m.autoMerge.method}), set by {m.autoMerge.by}. GitHub merges what the branch holds when the requirements pass, including later pushes by people with write access.
              {m.canDisableAutoMerge && <button type="button" className="link-button" disabled={busy} onClick={() => setPending({ kind: 'auto-off' })}>Turn off</button>}
            </p>
          ) : (
            !moved && !m.mergeable && !m.hasMergeQueue && m.autoMergeAllowed && chosen && (
              <button type="button" className="btn btn-small" disabled={busy} onClick={() => setPending({ kind: 'auto-on', method: chosen })}>Merge automatically when ready...</button>
            )
          )}
        </>
      )}
      {pending && question && (
        // Focus moves into the confirmation and Escape cancels it: a keyboard user who pressed
        // Merge otherwise had no sign that a question had appeared (review, 2026-10-24).
        <div className="merge-confirm" role="alertdialog" aria-label="Confirm" tabIndex={-1} ref={confirmBox}
          onKeyDown={(e) => { if (e.key === 'Escape' && !busy) { setPending(null); setAccepts(false); } }}>
          <p>{question}</p>
          {pending.kind === 'merge' && pending.method !== 'REBASE' && (
            <div className="merge-fields">
              <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Commit title (GitHub's if empty)" aria-label="Commit title" maxLength={256} disabled={busy} />
              <textarea className="comment-input" value={message} onChange={(e) => setMessage(e.target.value)} placeholder="Commit message (GitHub's if empty)" aria-label="Commit message" disabled={busy} />
            </div>
          )}
          {pending.kind === 'auto-on' && (
            <label className="merge-accept">
              <input type="checkbox" checked={accepts} onChange={(e) => setAccepts(e.target.checked)} /> I understand it may merge commits pushed after my review
            </label>
          )}
          <div className="comment-actions">
            <button type="button" className="btn btn-small" onClick={() => { setPending(null); setAccepts(false); }} disabled={busy}>Cancel</button>
            <button type="button" className={`btn btn-small ${pending.kind === 'delete-branch' ? 'btn-danger' : 'btn-primary'}`} onClick={() => run(pending)} disabled={busy || (pending.kind === 'auto-on' && !accepts)}>{busy ? 'Working...' : 'Confirm'}</button>
          </div>
        </div>
      )}
      {note && <p className="notice comment-note" role="status">{note}</p>}
    </section>
  );
}
