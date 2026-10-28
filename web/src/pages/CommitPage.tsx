import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { CommitDetail, CommitFileEntry, CommitObject, Layout, Me, PaneView, TreeNode } from '../api';
import { api } from '../api';
import { ChangeSectionHead, changeMark, changeTone } from '../components/ChangeSectionHead';
import { CommentCard, anchorOf } from '../components/Conversation';
import { FragmentHost } from '../components/FragmentHost';
import { safeHref } from '../fragment';
import { ErrorPanel } from '../components/Pane';
import { describe, newOpKey, readDraft, releasesKey, writeDraft, type Draft } from '../components/ReviewWrite';
import { RepoLayout } from '../components/RepoLayout';
import { TreePanel } from '../components/TreePanel';
import { Avatar, Blankslate, Icon, Loading, RelativeTime } from '../components/ui';
import { blobUrl, commitUrl, compareUrl, treeUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

/**
 * /owner/repo/commit/sha: GitHub's commit page with the desktop's commit card (FINISH.md Step 8)
 * — the message and its body, who wrote and who committed it, the parents, the stats, "Browse
 * files"; the changed objects as the marked project tree beside one section per OBJECT the
 * commit touched (2026-10-27: it was one per layer file, headed by a raw path), each compared
 * from the first parent as the desktop compares a commit, loaded as it comes into view.
 */
export function CommitPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', sha = '' } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const commit = useAsync((s) => api.commit(owner, repo, sha, s), [owner, repo, sha]);
  const parent = commit.state === 'ok' ? commit.value.parents[0] ?? null : null;
  // The marked tree from the first parent: what the commit changed, where it lives.
  const tree = useAsync((s) => (parent ? api.tree(owner, repo, sha, s, parent, true) : new Promise<never>(() => {})), [owner, repo, sha, parent]);
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
  const path = search.get('path');
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  usePageTitle(commit.state === 'ok' ? commit.value.headline : `Commit ${sha.slice(0, 7)}`, `${owner}/${repo}`);

  function setParam(key: string, value: string, fallback: string) {
    const next = new URLSearchParams(search);
    if (value === fallback) {
      next.delete(key);
    } else {
      next.set(key, value);
    }
    setSearch(next);
  }

  // A tree pick scrolls to the section keyed by the ROW's path, which is the object's key on the
  // server (the same tree builder names both). Until 2026-10-27 the target was the primary file,
  // and an object whose native XML alone had changed had no section by that id.
  const select = (node: TreeNode) => {
    const next = new URLSearchParams(search);
    next.set('path', node.path);
    setSearch(next);
    document.getElementById(`file-${node.path}`)?.scrollIntoView?.({ block: 'start' });
  };

  if (commit.state === 'error') {
    return (
      <RepoLayout me={me} owner={owner} repo={repo} nav="commits" refName={defaultBranch}>
        <ErrorPanel error={commit.error} signInUrl={signIn} />
      </RepoLayout>
    );
  }
  if (commit.state === 'loading') {
    return (
      <RepoLayout me={me} owner={owner} repo={repo} nav="commits" refName={defaultBranch}>
        <Loading what="Reading the commit…" />
      </RepoLayout>
    );
  }
  const c = commit.value;
  // Without the grouping (the project trees could not be read), the raw files are drawn as
  // plain sections under a notice saying why — the commit is still there to read.
  const sections: CommitObject[] = c.objectsUnavailable ? c.files.map(asPlain) : c.objects;
  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="commits" refName={defaultBranch} crumb={`Commit ${c.sha.slice(0, 7)}`} wide>
      <CommitCard owner={owner} repo={repo} commit={c} />
      {!parent && <Blankslate icon="git-commit" heading="The first commit">It has no parent to compare with: browse the files instead.</Blankslate>}
      {parent && (
        <div className="files-view">
          <aside className="files-tree">
            {tree.state === 'ok' && <TreePanel tree={tree.value} selected={path} onSelect={select} placeholder="Filter changed objects" comparing />}
            {tree.state === 'loading' && <Loading what="Listing what changed…" />}
            {tree.state === 'error' && <p className="text-danger pad">{tree.error.message}</p>}
          </aside>
          <div className="files-main">
            {c.files.length === 0 && <Blankslate icon="diff" heading="No files changed">This commit changes nothing GitHub lists.</Blankslate>}
            {c.objectsUnavailable && <p className="notice" role="note">{c.objectsUnavailable} The files are listed as GitHub lists them.</p>}
            {sections.map((o) => (
              <CommitObjectSection key={o.key} owner={owner} repo={repo} base={parent} head={c.sha} object={o} view={view} layout={layout}
                onView={(v) => setParam('view', v, 'rendered')} onLayout={(l) => setParam('layout', l, 'unified')}
                onOpenPath={(p) => navigate(blobUrl(owner, repo, c.sha, p))} signInUrl={signIn}
                active={path !== null && (o.key === path || o.layers.some((l) => l.path === path))} />
            ))}
            {c.filesTruncated && <p className="text-muted">GitHub lists at most 300 files for a commit; the rest are in <Link to={compareUrl(owner, repo, parent, c.sha)}>the comparison</Link>.</p>}
          </div>
        </div>
      )}
      <CommitComments owner={owner} repo={repo} sha={c.sha} onOpenPath={(p) => navigate(blobUrl(owner, repo, c.sha, p))} />
    </RepoLayout>
  );
}

