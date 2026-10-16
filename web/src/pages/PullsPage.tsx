import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { FragmentHost } from '../components/FragmentHost';
import { ErrorPanel } from '../components/Pane';
import { Workbench } from '../components/Workbench';
import { blobUrl, compareUrl, treeUrl } from '../routes';
import { useAsync } from '../useAsync';

/**
 * /owner/repo/pulls[?n=12]: the open pull requests — GitLab would say merge requests — each
 * with its description, and its diff one click away in the existing compare view, which is the
 * point: a PR's PLC changes reviewed as member tables and ladders, not as XML.
 */
export function PullsPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const picked = Number(search.get('n')) || null;

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const pulls = useAsync((s) => api.pulls(owner, repo, s), [owner, repo]);
  const pull = useAsync(
    (s) => (picked ? api.pullView(owner, repo, picked, s) : new Promise<never>(() => {})),
    [owner, repo, picked],
  );

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;

  function pick(number: number) {
    const next = new URLSearchParams(search);
    next.set('n', String(number));
    setSearch(next);
  }

  return (
    <Workbench
      me={me}
      owner={owner}
      repo={repo}
      refName={defaultBranch}
      defaultBranch={defaultBranch}
      branches={branches.state === 'ok' ? branches.value.branches : []}
      branchesTruncated={branches.state === 'ok' && branches.value.truncated}
      onRef={(r) => navigate(treeUrl(owner, repo, r))}
      rail="pulls"
      panelTitle="Merge requests"
      panel={
        <>
          {pulls.state === 'loading' && <p className="muted pad">Loading...</p>}
          {pulls.state === 'ok' && pulls.value.pulls.length === 0 && <p className="muted pad">No open merge requests.</p>}
          {pulls.state === 'ok' && (
            <ul className="row-list">
              {pulls.value.pulls.map((p) => (
                <li key={p.number}>
                  <button type="button" className={`row-pick${p.number === picked ? ' is-selected' : ''}`} onClick={() => pick(p.number)}>
                    <span className="row-title">
                      {p.draft && <span className="row-badge">draft</span>}
                      {p.title}
                    </span>
                    <span className="muted row-meta">
                      #{p.number} &middot; {p.author} &middot; {new Date(p.when).toLocaleDateString()} &middot; {p.headLabel} {'→'} {p.baseLabel}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          {pulls.state === 'ok' && pulls.value.truncated && (
            <p className="muted pad">The {pulls.value.pulls.length} most recently updated. The rest are on GitHub.</p>
          )}
        </>
      }
    >
      {pulls.state === 'error' ? (
        <ErrorPanel error={pulls.error} signInUrl={signIn} />
      ) : !picked ? (
        <section className="pane pane-empty">
          <p className="muted">Pick a merge request to read it, then open what it changed.</p>
        </section>
      ) : pull.state === 'loading' ? (
        <section className="pane pane-empty" aria-busy="true">Loading...</section>
      ) : pull.state === 'error' ? (
        <ErrorPanel error={pull.error} signInUrl={signIn} />
      ) : (
        <section className="pane">
          <header className="pane-head">
            <div className="pane-title">
              <span className="pane-label">Merge request</span>
              <h1>{pull.value.title}</h1>
              <span className="pane-folder">#{pull.value.number} · {pull.value.headLabel} {'→'} {pull.value.baseLabel}</span>
            </div>
            <div className="pane-tools">
              {/* The whole point of the tab: the PR's changes in the compare view, drawn as the
                  desktop draws them. The head SHA, not the head ref: a fork's branch name means
                  nothing here, its commit does. */}
              <Link className="button" to={compareUrl(owner, repo, pull.value.base, pull.value.headSha)}>What changed</Link>
            </div>
          </header>
          <p className="muted">
            {pull.value.draft ? 'Draft, ' : ''}opened by {pull.value.author}, updated {new Date(pull.value.when).toLocaleString()}
          </p>
          {pull.value.html ? (
            <FragmentHost html={pull.value.html} path="" onOpenPath={(p) => navigate(blobUrl(owner, repo, defaultBranch, p))} line={null} onLine={() => {}} />
          ) : (
            <p className="muted">No description.</p>
          )}
        </section>
      )}
    </Workbench>
  );
}
