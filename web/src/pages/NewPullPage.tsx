import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { ErrorPanel } from '../components/Pane';
import { describe, newOpKey } from '../components/ReviewWrite';
import { Workbench } from '../components/Workbench';
import { compareUrl, pullUrl, pullsUrl, treeUrl } from '../routes';
import { useAsync } from '../useAsync';

interface Form {
  base: string;
  head: string;
  title: string;
  body: string;
  draft: boolean;
  /** The template the body was filled from, so a base change refills only an untouched body. */
  template: string | null;
  /** As in CommentBox: kept from the first submit while the outcome is unknown, so a retry is the same operation. */
  opKey: string | null;
}

function readForm(slot: string): Form | null {
  try {
    const raw = sessionStorage.getItem(slot);
    return raw ? (JSON.parse(raw) as Form) : null;
  } catch {
    return null;
  }
}

function writeForm(slot: string, form: Form | null) {
  try {
    if (form === null) {
      sessionStorage.removeItem(slot);
    } else {
      sessionStorage.setItem(slot, JSON.stringify(form));
    }
  } catch {
    // Storage off: the form lives as long as the page does.
  }
}

/**
 * /owner/repo/pulls/new?base=&head=: open a merge request between two branches of this repository
 * (docs/web/PLAN.md, *Pull request review*, slice 5), in the person's own name, with the
 * repository's template, as a draft if they want. A fork's branch cannot be chosen here.
 *
 * The form survives a reload, and once submitted its words are the operation's: while the outcome
 * is unknown the only way on is the same request again, which the server recognises, never a
 * second pull request.
 */
export function NewPullPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const slot = `versio:new-pull:${owner}/${repo}`;

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const pulls = useAsync((s) => api.pulls(owner, repo, s), [owner, repo]);
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : '';

  const [form, setForm] = useState<Form>(() => readForm(slot) ?? {
    base: search.get('base') ?? '', head: search.get('head') ?? '', title: '', body: '', draft: false, template: null, opKey: null,
  });
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const base = form.base || defaultBranch;
  const locked = form.opKey !== null;

  useEffect(() => writeForm(slot, form), [slot, form]);

  const template = useAsync((s) => (base ? api.pullTemplate(owner, repo, base, s) : new Promise<never>(() => {})), [owner, repo, base]);
  useEffect(() => {
    // The template fills the description only while the person has not written their own.
    if (template.state === 'ok' && !locked) {
      const markdown = template.value.markdown;
      // The same object back when nothing changes: a new one each time would re-render forever.
      setForm((f) => ((f.body === '' || f.body === f.template) && (f.body !== (markdown ?? '') || f.template !== markdown)
        ? { ...f, body: markdown ?? '', template: markdown }
        : f));
    }
  }, [template, locked]);

  const writable = pulls.state === 'ok' && pulls.value.writable === true;
  const names = branches.state === 'ok' ? branches.value.branches.map((b) => b.name) : [];
  const same = form.head !== '' && form.head === base;
  const ready = !busy && base !== '' && form.head !== '' && !same && form.title.trim() !== '';

  async function submit() {
    if (!ready) {
      return;
    }
    const opKey = form.opKey ?? newOpKey();
    setForm({ ...form, base, opKey });
    setBusy(true);
    setNote(null);
    const answer = await api.createPull(owner, repo, { opKey, base, head: form.head, title: form.title, body: form.body, draft: form.draft });
    setBusy(false);
    if (answer.outcome === 'done' && answer.id) {
      writeForm(slot, null);
      navigate(pullUrl(owner, repo, Number(answer.id)));
      return;
    }
    if (answer.outcome !== 'unknown' && answer.outcome !== 'pending') {
      setForm((f) => ({ ...f, opKey: null }));
    }
    setNote(describe(answer));
  }

  const set = (change: Partial<Form>) => setForm((f) => ({ ...f, ...change }));

  return (
    <Workbench
      me={me}
      owner={owner}
      repo={repo}
      refName={defaultBranch || 'main'}
      defaultBranch={defaultBranch || 'main'}
      branches={branches.state === 'ok' ? branches.value.branches : []}
      branchesTruncated={branches.state === 'ok' && branches.value.truncated}
      onRef={(r) => navigate(treeUrl(owner, repo, r))}
      rail="pulls"
      panelTitle="New merge request"
      panel={<p className="muted pad">Choose the branch with the changes and the branch it should merge into.</p>}
    >
      {pulls.state === 'error' ? (
        <ErrorPanel error={pulls.error} signInUrl={null} />
      ) : pulls.state === 'loading' ? (
        <section className="pane pane-empty" aria-busy="true">Loading...</section>
      ) : !writable ? (
        <section className="pane pane-empty">
          <p className="muted">Merge requests cannot be opened here: sign in with GitHub, and writes must be switched on for this repository.</p>
          <Link className="button-quiet" to={pullsUrl(owner, repo)}>All merge requests</Link>
        </section>
      ) : (
        <section className="pane new-pull">
          <header className="pane-head">
            <div className="pane-title">
              <span className="pane-label">Merge request</span>
              <h1>New merge request</h1>
            </div>
            <div className="pane-tools">
              <Link className="button-quiet" to={pullsUrl(owner, repo)}>All merge requests</Link>
            </div>
          </header>
          <fieldset className="new-pull-fields" disabled={locked}>
            <div className="new-pull-branches">
              <label>
                Merge
                <select value={form.head} onChange={(e) => set({ head: e.target.value })} aria-label="Branch with the changes">
                  <option value="">choose a branch</option>
                  {names.filter((b) => b !== base).map((b) => <option key={b} value={b}>{b}</option>)}
                </select>
              </label>
              <label>
                into
                <select value={base} onChange={(e) => set({ base: e.target.value })} aria-label="Branch to merge into">
                  {names.map((b) => <option key={b} value={b}>{b}</option>)}
                </select>
              </label>
              {form.head && !same && (
                // A compare of the two branch tips; the merge request itself is measured from where
                // the branch started, which the review page shows once it exists.
                <Link to={compareUrl(owner, repo, base, form.head)}>Compare the branches</Link>
              )}
            </div>
            {same && <p className="notice" role="note">Choose two different branches.</p>}
            {branches.state === 'ok' && branches.value.truncated && <p className="muted">Only the first {names.length} branches are listed.</p>}
            <input className="new-pull-title" value={form.title} onChange={(e) => set({ title: e.target.value })} placeholder="Title" aria-label="Title" maxLength={256} />
            <textarea className="comment-input new-pull-body" value={form.body} onChange={(e) => set({ body: e.target.value })} placeholder="Describe the change" aria-label="Description" />
            {template.state === 'ok' && template.value.path && <p className="muted">The description starts from {template.value.path}.</p>}
            <label className="new-pull-draft">
              <input type="checkbox" checked={form.draft} onChange={(e) => set({ draft: e.target.checked })} /> Open as a draft, not yet ready for review
            </label>
          </fieldset>
          {note && <p className="notice comment-note" role="status">{note}</p>}
          <div className="comment-actions">
            {locked && !busy && (
              <button type="button" className="button-quiet" onClick={() => { set({ opKey: null }); setNote(null); }} title="Change the form again; a new submission follows">
                Edit
              </button>
            )}
            <button type="button" className="button" onClick={submit} disabled={!ready}>
              {busy ? 'Opening...' : locked ? 'Try again' : form.draft ? 'Open as draft' : 'Open merge request'}
            </button>
          </div>
        </section>
      )}
    </Workbench>
  );
}