/**
 * GitHub's commit comments (FINISH.md Step 3b), under the files as github.com draws them: each
 * as a comment card, one GitHub placed on a line naming its file and line; and a box to add one
 * on the commit as a whole when writes are on here and the person is signed in. The write goes
 * with an operation key like every other, with no snapshot: a commit is its own revision.
 */
function CommitComments({ owner, repo, sha, onOpenPath }: { owner: string; repo: string; sha: string; onOpenPath: (p: string) => void }) {
  const [generation, setGeneration] = useState(0);
  const comments = useAsync((s) => api.commitComments(owner, repo, sha, s), [owner, repo, sha, generation]);
  if (comments.state === 'loading') {
    return <section className="commit-comments" aria-label="Comments"><Loading what="Reading the comments…" /></section>;
  }
  if (comments.state === 'error') {
    return <section className="commit-comments" aria-label="Comments"><p className="text-danger">{comments.error.message}</p></section>;
  }
  const { writable, comments: list, truncated } = comments.value;
  return (
    <section className="commit-comments" aria-label="Comments">
      <h2 className="panel-title">{list.length} comment{list.length === 1 ? '' : 's'} on this commit</h2>
      {truncated && <p className="notice" role="note">The first {list.length} are shown; the rest are on GitHub.</p>}
      {list.map((c) => (
        <CommentCard key={c.id} author={c.author} avatar={c.avatar} when={c.when} url={c.url} anchor={anchorOf(c.url)}
          verb={c.path ? <>commented on <Link to={blobUrl(owner, repo, sha, c.path)}>{c.pathLabel}{c.line !== null ? `:L${c.line}` : ''}</Link></> : 'commented'}>
          <FragmentHost html={c.html} path="" onOpenPath={onOpenPath} line={null} onLine={() => {}} />
        </CommentCard>
      ))}
      {/* Keyed by the commit, as CommentBox is by its slot: a box kept across commits would save
          one commit's words under the next one's slot (ReviewWrite's "Pump draft overwrote Valve's"). */}
      {writable && <CommitCommentBox key={`${owner}/${repo}:${sha}`} owner={owner} repo={repo} sha={sha} onDone={() => setGeneration((g) => g + 1)} />}
    </section>
  );
}

