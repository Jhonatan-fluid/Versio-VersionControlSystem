import { useEffect, useState } from 'react';
import type { RepoLabel, ReviewSnapshot, ReviewStatus, WriteAnswer } from '../api';
import { api } from '../api';
import { useAsync } from '../useAsync';
import { ActionButton, ReasonAction, describe, newOpKey } from './ReviewWrite';
import { Icon, IssueLabel } from './ui';

type Summary = NonNullable<ReviewStatus['summary']>;

interface MetaProps {
  owner: string;
  repo: string;
  snap: ReviewSnapshot;
  onChanged: () => void;
  onStale: () => void;
}

/**
 * A metadata write: safe to repeat on the server (each reconciles by reading the pull request
 * back), so a fresh key per click is right — unlike a comment, a second "Save" of the same title
 * cannot make a second anything.
 */
function useMetaWrite({ owner, repo, snap, onChanged, onStale }: MetaProps) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  async function run(path: string, payload: Record<string, unknown>): Promise<boolean> {
    setBusy(true);
    setNote(null);
    const answer: WriteAnswer = await api.review.write(owner, repo, snap, path, 'POST', { ...payload, opKey: newOpKey() });
    setBusy(false);
    if (answer.outcome === 'done') {
      onChanged();
      return true;
    }
    if (answer.error === 'snapshot-stale' || answer.error === 'snapshot-expired') {
      onStale();
    }
    setNote(describe(answer));
    return false;
  }
  return { busy, note, run };
}

/**
 * The title, edited in place. It names the title it replaces: if someone renamed the pull request
 * meanwhile, the server refuses and the person's words stay in the box.
 */
export function TitleEditor(props: MetaProps & { summary: Summary; onDone: () => void }) {
  // What is being replaced is fixed when editing starts. The live summary is polled; taking it at
  // submit would name SOMEONE ELSE'S new title as the one replaced, and overwrite it.
  const [from] = useState(props.summary.titleText);
  const [title, setTitle] = useState(from);
  const write = useMetaWrite(props);
  return (
    <form className="title-editor" onSubmit={async (e) => {
      e.preventDefault();
      if (await write.run('/edit', { title, expectTitle: from })) {
        props.onDone();
      }
    }}>
      <input value={title} onChange={(e) => setTitle(e.target.value)} aria-label="Title" maxLength={256} disabled={write.busy} />
      <button type="submit" className="button" disabled={write.busy || !title.trim() || title === from}>Save</button>
      <button type="button" className="button-quiet" onClick={props.onDone}>Cancel</button>
      {write.note && <p className="notice comment-note" role="status">{write.note}</p>}
    </form>
  );
}

/**
 * The base, changed. This review was read against the old base, so once it changes the page says
 * the pull request changed and offers the reload — nothing is reviewed against a base it no longer has.
 */
export function BaseEditor(props: MetaProps & { branches: string[]; current: string; head: string; onDone: () => void }) {
  const [base, setBase] = useState(props.current);
  const write = useMetaWrite(props);
  return (
    <form className="base-editor" onSubmit={async (e) => {
      e.preventDefault();
      if (await write.run('/edit', { base })) {
        props.onDone();
      }
    }}>
      <select value={base} onChange={(e) => setBase(e.target.value)} aria-label="Merge into" disabled={write.busy}>
        {props.branches.filter((b) => b !== props.head).map((b) => <option key={b} value={b}>{b}</option>)}
      </select>
      <button type="submit" className="button" disabled={write.busy || base === props.current}>Change base</button>
      <button type="button" className="button-quiet" onClick={props.onDone}>Cancel</button>
      {write.note && <p className="notice comment-note" role="status">{write.note}</p>}
    </form>
  );
}

/** Close or reopen; draft or ready. A merged pull request has neither. */
export function StateActions({ owner, repo, snap, summary, onChanged, onStale }: MetaProps & { summary: Summary }) {
  if (summary.merged) {
    return null;
  }
  const open = summary.state === 'OPEN';
  // onStale goes through, as every other write's does: a close refused as stale offered the inline
  // note but not the page's "load the latest" (review, 2026-10-24).
  return (
    <p className="state-actions">
      {open && (
        <ActionButton owner={owner} repo={repo} snap={snap} path="/draft" payload={{ draft: !summary.draft }}
          label={summary.draft ? 'Ready for review' : 'Convert to draft'} onDone={onChanged} onStale={onStale} />
      )}
      <ActionButton owner={owner} repo={repo} snap={snap} path="/state" payload={{ open: !open }}
        label={open ? 'Close' : 'Reopen'} confirm={open ? 'Close this pull request on GitHub?' : undefined} onDone={onChanged} onStale={onStale} />
    </p>
  );
}

