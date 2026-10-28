import { useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Branch, Layout, Me, PaneView, TreeNode } from '../api';
import { api, rawUrl } from '../api';
import { ErrorPanel, Pane } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { TreePanel } from '../components/TreePanel';
import { Blankslate, Button, Icon, Loading } from '../components/ui';
import { blobUrl, branchesUrl, commitUrl, commitsUrl, compareUrl, tagsUrl, treeUrl } from '../routes';
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
/** The folder or project node at `path`, or null when the path is not one (a file, an object, nothing). */
function findFolder(nodes: TreeNode[], path: string): TreeNode | null {
  for (const node of nodes) {
    if (node.path === path) {
      return node.kind === 'folder' || node.kind === 'project' ? node : null;
    }
    if (path.startsWith(node.path + '/') && node.children.length > 0) {
      const below = findFolder(node.children, path);
      if (below) {
        return below;
      }
    }
  }
  return null;
}

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
  // A path that names a folder of the tree (a pasted /tree/ref/folder link) lists the folder.
  const folderNode = tree.state === 'ok' && pathParam ? findFolder(tree.value.nodes, pathParam) : null;
  const path = folderNode ? null : pathParam || readme;
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
  const line = lineFromHash(location.hash);
  usePageTitle(pathParam ? `${pathParam.slice(pathParam.lastIndexOf('/') + 1)} at ${ref ?? ''}` : null, `${owner}/${repo}`);

  const pane = useAsync(
    (s) => (ref && path ? api.view(owner, repo, ref, path, view, layout, s) : new Promise<never>(() => {})),
    [owner, repo, ref, path, view, layout],
  );
  // "Used by" (FINISH.md Step 3c), read beside the drawing: the objects naming this one at this
  // version. Its own request, so a slow search never holds the drawing back.
  const usages = useAsync(
    (s) => (ref && path ? api.usages(owner, repo, ref, path, s) : new Promise<never>(() => {})),
    [owner, repo, ref, path],
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
  const head = latest.state === 'ok' ? latest.value.commits[0] ?? null : null;

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
        {/* GitHub's "N Branches · Tags" beside the picker (Step 8). */}
        <Link to={branchesUrl(owner, repo)} className="code-refs-link" title="All branches">
          <Icon name="git-branch" /> {branches.state === 'ok' ? `${branches.value.branches.length}${branches.value.truncated ? '+' : ''} ` : ''}Branches
        </Link>
        <Link to={tagsUrl(owner, repo)} className="code-refs-link" title="All tags"><Icon name="tag" /> Tags</Link>
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
            <Link to={commitUrl(owner, repo, head.sha)}><code>{head.sha.slice(0, 7)}</code></Link>
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
          {folderNode ? (
            // GitHub's /tree/ref/folder: the folder's own listing, not a drawing of a directory
            // (which the server has no view for; the page asked it anyway until the review of 2026-10-24).
            <div className="list-box" aria-label={`Files in ${folderNode.label}`}>
              <div className="list-row text-bold"><Icon name="file-directory-fill" className="tree-icon tree-icon-folder" /> {folderNode.label}</div>
              {folderNode.children.map((child) => (
                <div key={child.path} className="list-row">
                  {child.kind === 'folder' || child.kind === 'project'
                    ? <Link to={treeUrl(owner, repo, refName, child.path)}><Icon name="file-directory-fill" className="tree-icon tree-icon-folder" /> {child.name}</Link>
                    : <button type="button" className="link-button" onClick={() => select(child)}>{child.name}</button>}
                  {child.typeName && <span className="text-muted text-small"> {child.typeName}</span>}
                </div>
              ))}
              {folderNode.children.length === 0 && <p className="text-muted pad">Nothing in this folder.</p>}
            </div>
          ) : path ? (
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
              pageUrl={ref && path ? rawUrl(owner, repo, ref, path) : null}
              usages={usages}
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
