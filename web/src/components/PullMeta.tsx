import { useState } from 'react';
import type { RepoLabel, ReviewSnapshot, ReviewStatus, WriteAnswer } from '../api';
import { api } from '../api';
import { useAsync } from '../useAsync';
import { ActionButton, describe, newOpKey } from './ReviewWrite';

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
export function StateActions({ owner, repo, snap, summary, onChanged }: MetaProps & { summary: Summary }) {
  if (summary.merged) {
    return null;
  }
  const open = summary.state === 'OPEN';
  return (
    <p className="state-actions">
      {open && (
        <ActionButton owner={owner} repo={repo} snap={snap} path="/draft" payload={{ draft: !summary.draft }}
          label={summary.draft ? 'Ready for review' : 'Convert to draft'} onDone={onChanged} />
      )}
      <ActionButton owner={owner} repo={repo} snap={snap} path="/state" payload={{ open: !open }}
        label={open ? 'Close' : 'Reopen'} confirm={open ? 'Close this merge request on GitHub?' : undefined} onDone={onChanged} />
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
          // The colour is six hex digits or empty: the server checked it, because it ends up here.
          <span key={l.value} className="label" style={l.color ? { borderColor: `#${l.color}` } : undefined}>
            {l.name}
            <button type="button" className="link-button" disabled={write.busy} onClick={() => write.run('/labels', { add: [], remove: [l.value] })} aria-label={`Remove the label ${l.name}`}>×</button>
          </span>
        ))}
      </p>
      {offered.length > 0 && (
        <select value="" disabled={write.busy} aria-label="Add a label" onChange={(e) => e.target.value && write.run('/labels', { add: [e.target.value], remove: [] })}>
          <option value="">Add a label</option>
          {offered.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
        </select>
      )}
      {write.note && <p className="notice comment-note" role="status">{write.note}</p>}
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
