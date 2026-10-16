import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { FragmentHost } from '../components/FragmentHost';
import { ErrorPanel } from '../components/Pane';
import { Workbench } from '../components/Workbench';
import { blobUrl, treeUrl } from '../routes';
import { useAsync } from '../useAsync';

/** /owner/repo/releases[?tag=v1.2]: the published releases, each with its notes rendered. */
export function ReleasesPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const [search, setSearch] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const picked = search.get('tag');

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const releases = useAsync((s) => api.releases(owner, repo, s), [owner, repo]);
  const release = useAsync(
    (s) => (picked ? api.releaseView(owner, repo, picked, s) : new Promise<never>(() => {})),
    [owner, repo, picked],
  );

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;

  function pick(tag: string) {
    const next = new URLSearchParams(search);
    next.set('tag', tag);
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
      rail="releases"
      panelTitle="Releases"
      panel={
        <>
          {releases.state === 'loading' && <p className="muted pad">Loading...</p>}
          {releases.state === 'ok' && releases.value.releases.length === 0 && <p className="muted pad">No releases yet.</p>}
          {releases.state === 'ok' && (
            <ul className="row-list">
              {releases.value.releases.map((r) => (
                <li key={r.tag}>
                  <button type="button" className={`row-pick${r.tag === picked ? ' is-selected' : ''}`} onClick={() => pick(r.tag)}>
                    <span className="row-title">
                      {r.prerelease && <span className="row-badge">pre-release</span>}
                      {r.name}
                    </span>
                    <span className="muted row-meta">
                      <code>{r.tagLabel}</code> &middot; {new Date(r.when).toLocaleDateString()}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          {releases.state === 'ok' && releases.value.truncated && (
            <p className="muted pad">The latest {releases.value.releases.length}. Older ones are on GitHub.</p>
          )}
        </>
      }
    >
      {releases.state === 'error' ? (
        <ErrorPanel error={releases.error} signInUrl={signIn} />
      ) : !picked ? (
        <section className="pane pane-empty">
          <p className="muted">Pick a release to read its notes.</p>
        </section>
      ) : release.state === 'loading' ? (
        <section className="pane pane-empty" aria-busy="true">Loading...</section>
      ) : release.state === 'error' ? (
        <ErrorPanel error={release.error} signInUrl={signIn} />
      ) : (
        <section className="pane">
          <header className="pane-head">
            <div className="pane-title">
              <span className="pane-label">Release</span>
              <h1>{release.value.name}</h1>
              <span className="pane-folder">{release.value.tagLabel}</span>
            </div>
          </header>
          <p className="muted">
            {release.value.prerelease ? 'Pre-release, ' : ''}published by {release.value.author}, {new Date(release.value.when).toLocaleString()}
          </p>
          {release.value.html ? (
            <FragmentHost html={release.value.html} path="" onOpenPath={(p) => navigate(blobUrl(owner, repo, defaultBranch, p))} line={null} onLine={() => {}} />
          ) : (
            <p className="muted">No notes.</p>
          )}
        </section>
      )}
    </Workbench>
  );
}
