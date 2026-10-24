import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Me, ReviewSnapshot } from '../api';
import { api } from '../api';
import { sanitize } from '../fragment';
import { ErrorPanel } from '../components/Pane';
import { describe, newOpKey, releasesKey } from '../components/ReviewWrite';
import { RepoLayout } from '../components/RepoLayout';
import { Blankslate, Button, IssueLabel, LinkButton, Loading } from '../components/ui';
import { compareUrl, pullUrl, pullsUrl } from '../routes';
import { usePageTitle } from '../title';
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
  /**
   * What GitHub's form sets beside the pull request (Step 7): each applied after it is opened, as
   * its own write under the new pull request's snapshot — GitHub's create takes none of them.
   */
  reviewers?: string;
  assignees?: string;
  labels?: string[];
  milestone?: number | null;
}

const never = () => new Promise<never>(() => {});

/** A comma-separated list of logins (or team slugs after a slash), as typed. */
function logins(text: string | undefined): string[] {
  return (text ?? '').split(/[,\s]+/).map((s) => s.trim()).filter(Boolean);
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
 * /owner/repo/pulls/new?base=&head=: open a pull request between two branches of this repository
 * (docs/web/PLAN.md, *Pull request review*, slice 5), in the person's own name, with the
 * repository's template, as a draft if they want. A fork's branch cannot be chosen here.
 *
 * The form survives a reload, and once submitted its words are the operation's: while the outcome
 * is unknown the only way on is the same request again, which the server recognises, never a
 * second pull request.
 */
export function NewPullPage({ me }: { me: Me | null }) {
  // One form per repository: kept mounted across a change of repository, its state would be saved
  // into the next repository's draft (the comment box's defect, found by review, 2026-10-20).
  const { owner = '', repo = '' } = useParams();
  return <NewPullForm key={`${owner}/${repo}`} me={me} />;
}

function NewPullForm({ me }: { me: Me | null }) {
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
  const fork = form.head.includes(':');
  const same = form.head !== '' && form.head === base;
  const ready = !busy && base !== '' && form.head !== '' && !same && form.title.trim() !== '';
  // GitHub's form shows the comparison under it: the files that would change, from this
  // repository's branches (a fork's cannot be compared here).
  const comparison = useAsync((s) => (base && form.head && !same && !fork ? api.compare(owner, repo, base, form.head, s) : never()), [owner, repo, base, form.head, same, fork]);
  const labels = useAsync((s) => (writable ? api.labels(owner, repo, s) : never()), [owner, repo, writable]);
  const milestones = useAsync((s) => (writable ? api.milestones(owner, repo, s) : never()), [owner, repo, writable]);
  const [preview, setPreview] = useState<string | null>(null);

  /**
   * What the form sets beside the pull request, applied after it is opened: each its own write
   * under the new pull request's snapshot, so a refused one is said and the rest still land. A
   * refusal here never means the pull request was not opened: the link to it is given either way.
   */
  async function applyExtras(number: number): Promise<string | null> {
    const reviewers = logins(form.reviewers);
    const people = reviewers.filter((r) => !r.includes('/'));
    const teams = reviewers.filter((r) => r.includes('/')).map((r) => r.slice(r.lastIndexOf('/') + 1));
    const assignees = logins(form.assignees);
    const wanted: { path: string; payload: Record<string, unknown>; what: string }[] = [];
    if (people.length + teams.length > 0) {
      wanted.push({ path: '/reviewers', payload: { add: people, addTeams: teams }, what: 'the reviewers' });
    }
    if (form.labels && form.labels.length > 0) {
      wanted.push({ path: '/labels', payload: { add: form.labels, remove: [] }, what: 'the labels' });
    }
    if (assignees.length > 0) {
      wanted.push({ path: '/assignees', payload: { add: assignees, remove: [] }, what: 'the assignees' });
    }
    if (form.milestone) {
      wanted.push({ path: '/milestone', payload: { number: form.milestone }, what: 'the milestone' });
    }
    if (wanted.length === 0) {
      return null;
    }
    let snap: ReviewSnapshot;
    try {
      snap = await api.review.snapshot(owner, repo, number);
    } catch (e) {
      return `the rest could not be set: ${(e as Error).message}`;
    }
    const failed: string[] = [];
    for (const w of wanted) {
      const answer = await api.review.write(owner, repo, snap, w.path, 'POST', { ...w.payload, opKey: newOpKey() });
      if (answer.outcome !== 'done') {
        failed.push(`${w.what} (${describe(answer)})`);
      }
    }
    return failed.length > 0 ? `not set: ${failed.join('; ')}` : null;
  }

  async function submit() {
    if (!ready) {
      return;
    }
    const opKey = form.opKey ?? newOpKey();
    setForm({ ...form, base, opKey });
    setBusy(true);
    setNote(null);
    const answer = await api.createPull(owner, repo, { opKey, base, head: form.head, title: form.title, body: form.body, draft: form.draft });
    if (answer.outcome === 'done' && answer.id) {
      const number = Number(answer.id);
      const left = await applyExtras(number);
      setBusy(false);
      writeForm(slot, null);
      if (left) {
        // Opened, with something beside it refused: said here with the link, never lost in the move.
        setForm({ base, head: '', title: '', body: '', draft: false, template: null, opKey: null });
        setNote(`Pull request #${number} is open; ${left}.`);
        setOpened(number);
        return;
      }
      navigate(pullUrl(owner, repo, number));
      return;
    }
    setBusy(false);
    if (releasesKey(answer, form.opKey !== null)) {
      setForm((f) => ({ ...f, opKey: null }));
    }
    setNote(describe(answer));
  }

  const [opened, setOpened] = useState<number | null>(null);
  const set = (change: Partial<Form>) => setForm((f) => ({ ...f, ...change }));

  usePageTitle('New pull request', `${owner}/${repo}`);

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="pulls" crumb="New pull request" refName={defaultBranch || undefined}>
      {pulls.state === 'error' ? (
        <ErrorPanel error={pulls.error} signInUrl={null} />
      ) : pulls.state === 'loading' ? (
        <Loading />
      ) : !writable ? (
        <Blankslate icon="git-pull-request" heading="Pull requests cannot be opened here" action={<LinkButton to={pullsUrl(owner, repo)}>All pull requests</LinkButton>}>
          Sign in with GitHub, and writes must be switched on for this repository.
        </Blankslate>
      ) : (
        <section className="new-pull">
          <div className="pr-head">
            <div className="pr-title-row">
              <h1 className="pr-title" style={{ fontSize: 24 }}>Open a pull request</h1>
              <div className="pr-title-actions">
                <LinkButton to={pullsUrl(owner, repo)} size="small">All pull requests</LinkButton>
              </div>
            </div>
            <p className="text-muted" style={{ margin: '4px 0 0' }}>Choose the branch with the changes and the branch it should merge into, then describe the change.</p>
          </div>
          <fieldset className="new-pull-fields" disabled={locked}>
            <div className="new-pull-branches">
              <label>
                Merge
                <select value={fork ? '' : form.head} onChange={(e) => set({ head: e.target.value })} aria-label="Branch with the changes">
                  <option value="">choose a branch</option>
                  {names.filter((b) => b !== base).map((b) => <option key={b} value={b}>{b}</option>)}
                </select>
              </label>
              <label className="new-pull-fork">
                or a fork's
                <input value={fork ? form.head : ''} onChange={(e) => set({ head: e.target.value })} placeholder="owner:branch" aria-label="A fork's branch, as owner:branch" />
              </label>
              <label>
                into
                <select value={base} onChange={(e) => set({ base: e.target.value })} aria-label="Branch to merge into">
                  {names.map((b) => <option key={b} value={b}>{b}</option>)}
                </select>
              </label>
              {form.head && !same && (
                // A compare of the two branch tips; the pull request itself is measured from where
                // the branch started, which the review page shows once it exists.
                <Link to={compareUrl(owner, repo, base, form.head)}>Compare the branches</Link>
              )}
            </div>
            {same && <p className="notice" role="note">Choose two different branches.</p>}
            {branches.state === 'ok' && branches.value.truncated && <p className="muted">Only the first {names.length} branches are listed.</p>}
            {/* The comparison under the form, as GitHub shows it: what would change, and the way to the full comparison. */}
            {comparison.state === 'ok' && (
              <div className="new-pull-compare box">
                <div className="box-header">
                  <span className="text-bold">{comparison.value.total} file{comparison.value.total === 1 ? '' : 's'} changed</span>
                  <Link to={compareUrl(owner, repo, base, form.head)}>See the comparison</Link>
                </div>
                <ul className="row-list new-pull-files">
                  {comparison.value.files.slice(0, 20).map((f) => (
                    <li key={f.path}><span className={`change-mark mark-${f.change}`}>{f.change[0]?.toUpperCase()}</span> <code>{f.label}</code>{f.object && <span className="text-muted"> {f.object}</span>}</li>
                  ))}
                  {comparison.value.files.length > 20 && <li className="text-muted">and {comparison.value.files.length - 20} more</li>}
                </ul>
              </div>
            )}
            {comparison.state === 'error' && <p className="muted">The comparison could not be read: {comparison.error.message}</p>}
            <input className="new-pull-title" value={form.title} onChange={(e) => set({ title: e.target.value })} placeholder="Title" aria-label="Title" maxLength={256} />
            <div className="comment-toolbar">
              <button type="button" className="btn btn-invisible btn-small" disabled={!form.body.trim()}
                onClick={async () => setPreview(preview === null ? await api.previewMarkdown(owner, repo, form.body) : null)}>
                {preview === null ? 'Preview' : 'Write'}
              </button>
            </div>
            {preview === null ? (
              <textarea className="comment-input new-pull-body" value={form.body} onChange={(e) => set({ body: e.target.value })} placeholder="Describe the change" aria-label="Description" />
            ) : (
              <div className="comment-preview fragment new-pull-preview" dangerouslySetInnerHTML={{ __html: sanitize(preview) }} />
            )}
            {template.state === 'ok' && template.value.path && <p className="muted">The description starts from {template.value.path}.</p>}
            <label className="new-pull-draft">
              <input type="checkbox" checked={form.draft} onChange={(e) => set({ draft: e.target.checked })} /> Open as a draft, not yet ready for review
            </label>
            {/* GitHub's right column of the form: set after the pull request is opened, each its own write. */}
            <div className="new-pull-extras">
              <label>Reviewers <input value={form.reviewers ?? ''} onChange={(e) => set({ reviewers: e.target.value })} placeholder="logins, or org/team" aria-label="Reviewers" /></label>
              <label>Assignees <input value={form.assignees ?? ''} onChange={(e) => set({ assignees: e.target.value })} placeholder="logins" aria-label="Assignees" /></label>
              {labels.state === 'ok' && labels.value.labels.length > 0 && (
                <fieldset className="new-pull-labels">
                  <legend>Labels</legend>
                  {labels.value.labels.map((l) => (
                    <label key={l.value} className="new-pull-label">
                      <input type="checkbox" checked={form.labels?.includes(l.value) ?? false}
                        onChange={(e) => set({ labels: e.target.checked ? [...(form.labels ?? []), l.value] : (form.labels ?? []).filter((x) => x !== l.value) })} />
                      <IssueLabel name={l.label} color={l.color} />
                    </label>
                  ))}
                </fieldset>
              )}
              {milestones.state === 'ok' && milestones.value.milestones.length > 0 && (
                <label>Milestone
                  <select value={form.milestone ?? ''} onChange={(e) => set({ milestone: e.target.value === '' ? null : Number(e.target.value) })} aria-label="Milestone">
                    <option value="">No milestone</option>
                    {milestones.value.milestones.map((m) => <option key={m.number} value={m.number}>{m.title}</option>)}
                  </select>
                </label>
              )}
            </div>
          </fieldset>
          {note && <p className="notice comment-note" role="status">{note}{opened !== null && <> <Link to={pullUrl(owner, repo, opened)}>Open #{opened}</Link></>}</p>}
          <div className="comment-actions">
            {locked && !busy && (
              <Button onClick={() => { set({ opKey: null }); setNote(null); }} title="Change the form again; a new submission follows">
                Edit
              </Button>
            )}
            <Button variant="primary" onClick={submit} disabled={!ready}>
              {busy ? 'Opening...' : locked ? 'Try again' : form.draft ? 'Create draft pull request' : 'Create pull request'}
            </Button>
          </div>
        </section>
      )}
    </RepoLayout>
  );
}
