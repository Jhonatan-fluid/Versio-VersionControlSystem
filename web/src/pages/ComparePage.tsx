import { useState } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { ChangedFile, Layout, Me, PaneView } from '../api';
import { api } from '../api';
import { Header } from '../components/Header';
import { ErrorPanel, Pane } from '../components/Pane';
import { blobUrl, compareUrl, parseRange } from '../routes';
import { useAsync } from '../useAsync';
import { lineFromHash } from './RepoPage';

const mark: Record<ChangedFile['change'], string> = { added: 'A', modified: 'M', deleted: 'D' };

/** /owner/repo/compare/base...head: every changed file, and each one drawn as the desktop compares it. */
export function ComparePage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', range = '' } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const [listOpen, setListOpen] = useState(false);
  const parsed = parseRange(range);
  const base = parsed?.base ?? '';
  const head = parsed?.head ?? '';
  const path = search.get('path');
  const view: PaneView = search.get('view') === 'raw' ? 'raw' : 'rendered';
  const layout: Layout = search.get('layout') === 'split' ? 'split' : 'unified';

  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const comparison = useAsync((s) => (parsed ? api.compare(owner, repo, base, head, s) : Promise.reject(new Error('Pick two versions to compare.'))), [owner, repo, base, head]);
  const pane = useAsync(
    (s) => (parsed && path ? api.compareView(owner, repo, base, head, path, view, layout, s) : new Promise<never>(() => {})),
    [owner, repo, base, head, path, view, layout],
  );

  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  const names = branches.state === 'ok' ? branches.value.map((b) => b.name) : [];

  function go(nextBase: string, nextHead: string, nextPath: string | null) {
    navigate(compareUrl(owner, repo, nextBase, nextHead, nextPath ?? undefined, search));
    setListOpen(false);
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
    <div className="app">
      <Header
        me={me}
        onToggleTree={() => setListOpen(!listOpen)}
        repo={{ owner, name: repo, ref: head || 'main', branches: branches.state === 'ok' ? branches.value : [], onRef: (r) => go(base, r, path), compareHref: location.pathname + location.search }}
      />
      <div className="comparebar">
        {picker(base, (b) => go(b, head, path), 'Older')}
        <span aria-hidden="true">{'→'}</span>
        {picker(head, (h) => go(base, h, path), 'Newer')}
        {comparison.state === 'ok' && (
          <span className="muted">
            {comparison.value.total === 1 ? '1 file changed' : `${comparison.value.total} files changed`}
            {comparison.value.truncated && `, the first ${comparison.value.files.length} listed`}
          </span>
        )}
      </div>
      <div className={`workspace${listOpen ? ' tree-open' : ''}`}>
        <aside className="sidebar">
          {comparison.state === 'loading' && <p className="muted pad">Comparing...</p>}
          {comparison.state === 'error' && <ErrorPanel error={comparison.error} signInUrl={signIn} />}
          {comparison.state === 'ok' && (
            <ul className="changes">
              {comparison.value.files.length === 0 && <li className="muted pad">These two versions are the same.</li>}
              {comparison.value.files.map((f) => (
                <li key={f.path}>
                  <button type="button" className={`tree-row${f.path === path ? ' is-selected' : ''}`} onClick={() => go(base, head, f.path)} title={f.path}>
                    <span className={`change change-${f.change}`}>{mark[f.change]}</span>
                    <span className="tree-name">{f.object ?? f.path.slice(f.path.lastIndexOf('/') + 1)}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </aside>
        {path ? (
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
      </div>
    </div>
  );
}
