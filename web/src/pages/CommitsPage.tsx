import { Link, useLocation, useNavigate, useParams } from 'react-router';
import type { Commit, Me } from '../api';
import { api } from '../api';
import { ErrorPanel } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { Avatar, Blankslate, Icon, Loading, RelativeTime } from '../components/ui';
import { blobUrl, commitUrl, commitsUrl, compareUrl, treeUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';
import { BranchPicker } from './RepoPage';

/**
 * The commits shown. The server sends one more (ApiEndpoints): its presence is what says the list
 * was cut, and it is the "older" the last shown commit compares with.
 */
const HistoryLimit = 50;

/** Commits grouped by the day they were made, as GitHub's commits page groups them. */
function byDay(commits: Commit[]): { day: string; commits: Commit[] }[] {
  const groups: { day: string; commits: Commit[] }[] = [];
  for (const c of commits) {
    const date = new Date(c.when);
    const day = Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    const last = groups[groups.length - 1];
    if (last && last.day === day) {
      last.commits.push(c);
    } else {
      groups.push({ day, commits: [c] });
    }
  }
  return groups;
}

/** A layer's file name, for a chip. */
function fileName(label: string): string {
  return label.slice(label.lastIndexOf('/') + 1);
}

/**
 * /owner/repo/commits/ref[/path]: GitHub's commits page — by day, each openable and comparable.
 * With a path that is an object's, the OBJECT's history (2026-10-27): the heading names it and
 * its files, a chip per file narrows the list to that one file, and each row says which of the
 * files the commit touched when there is more than one.
 */
export function CommitsPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '', ref = '', '*': path } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  // GitHub's /commits with no ref is the default branch's (review, 2026-10-24: it landed on NotFound).
  const refShown = ref || (info.state === 'ok' ? info.value.defaultBranch : '');
  const history = useAsync((s) => (refShown ? api.history(owner, repo, refShown, path || undefined, s) : new Promise<never>(() => {})), [owner, repo, refShown, path]);
  // Compare opens on the default branch against the ref shown, as from the repository page. It
  // first compared the ref with itself, which always opened on "no differences" (found in review).
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname) : null;
  const object = history.state === 'ok' ? history.value.object : null;
  const layers = history.state === 'ok' ? history.value.layers : [];
  usePageTitle(path ? `History for ${object ?? path}` : `Commits · ${refShown}`, `${owner}/${repo}`);

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="commits" refName={refShown}>
      <div className="code-toolbar" style={{ padding: '0 0 16px' }}>
        <BranchPicker
          value={refShown}
          branches={branches.state === 'ok' ? branches.value.branches : []}
          truncated={branches.state === 'ok' && branches.value.truncated}
          onPick={(r) => navigate(commitsUrl(owner, repo, r, path || undefined))}
        />
        {path && !object && <span className="text-muted">History for <code>{path}</code></span>}
        {path && object && (
          <span className="text-muted history-object" title={history.state === 'ok' ? history.value.objectLabel ?? undefined : undefined}>
            History for <span className="text-bold">{object}</span> · {layers.length} file{layers.length === 1 ? '' : 's'}
            {history.state === 'ok' && history.value.layersCut && <> (the first {layers.length} of them)</>}
          </span>
        )}
        <Link to={compareUrl(owner, repo, defaultBranch, refShown, path || undefined)} className="btn btn-small" style={{ marginLeft: 'auto' }}>
          <Icon name="diff" className="btn-icon" />
          <span className="btn-text">Compare</span>
        </Link>
      </div>
      {path && object && layers.length > 1 && (
        // A chip per file: the one asked about is lit; another opens that file's own history,
        // which is one layer's list and says so by naming no object (a file is its own node).
        <div className="layers" aria-label="Files of this object">
          {layers.map((l) => (
            <Link key={l.path} to={commitsUrl(owner, repo, refShown, l.path)} className={`chip${l.path === path ? ' is-on' : ''}`} title={l.label}>{fileName(l.label)}</Link>
          ))}
        </div>
      )}
      {history.state === 'loading' && <Loading what="Reading the commits…" />}
      {history.state === 'error' && <ErrorPanel error={history.error} signInUrl={signIn} />}
      {history.state === 'ok' && history.value.commits.length === 0 && <Blankslate icon="git-commit" heading="No commits here">Nothing has touched this path on this branch.</Blankslate>}
      {history.state === 'ok' && (
        <ol className="commits">
          {byDay(history.value.commits.slice(0, HistoryLimit)).map((group) => (
            <li key={group.day + group.commits[0]!.sha}>
              <div className="commit-day">
                <Icon name="git-commit" />
                Commits on {group.day}
              </div>
              <ul className="commit-group" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
                {group.commits.map((c) => {
                  const [subject] = c.message.split('\n');
                  return (
                    <li key={c.sha} className="commit-row">
                      <div className="commit-main">
                        {/* GitHub's /commit/sha: the commit's own page, with its message, people, stats and every file drawn (Step 8). */}
                        <Link className="commit-subject" to={commitUrl(owner, repo, c.sha)} title="This commit's page">
                          {subject}
                        </Link>
                        <div className="commit-byline">
                          <Avatar login={c.author} size={16} />
                          <span className="text-bold">{c.author}</span>
                          <span>committed <RelativeTime when={c.when} /></span>
                          {layers.length > 1 && c.touched.map((t) => {
                            const label = layers.find((l) => l.path === t)?.label ?? t;
                            return <span key={t} className="chip commit-touched" title={label}>{fileName(label)}</span>;
                          })}
                        </div>
                      </div>
                      <div className="commit-side">
                        <Link className="commit-sha" to={commitUrl(owner, repo, c.sha)} title={c.sha}><code>{c.sha.slice(0, 7)}</code></Link>
                        <Link className="btn btn-small" to={path ? blobUrl(owner, repo, c.sha, path) : treeUrl(owner, repo, c.sha)} title="The repository as it was at this commit">
                          <Icon name="code" className="btn-icon" />
                        </Link>
                      </div>
                    </li>
                  );
                })}
              </ul>
            </li>
          ))}
        </ol>
      )}
      {history.state === 'ok' && history.value.commits.length > HistoryLimit && (
        <p className="text-muted" style={{ marginTop: 16 }}>The latest {HistoryLimit} commits. Older ones are on GitHub.</p>
      )}
    </RepoLayout>
  );
}
