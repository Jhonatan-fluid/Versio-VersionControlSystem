import { useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Branch, Layout, Me, PaneView, TreeNode } from '../api';
import { api } from '../api';
import { ErrorPanel, Pane } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { TreePanel } from '../components/TreePanel';
import { Blankslate, Button, Icon, Loading } from '../components/ui';
import { blobUrl, commitsUrl, compareUrl, treeUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

/** The line in the URL's hash, as GitHub writes it: #L12. */
export function lineFromHash(hash: string): number | null {
  const m = /^#L(\d+)$/.exec(hash);
  return m ? Number(m[1]) : null;
}

/**
 * GitHub's branch picker: the current ref as a button, the branches as a filterable list. A
 * commit SHA (from "Browse files" on a commit) shows as its short form, since it is no branch.
 */
export function BranchPicker({ value, branches, truncated, onPick }: { value: string; branches: Branch[]; truncated: boolean; onPick: (ref: string) => void }) {
  const known = branches.find((b) => b.name === value);
  const shown = known?.label ?? (value.length === 40 ? value.slice(0, 7) : value);
  return (
    <label className="branch">
      <Icon name="git-branch" className="text-muted" />
      <span className="visually-hidden">Branch</span>
      <select className="form-control" value={value} onChange={(e) => onPick(e.target.value)} aria-label="Branch">
        {!known && <option value={value}>{shown}</option>}
        {branches.map((b) => (
          <option key={b.name} value={b.name}>{b.label}</option>
        ))}
        {truncated && (
          <option disabled value="">
            {`Only the first ${branches.length} branches are listed`}
          </option>
        )}
      </select>
    </label>
  );
}

/**
 * /owner/repo, /owner/repo/tree/ref and /owner/repo/blob/ref/path: the Code view. GitHub's
 * shape — the branch picker and the latest-commit line over the content, the file tree in a
 * sidebar — with Versio's project tree in the sidebar and the desktop's rendering in the pane.
 */
export function RepoPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', ref: refParam, '*': pathParam } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const [treeOpen, setTreeOpen] = useState(false);

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const ref = refParam ?? (info.state === 'ok' ? info.value.defaultBranch : null);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const tree = useAsync((s) => (ref ? api.tree(owner, repo, ref, s) : new Promise<never>(() => {})), [owner, repo, ref]);
  const latest = useAsync((s) => (ref ? api.history(owner, repo, ref, undefined, s) : new Promise<never>(() => {})), [owner, repo, ref]);

  const readme = tree.state === 'ok' ? tree.value.nodes.find((n) => n.kind === 'file' && /^readme(\.md|\.markdown)?$/i.test(n.name))?.path ?? null : null;
  const path = pathParam || readme;
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
  const line = lineFromHash(location.hash);
  usePageTitle(pathParam ? `${pathParam.slice(pathParam.lastIndexOf('/') + 1)} at ${ref ?? ''}` : null, `${owner}/${repo}`);

  const pane = useAsync(
    (s) => (ref && path ? api.view(owner, repo, ref, path, view, layout, s) : new Promise<never>(() => {})),
    [owner, repo, ref, path, view, layout],
  );

  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;

  function open(target: string, keep = true) {
    if (!ref) {
      return;
    }
    setTreeOpen(false);
    navigate(blobUrl(owner, repo, ref, target, keep ? search : undefined));
  }

  function select(node: TreeNode) {
    open(node.primary ?? node.files[0] ?? node.path);
  }

  function setParam(key: string, value: string, fallback: string) {
    const next = new URLSearchParams(search);
    if (value === fallback) {
      next.delete(key);
    } else {
      next.set(key, value);
    }
    setSearch(next);
  }

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const refName = ref ?? defaultBranch;
  const head = latest.state === 'ok' ? latest.value[0] ?? null : null;

  if (info.state === 'error') {
    return (
      <RepoLayout me={me} owner={owner} repo={repo} nav="code">
        <ErrorPanel error={info.error} signInUrl={signIn} />
      </RepoLayout>
    );
  }

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="code" refName={refName} isPrivate={info.state === 'ok' ? info.value.private : undefined} wide>
      <div className="code-toolbar">
        <Button className="tree-toggle-button" leadingIcon="three-bars" size="small" onClick={() => setTreeOpen(!treeOpen)} aria-label="Show or hide the tree" />
        <BranchPicker
          value={refName}
          branches={branches.state === 'ok' ? branches.value.branches : []}
          truncated={branches.state === 'ok' && branches.value.truncated}
          onPick={(r) => navigate(pathParam ? blobUrl(owner, repo, r, pathParam, search) : treeUrl(owner, repo, r))}
        />
        <Link to={commitsUrl(owner, repo, refName)} className="btn btn-small" title="The commits of this branch">
          <Icon name="history" className="btn-icon" />
          <span className="btn-text">Commits</span>
        </Link>
        {refName !== defaultBranch && (
          <Link to={compareUrl(owner, repo, defaultBranch, refName)} className="btn btn-small" title={`What this branch changes against ${defaultBranch}`}>
            <Icon name="diff" className="btn-icon" />
            <span className="btn-text">Compare</span>
          </Link>
        )}
        {head && (
          // GitHub's latest-commit line over the file list: who changed this branch last and what they said.
          <span className="text-muted text-small" style={{ marginLeft: 'auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            <span className="text-bold">{head.author}</span> {head.message.split('\n')[0]}{' '}
            <Link to={commitsUrl(owner, repo, refName)}><code>{head.sha.slice(0, 7)}</code></Link>
          </span>
        )}
      </div>
      <div className={`code-view${treeOpen ? ' sidebar-open' : ''}`}>
        <aside className="code-sidebar">
          <div className="code-sidebar-head">
            <Icon name="file-directory-fill" className="tree-icon tree-icon-folder" />
            <span>Files</span>
          </div>
          <div className="code-sidebar-body">
            {tree.state === 'ok' && <TreePanel tree={tree.value} selected={path} onSelect={select} />}
            {tree.state === 'loading' && <Loading what="Reading the tree…" />}
            {tree.state === 'error' && <p className="text-danger pad">{tree.error.message}</p>}
          </div>
        </aside>
        <div className="code-main">
          {path ? (
            <Pane
              result={pane}
              view={view}
              layout={layout}
              onView={(v) => setParam('view', v, 'rendered')}
              onLayout={(l) => setParam('layout', l, 'unified')}
              onLayer={(layer) => open(layer)}
              onOpenPath={(p) => open(p)}
              line={line}
              onLine={(n) => navigate({ hash: `L${n}`, search: search.toString() }, { replace: true })}
              comparing={false}
              signInUrl={signIn}
            />
          ) : tree.state === 'ok' ? (
            <Blankslate icon="file" heading="Pick an object in the tree">Every POU, data type, variable list, network and document opens here, drawn as the desktop draws it.</Blankslate>
          ) : (
            <Loading />
          )}
        </div>
      </div>
    </RepoLayout>
  );
}