/** Labels the repository has: put on, taken off. None is made here. */
export function LabelsEditor(props: MetaProps & { summary: Summary }) {
  const write = useMetaWrite(props);
  const labels = useAsync((s) => api.labels(props.owner, props.repo, s), [props.owner, props.repo]);
  const on = new Set(props.summary.labels.map((l) => l.value));
  const offered: RepoLabel[] = labels.state === 'ok' ? labels.value.labels.filter((l) => !on.has(l.value)) : [];
  return (
    <div className="labels-editor">
      <p className="review-labels">
        {props.summary.labels.map((l) => (
          // Drawn as everywhere else (the filled label with contrast text): the sidebar drew a
          // border-only pill, so one label looked like two on the same page (review, 2026-10-24).
          <span key={l.value} className="label-with-remove">
            <IssueLabel name={l.name} color={l.color} />
            <button type="button" className="link-button" disabled={write.busy} onClick={() => write.run('/labels', { add: [], remove: [l.value] })} aria-label={`Remove the label ${l.name}`}>×</button>
          </span>
        ))}
      </p>
      {offered.length > 0 && (
        <select value="" disabled={write.busy} aria-label="Add a label" onChange={(e) => e.target.value && write.run('/labels', { add: [e.target.value], remove: [] })}>
          <option value="">Add a label</option>
          {offered.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
          {labels.state === 'ok' && labels.value.truncated && <option value="" disabled>More labels on GitHub than listed here</option>}
        </select>
      )}
      {write.note && <p className="notice comment-note" role="status">{write.note}</p>}
    </div>
  );
}

/** The milestone, one of the repository's open ones or none: put on, never made, as labels are (Step 5). */
export function MilestoneEditor(props: MetaProps & { summary: Summary }) {
  const write = useMetaWrite(props);
  const milestones = useAsync((s) => api.milestones(props.owner, props.repo, s), [props.owner, props.repo]);
  const current = props.summary.milestoneNumber ?? null;
  // The pick is kept locally until the poll agrees: a controlled select bound to the polled value
  // snapped back to the old option the moment it was picked (review, 2026-10-24).
  const [picked, setPicked] = useState<string | null>(null);
  useEffect(() => setPicked(null), [current]);
  return (
    <div className="milestone-editor">
      {props.summary.milestone && <p><Icon name="milestone" className="text-muted" /> {props.summary.milestone}</p>}
      {milestones.state === 'ok' && (
        <select value={picked ?? (current === null ? '' : String(current))} disabled={write.busy} aria-label="Milestone"
          onChange={(e) => { setPicked(e.target.value); write.run('/milestone', { number: e.target.value === '' ? null : Number(e.target.value) }); }}>
          <option value="">No milestone</option>
          {(milestones.value.milestones ?? []).map((m) => <option key={m.number} value={m.number}>{m.title}</option>)}
          {milestones.value.truncated && <option value="" disabled>More milestones on GitHub than listed here</option>}
        </select>
      )}
      {milestones.state === 'error' && <p className="text-muted text-small">Milestones could not be read.</p>}
      {write.note && <p className="notice comment-note" role="status">{write.note}</p>}
    </div>
  );
}

/** Lock the conversation with GitHub's reasons, or unlock it: a write for people with write access, which GitHub decides. */
export function LockAction({ owner, repo, snap, summary, onChanged, onStale }: MetaProps & { summary: Summary }) {
  if (summary.locked) {
    return (
      <p className="lock-action">
        <ActionButton owner={owner} repo={repo} snap={snap} path="/lock" payload={{ lock: false }} label="Unlock conversation" onDone={onChanged} onStale={onStale} />
      </p>
    );
  }
  return (
    <p className="lock-action">
      <ReasonAction owner={owner} repo={repo} snap={snap} path="/lock" label="Lock conversation" kind="choice" submitLabel="Lock"
        options={[{ value: null, label: 'No reason' }, { value: 'OFF_TOPIC', label: 'Off-topic' }, { value: 'TOO_HEATED', label: 'Too heated' }, { value: 'RESOLVED', label: 'Resolved' }, { value: 'SPAM', label: 'Spam' }]}
        payloadFor={(reason) => ({ lock: true, reason })} onDone={onChanged} onStale={onStale} />
    </p>
  );
}

/** The viewer's own notifications for this pull request: GitHub's Subscribe / Unsubscribe. */
export function SubscriptionAction({ owner, repo, snap, summary, onChanged, onStale }: MetaProps & { summary: Summary }) {
  const subscribed = summary.viewerSubscription === 'SUBSCRIBED';
  return (
    <div className="subscription-action">
      <ActionButton owner={owner} repo={repo} snap={snap} path="/subscription" payload={{ state: subscribed ? 'UNSUBSCRIBED' : 'SUBSCRIBED' }}
        label={subscribed ? 'Unsubscribe' : 'Subscribe'} onDone={onChanged} onStale={onStale} button />
      <p className="text-muted text-small">
        {subscribed ? "You're receiving notifications because you're watching this pull request." : summary.viewerSubscription === 'IGNORED' ? "You're ignoring this pull request." : "You're not receiving notifications from this pull request."}
      </p>
    </div>
  );
}

/** Assignees by login; the server asks GitHub first whether each can be assigned, since GitHub would drop them silently. */
export function AssigneesEditor(props: MetaProps & { summary: Summary }) {
  const write = useMetaWrite(props);
  const [login, setLogin] = useState('');
  return (
    <div className="assignees-editor">
      <ul className="row-list">
        {props.summary.assignees.map((a) => (
          <li key={a}>
            {a}
            <button type="button" className="link-button" disabled={write.busy} onClick={() => write.run('/assignees', { add: [], remove: [a] })} aria-label={`Unassign ${a}`}>remove</button>
          </li>
        ))}
      </ul>
      <form className="reviewers-add" onSubmit={async (e) => {
        e.preventDefault();
        if (login.trim() && await write.run('/assignees', { add: [login.trim()], remove: [] })) {
          setLogin('');
        }
      }}>
        <input value={login} onChange={(e) => setLogin(e.target.value)} placeholder="GitHub login" aria-label="Assign" disabled={write.busy} />
        <button type="submit" className="button-quiet" disabled={write.busy || !login.trim()}>Assign</button>
      </form>
      {write.note && <p className="notice comment-note" role="status">{write.note}</p>}
    </div>
  );
}
