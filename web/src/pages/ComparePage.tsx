import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Layout, Me, PaneView, TreeNode } from '../api';
import { api } from '../api';
import { ErrorPanel, Pane } from '../components/Pane';
import { PeekPane } from '../components/PeekPane';
import { RepoLayout } from '../components/RepoLayout';
import { TreePanel } from '../components/TreePanel';
import { Blankslate, Icon, Loading } from '../components/ui';
import { compareUrl, newPullUrl, parseRange } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';
import { lineFromHash } from './RepoPage';

/**
 * /owner/repo/compare/base...head: GitHub's compare page — the two refs over the changed
 * files, a way to open a pull request between them — with every file drawn as the desktop
 * compares it. What changed is the project tree marked as the desktop's Project panel marks a
 * diff (FINISH.md Step 8): A/M/D on each object, roll-ups on the folders, deleted objects struck,
 * only the changed ones listed; a sidebar beside the drawing, as on the review page.
 */
export function ComparePage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', range = '' } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const parsed = parseRange(range);
  const base = parsed?.base ?? '';
  const head = parsed?.head ?? '';
  const path = search.get('path');
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';
  const full = search.get('full') === '1';
  usePageTitle(`Comparing ${base}...${head}`, `${owner}/${repo}`);

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const pulls = useAsync((s) => api.pulls(owner, repo, s), [owner, repo]);
  const comparison = useAsync((s) => (parsed ? api.compare(owner, repo, base, head, s) : Promise.reject(new Error('Pick two versions to compare.'))), [owner, repo, base, head]);
  const tree = useAsync((s) => (parsed ? api.tree(owner, repo, head, s, base, true) : new Promise<never>(() => {})), [owner, repo, base, head]);
  const pane = useAsync(
    (s) => (parsed && path ? api.compareView(owner, repo, base, head, path, view, layout, s, full) : new Promise<never>(() => {})),
    [owner, repo, base, head, path, view, layout, full],
  );

  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  // A link to something outside the comparison opens a peek at the newer version beside it
  // (FINISH.md C2, "stay in context"); one to a changed file opens that file's comparison.
  const [peek, setPeek] = useState<string | null>(null);
  useEffect(() => setPeek(null), [base, head, path]);
  const inComparison = (p: string) => comparison.state === 'ok' && comparison.value.files.some((f) => f.path === p);
  const names = branches.state === 'ok' ? branches.value.branches.map((b) => b.name) : [];
  const bothBranches = names.includes(base) && names.includes(head);
  const writable = pulls.state === 'ok' && pulls.value.writable;

  function go(nextBase: string, nextHead: string, nextPath: string | null) {
    navigate(compareUrl(owner, repo, nextBase, nextHead, nextPath ?? undefined, search));
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

  const picker = (value: string, onChange: (v: string) => void, label: string) => (
    <label className="branch">
      <span className="text-muted">{label}</span>
      <select className="form-control" value={value} onChange={(e) => onChange(e.target.value)} aria-label={label}>
        {!names.includes(value) && value && <option value={value}>{value.length === 40 ? value.slice(0, 7) : value}</option>}
        {names.map((n) => (
          <option key={n} value={n}>{n}</option>
        ))}
      </select>
    </label>
  );

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="code" crumb="Compare" refName={head || defaultBranch} wide>
      <div className="pr-head">
        <h1 className="pr-title" style={{ fontSize: 24 }}>Comparing changes</h1>
        <p className="text-muted" style={{ margin: '4px 0 12px' }}>Choose two branches or commits to see what changed between them, drawn as the desktop draws it.</p>
        <div className="compare-pick box" style={{ background: 'var(--color-canvas-subtle)' }}>
          {picker(base, (b) => go(b, head, path), 'base')}
          <Icon name="arrow-right" className="text-muted" />
          {picker(head, (h) => go(base, h, path), 'compare')}
          {comparison.state === 'ok' && (
            <span className="text-muted">
              {comparison.value.total === 1 ? '1 file changed' : `${comparison.value.total} files changed`}
              {comparison.value.truncated && `, the first ${comparison.value.files.length} listed`}
            </span>
          )}
          {writable && bothBranches && base !== head && (
            <Link to={newPullUrl(owner, repo, base, head)} className="btn btn-primary btn-small" style={{ marginLeft: 'auto' }}>
              <Icon name="git-pull-request" className="btn-icon" />
              <span className="btn-text">Create pull request</span>
            </Link>
          )}
        </div>
      </div>
      {comparison.state === 'error' ? (
        <ErrorPanel error={comparison.error} signInUrl={signIn} />
      ) : (
        <div className="files-view">
          <aside className="files-tree">
            {(comparison.state === 'loading' || tree.state === 'loading') && <Loading what="Comparing…" />}
            {comparison.state === 'ok' && comparison.value.files.length === 0 && <p className="text-muted pad">These two versions are the same.</p>}
            {tree.state === 'ok' && comparison.state === 'ok' && comparison.value.files.length > 0 && (
              <TreePanel tree={tree.value} selected={path} onSelect={(node: TreeNode) => go(base, head, node.primary ?? node.files[0] ?? node.path)} placeholder="Filter changed objects" comparing />
            )}
            {tree.state === 'error' && <p className="text-danger pad">{tree.error.message}</p>}
          </aside>
          <div className="files-main">
            {path ? (
              <Pane
                result={pane}
                view={view}
                layout={layout}
                onView={(v) => setParam('view', v, 'rendered')}
                onLayout={(l) => setParam('layout', l, 'unified')}
                full={full}
                onFull={(on) => setParam('full', on ? '1' : '0', '0')}
                onLayer={(layer) => go(base, head, layer)}
                onOpenPath={(p) => (inComparison(p) ? go(base, head, p) : setPeek(p))}
                line={lineFromHash(location.hash)}
                onLine={(n) => navigate({ hash: `L${n}`, search: search.toString() }, { replace: true })}
                comparing
                signInUrl={signIn}
              />
            ) : (
              <Blankslate icon="diff" heading="Pick a changed file">Each one is drawn as a comparison: tables, networks and source, old against new.</Blankslate>
            )}
          </div>
        </div>
      )}
      {peek && <PeekPane key={peek} owner={owner} repo={repo} refName={head} path={peek} onClose={() => setPeek(null)} signInUrl={signIn} />}
    </RepoLayout>
  );
}
