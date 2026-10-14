import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { ChangedFile, Layout, Me, PaneView } from '../api';
import { api } from '../api';
import { ErrorPanel, Pane } from '../components/Pane';
import { Workbench } from '../components/Workbench';
import { blobUrl, compareUrl, parseRange } from '../routes';
import { useAsync } from '../useAsync';
import { lineFromHash } from './RepoPage';

const mark: Record<ChangedFile['change'], string> = { added: 'A', modified: 'M', deleted: 'D' };

/** /owner/repo/compare/base...head: the Compare panel — every changed file, drawn as the desktop compares it. */
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

  // The repository's default branch, so the rail's other tiles land somewhere sensible even
  // when the range in the URL is malformed. The other pages ask the same cached answer.
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const comparison = useAsync((s) => (parsed ? api.compare(owner, repo, base, head, s) : Promise.reject(new Error('Pick two versions to compare.'))), [owner, repo, base, head]);
  const pane = useAsync(
    (s) => (parsed && path ? api.compareView(owner, repo, base, head, path, view, layout, s) : new Promise<never>(() => {})),
    [owner, repo, base, head, path, view, layout],
  );

  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  const names = branches.state === 'ok' ? branches.value.branches.map((b) => b.name) : [];

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
      <span className="muted">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        {!names.includes(value) && value && <option value={value}>{value.length === 40 ? value.slice(0, 8) : value}</option>}
        {names.map((n) => (
          <option key={n} value={n}>{n}</option>
        ))}
      </select>
    </label>
  );

  return (
    <Workbench
      me={me}
      owner={owner}
      repo={repo}
      refName={head || defaultBranch}
      defaultBranch={defaultBranch}
      branches={branches.state === 'ok' ? branches.value.branches : []}
      branchesTruncated={branches.state === 'ok' && branches.value.truncated}
      onRef={(r) => go(base, r, path)}
      rail="compare"
      panelTitle="Compare"
      compareBase={base || undefined}
      panel={
        <>
          <div className="compare-pick">
            {picker(base, (b) => go(b, head, path), 'Older')}
            <span aria-hidden="true">{'→'}</span>
            {picker(head, (h) => go(base, h, path), 'Newer')}
          </div>
          {comparison.state === 'ok' && (
            <p className="muted pad compare-count">
              {comparison.value.total === 1 ? '1 file changed' : `${comparison.value.total} files changed`}
              {comparison.value.truncated && `, the first ${comparison.value.files.length} listed`}
            </p>
          )}
          {comparison.state === 'loading' && <p className="muted pad">Comparing...</p>}
          {comparison.state === 'ok' && (
            <ul className="changes">
              {comparison.value.files.length === 0 && <li className="muted pad">These two versions are the same.</li>}
              {comparison.value.files.map((f) => (
                <li key={f.path}>
                  <button type="button" className={`tree-row${f.path === path ? ' is-selected' : ''}`} onClick={() => go(base, head, f.path)} title={f.label}>
                    <span className={`change change-${f.change}`}>{mark[f.change]}</span>
                    <span className="tree-name">{f.object ?? f.label.slice(f.label.lastIndexOf('/') + 1)}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      }
    >
      {comparison.state === 'error' ? (
        <ErrorPanel error={comparison.error} signInUrl={signIn} />
      ) : path ? (
        <Pane
          result={pane}
          view={view}
          layout={layout}
          onView={(v) => setParam('view', v, 'rendered')}
          onLayout={(l) => setParam('layout', l, 'unified')}
          onLayer={(layer) => go(base, head, layer)}
          onOpenPath={(p) => navigate(blobUrl(owner, repo, head, p))}
          line={lineFromHash(location.hash)}
          onLine={(n) => navigate({ hash: `L${n}`, search: search.toString() }, { replace: true })}
          comparing
          signInUrl={signIn}
        />
      ) : (
        <section className="pane pane-empty">
          <p className="muted">Pick a changed file.</p>
        </section>
      )}
    </Workbench>
  );
}
