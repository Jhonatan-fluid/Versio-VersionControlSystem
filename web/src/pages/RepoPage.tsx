import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Layout, Me, PaneView, TreeNode } from '../api';
import { api } from '../api';
import { Header } from '../components/Header';
import { ErrorPanel, Pane } from '../components/Pane';
import { TreePanel } from '../components/TreePanel';
import { Workbench } from '../components/Workbench';
import { blobUrl } from '../routes';
import { useAsync } from '../useAsync';

/** The line in the URL's hash, as GitHub writes it: #L12. */
export function lineFromHash(hash: string): number | null {
  const m = /^#L(\d+)$/.exec(hash);
  return m ? Number(m[1]) : null;
}

/** /owner/repo, /owner/repo/tree/ref and /owner/repo/blob/ref/path: the Project panel beside the pane. */
export function RepoPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', ref: refParam, '*': pathParam } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const ref = refParam ?? (info.state === 'ok' ? info.value.defaultBranch : null);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const tree = useAsync((s) => (ref ? api.tree(owner, repo, ref, s) : new Promise<never>(() => {})), [owner, repo, ref]);

  const readme = tree.state === 'ok' ? tree.value.nodes.find((n) => n.kind === 'file' && /^readme(\.md|\.markdown)?$/i.test(n.name))?.path ?? null : null;
  const path = pathParam || readme;
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
  const line = lineFromHash(location.hash);

  const pane = useAsync(
    (s) => (ref && path ? api.view(owner, repo, ref, path, view, layout, s) : new Promise<never>(() => {})),
    [owner, repo, ref, path, view, layout],
  );

  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;

  function open(target: string, keep = true) {
    if (!ref) {
      return;
    }
    navigate(blobUrl(owner, repo, ref, target, keep ? search : undefined));
  }

  function select(node: TreeNode) {
    const target = node.primary ?? node.files[0] ?? node.path;
    open(target);
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

  if (info.state === 'error') {
    return (
      <div className="app">
        <Header me={me} />
        <ErrorPanel error={info.error} signInUrl={signIn} />
      </div>
    );
  }

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  return (
    <Workbench
      me={me}
      owner={owner}
      repo={repo}
      refName={ref ?? defaultBranch}
      defaultBranch={defaultBranch}
      branches={branches.state === 'ok' ? branches.value.branches : []}
      branchesTruncated={branches.state === 'ok' && branches.value.truncated}
      onRef={(r) => navigate(path ? blobUrl(owner, repo, r, path, search) : `/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/tree/${encodeURIComponent(r)}`)}
      rail="project"
      panelTitle="Project"
      panel={
        <>
          {tree.state === 'ok' && <TreePanel tree={tree.value} selected={path} onSelect={select} />}
          {tree.state === 'loading' && <p className="muted pad">Loading the tree...</p>}
          {tree.state === 'error' && <p className="error-text pad">{tree.error.message}</p>}
        </>
      }
    >
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
      ) : (
        <section className="pane pane-empty">
          <p className="muted">Pick an object in the tree.</p>
        </section>
      )}
    </Workbench>
  );
}