/** The box for a comment on the commit: the same key rules as every comment box, without a snapshot. */
function CommitCommentBox({ owner, repo, sha, onDone }: { owner: string; repo: string; sha: string; onDone: () => void }) {
  // The words and the held key are kept where every comment box keeps its draft (ReviewWrite's
  // session storage), not in this component's state: the key is held from the first submission
  // to the outcome so a retry is the same operation to the server, and a reload or a dropped
  // connection mid-post must find that key again — held in React state only, as the first
  // version did, the reload minted a new one and the retry was a second comment (review,
  // 2026-10-24).
  const slot = `commit:${owner}/${repo}:${sha}:comment`;
  const [draft, setDraft] = useState<Draft>(() => readDraft(slot));
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  useEffect(() => writeDraft(slot, draft), [slot, draft]);
  const locked = draft.opKey !== null;
  async function submit() {
    if (!draft.body.trim() || busy) {
      return;
    }
    const key = draft.opKey ?? newOpKey();
    setDraft({ body: draft.body, opKey: key });
    setBusy(true);
    setNote(null);
    const answer = await api.addCommitComment(owner, repo, sha, { opKey: key, body: draft.body });
    setBusy(false);
    if (answer.outcome === 'done') {
      writeDraft(slot, null);
      setDraft({ body: '', opKey: null });
      onDone();
      return;
    }
    if (releasesKey(answer, draft.opKey !== null)) {
      setDraft((d) => ({ ...d, opKey: null }));
    }
    setNote(describe(answer));
  }
  return (
    <div className="commit-comment-new comment-box">
      {/* Once submitted the text is the key's payload: a retry with other words is another write,
          which the server refuses. "Edit text" starts over with a new key, as CommentBox's does. */}
      <textarea value={draft.body} placeholder="Leave a comment on this commit" aria-label="Leave a comment on this commit" readOnly={locked} rows={4} onChange={(e) => setDraft({ body: e.target.value, opKey: null })} />
      {note && <p className="notice comment-note" role="status">{note}</p>}
      <div className="comment-actions">
        {locked && !busy && (
          <button type="button" className="button-quiet" onClick={() => { setDraft({ ...draft, opKey: null }); setNote(null); }} title="Edit the text again; a new submission follows">
            Edit text
          </button>
        )}
        <button type="button" className="btn btn-primary" onClick={submit} disabled={busy || !draft.body.trim()}>{locked && !busy ? 'Try again' : 'Comment on this commit'}</button>
      </div>
    </div>
  );
}

/** The desktop's commit card (CommitHeading): the message, the byline, the stats, the parents, the ways on. */
function CommitCard({ owner, repo, commit: c }: { owner: string; repo: string; commit: CommitDetail }) {
  return (
    <div className="commit-card box">
      <div className="commit-card-head">
        <div className="commit-card-text">
          <h1 className="commit-card-title">{c.headline}</h1>
          {c.body && <pre className="commit-body">{c.body}</pre>}
        </div>
        <div className="commit-card-actions">
          <Link to={treeUrl(owner, repo, c.sha)} className="btn btn-small">Browse files</Link>
          {safeHref(c.url) && <a href={safeHref(c.url)!} target="_blank" rel="noopener noreferrer" className="btn btn-small" title="On GitHub"><Icon name="link" className="btn-icon" /></a>}
        </div>
      </div>
      <div className="commit-card-foot">
        <span className="commit-byline">
          <Avatar login={c.login ?? c.author} url={c.avatar} size={20} />
          <span className="text-bold">{c.login ?? c.author}</span>
          {c.login && c.login !== c.author && <span className="text-muted">({c.author})</span>}
          <span>authored <RelativeTime when={c.when} /></span>
          {c.committer && <span className="text-muted">· committed by {c.committer}{c.committed && <> <RelativeTime when={c.committed} /></>}</span>}
          {c.verified === true && <span className="verified-badge" title="This commit was signed with a verified signature">Verified</span>}
        </span>
        <span className="commit-card-meta">
          <span className="diffstat"><span className="diffstat-add">+{c.additions}</span> <span className="diffstat-del">−{c.deletions}</span></span>
          <span className="text-muted">{c.files.length} file{c.files.length === 1 ? '' : 's'} changed</span>
          <span className="text-muted">
            {c.parents.length === 0 ? 'no parent' : `${c.parents.length === 1 ? 'parent' : 'parents'} `}
            {c.parents.map((p, i) => <span key={p}>{i > 0 && ' + '}<Link to={commitUrl(owner, repo, p)}><code>{p.slice(0, 7)}</code></Link></span>)}
          </span>
          <code className="commit-sha" title={c.sha}>commit {c.sha.slice(0, 7)}</code>
        </span>
      </div>
    </div>
  );
}

