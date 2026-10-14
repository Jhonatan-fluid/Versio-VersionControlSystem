import { Link, useLocation, useNavigate, useParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { ErrorPanel } from '../components/Pane';
import { Workbench } from '../components/Workbench';
import { blobUrl, compareUrl, historyUrl, treeUrl } from '../routes';
import { useAsync } from '../useAsync';

/**
 * The commits shown. The server sends one more (ApiEndpoints): its presence is what says the list
 * was cut, and it is the "older" the last shown commit compares with.
 */
const HistoryLimit = 50;

/** /owner/repo/history/ref[/path]: the History panel — commits, each openable and comparable. */
export function HistoryPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', ref = '', '*': path } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const commits = useAsync((s) => api.history(owner, repo, ref, path || undefined, s), [owner, repo, ref, path]);
  // Compare opens on the default branch against the ref shown, as from the repository page. It
  // first compared the ref with itself, which always opened on "no differences" (found in review).
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname) : null;

  return (
    <Workbench
      me={me}
      owner={owner}
      repo={repo}
      refName={ref}
      defaultBranch={defaultBranch}
      branches={branches.state === 'ok' ? branches.value.branches : []}
      branchesTruncated={branches.state === 'ok' && branches.value.truncated}
      onRef={(r) => navigate(historyUrl(owner, repo, r, path))}
      rail="history"
      panelTitle={path ? `History of ${path.slice(path.lastIndexOf('/') + 1)}` : 'History'}
      panel={
        <>
          {commits.state === 'loading' && <p className="muted pad">Loading...</p>}
          {commits.state === 'ok' && (
            <ol className="commits">
              {commits.value.slice(0, HistoryLimit).map((c, i) => {
                const older = commits.value[i + 1];
                const [subject] = c.message.split('\n');
                return (
                  <li key={c.sha}>
                    <div className="commit-subject">{subject}</div>
                    <div className="muted">
                      {c.author} &middot; {new Date(c.when).toLocaleString()} &middot; <code>{c.sha.slice(0, 8)}</code>
                    </div>
                    <div className="commit-links">
                      <Link to={path ? blobUrl(owner, repo, c.sha, path) : treeUrl(owner, repo, c.sha)}>Open at this commit</Link>
                      {older && <Link to={compareUrl(owner, repo, older.sha, c.sha, path || undefined)}>What changed</Link>}
                    </div>
                  </li>
                );
              })}
            </ol>
          )}
          {commits.state === 'ok' && commits.value.length > HistoryLimit && (
            <p className="muted pad">The latest {HistoryLimit} commits. Older ones are on GitHub.</p>
          )}
        </>
      }
    >
      {commits.state === 'error' ? (
        <ErrorPanel error={commits.error} signInUrl={signIn} />
      ) : (
        <section className="pane pane-empty">
          <p className="muted">Open a commit's version of the repository, or see what it changed.</p>
        </section>
      )}
    </Workbench>
  );
}