/** A raw file as a section of its own: what the page draws when the server could not group. */
function asPlain(f: CommitFileEntry): CommitObject {
  return { key: f.path, kind: 'file', name: f.label.slice(f.label.lastIndexOf('/') + 1), label: f.label, path: f.path, from: f.previousPath, type: null, typeName: null, toolchain: '', status: changeTone(f.status), additions: f.additions, deletions: f.deletions, layers: [{ ...f, changed: true }] };
}

interface CommitObjectSectionProps {
  owner: string;
  repo: string;
  base: string;
  head: string;
  object: CommitObject;
  view: PaneView;
  layout: Layout;
  onView: (v: PaneView) => void;
  onLayout: (l: Layout) => void;
  onOpenPath: (p: string) => void;
  signInUrl: string | null;
  active: boolean;
}

/**
 * One object the commit touched — or one file outside any object — under the shared header, with
 * a chip per layer when it has several (the pane's chips, FINISH.md Step 8): the drawn layer is the
 * one the server picked (the readable file when the commit touched it), and a chip switches it.
 * Drawn as the compare view draws it from the first parent, once the section nears the viewport.
 */
function CommitObjectSection({ owner, repo, base, head, object: o, view, layout, onView, onLayout, onOpenPath, signInUrl, active }: CommitObjectSectionProps) {
  const section = useRef<HTMLElement>(null);
  const [near, setNear] = useState(active);
  const [folded, setFolded] = useState(false);
  const [full, setFull] = useState(false);
  const [shown, setShown] = useState(o.path);
  const layer = o.layers.find((l) => l.path === shown) ?? o.layers[0];
  useEffect(() => {
    if (near || !section.current || typeof IntersectionObserver === 'undefined') {
      setNear(true);
      return;
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        setNear(true);
        observer.disconnect();
      }
    }, { rootMargin: '400px 0px' });
    observer.observe(section.current);
    return () => observer.disconnect();
  }, [near]);
  // A renamed layer is compared from its old path (review, 2026-10-25): without it the server has
  // nothing on the older side and draws the whole file as added, hiding what the edit removed.
  const from = layer?.previousPath ?? undefined;
  const result = useAsync((s) => (near && !folded && layer ? api.compareView(owner, repo, base, head, layer.path, view, layout, from, s, full) : new Promise<never>(() => {})), [near, folded, owner, repo, base, head, layer?.path, from, view, layout, full]);
  return (
    <section ref={section} id={`file-${o.key}`} className={`file-review${folded ? ' is-folded' : ''}${active ? ' is-active' : ''}`} data-file={o.key} data-kind={o.kind}>
      <ChangeSectionHead open={!folded} onToggle={() => setFolded(!folded)} status={o.status} name={o.name} label={o.label}
        typeName={o.typeName} toolchain={o.toolchain} renamedFrom={layer?.previousLabel} additions={o.additions} deletions={o.deletions}
        view={view} onView={onView} layout={layout} onLayout={onLayout} full={full} onFull={setFull} />
      {!folded && (
        <div className="file-review-body">
          {o.layers.length > 1 && (
            <div className="layers" aria-label="Files of this object">
              {o.layers.map((l) => (
                <button key={l.path} type="button" className={`chip${l.path === shown ? ' is-on' : ''}`} onClick={() => setShown(l.path)} title={l.label}>
                  {l.label.slice(l.label.lastIndexOf('/') + 1)}
                  {l.changed && <span className={`change change-${changeTone(l.status)}`} aria-label={changeTone(l.status)}> {changeMark[l.status] ?? ''}</span>}
                </button>
              ))}
            </div>
          )}
          {result.state === 'loading' && <Loading what="Drawing the file…" />}
          {result.state === 'error' && <ErrorPanel error={result.error} signInUrl={signInUrl} />}
          {result.state === 'ok' && (
            <>
              {result.value.notices.map((n) => <p key={n} className="notice" role="note">{n}</p>)}
              <FragmentHost html={result.value.html} path={result.value.path} onOpenPath={onOpenPath} line={null} onLine={() => {}} onUnfold={() => setFull(true)} />
            </>
          )}
        </div>
      )}
    </section>
  );
}
