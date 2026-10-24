import { Link, useLocation, useParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { ErrorPanel } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { Blankslate, Icon, Loading } from '../components/ui';
import { commitUrl, commitsUrl, compareUrl, releasesUrl, treeUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

/** /owner/repo/branches: GitHub's branch list (Step 8) — the default first, each with its commits, its tree and its comparison with the default. */
export function BranchesPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const location = useLocation();
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const branches = useAsync((s) => api.branches(owner, repo, s), [owner, repo]);
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname) : null;
  usePageTitle('Branches', `${owner}/${repo}`);
  const list = branches.state === 'ok' ? [...branches.value.branches].sort((a, b) => (a.name === defaultBranch ? -1 : b.name === defaultBranch ? 1 : a.name.localeCompare(b.name))) : [];
  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="code" crumb="Branches" refName={defaultBranch}>
      <h1 className="pr-title" style={{ fontSize: 24, marginBottom: 12 }}>Branches</h1>
      {branches.state === 'error' && <ErrorPanel error={branches.error} signInUrl={signIn} />}
      {branches.state === 'loading' && <Loading what="Reading the branches…" />}
      {branches.state === 'ok' && (
        <div className="list-box">
          {list.length === 0 && <Blankslate icon="git-branch" heading="No branches">This repository has no branches GitHub lists.</Blankslate>}
          {list.map((b) => (
            <div key={b.name} className="list-row refs-row">
              <Icon name="git-branch" className="list-row-icon text-muted" />
              <div className="list-row-main">
                <Link className="branch-name" to={treeUrl(owner, repo, b.name)} title={b.label}>{b.label}</Link>
                {b.name === defaultBranch && <span className="row-badge">Default</span>}
                <div className="list-row-meta"><Link to={commitUrl(owner, repo, b.commit)}><code>{b.commit.slice(0, 7)}</code></Link></div>
              </div>
              <div className="list-row-side">
                <Link to={commitsUrl(owner, repo, b.name)} className="btn btn-small">Commits</Link>
                {b.name !== defaultBranch && <Link to={compareUrl(owner, repo, defaultBranch, b.name)} className="btn btn-small">Compare</Link>}
              </div>
            </div>
          ))}
          {branches.value.truncated && <div className="list-foot">Only the first {list.length} branches are listed; the rest are on GitHub.</div>}
        </div>
      )}
    </RepoLayout>
  );
}

/** /owner/repo/tags: GitHub's tag list (Step 8) — each with its tree, its commit, and its release when there is one. */
export function TagsPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const location = useLocation();
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const tags = useAsync((s) => api.tags(owner, repo, s), [owner, repo]);
  const releases = useAsync((s) => api.releases(owner, repo, s), [owner, repo]);
  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname) : null;
  usePageTitle('Tags', `${owner}/${repo}`);
  const released = new Set(releases.state === 'ok' ? releases.value.releases.map((r) => r.tag) : []);
  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="code" crumb="Tags" refName={defaultBranch}>
      <h1 className="pr-title" style={{ fontSize: 24, marginBottom: 12 }}>Tags</h1>
      {tags.state === 'error' && <ErrorPanel error={tags.error} signInUrl={signIn} />}
      {tags.state === 'loading' && <Loading what="Reading the tags…" />}
      {tags.state === 'ok' && (
        <div className="list-box">
          {tags.value.tags.length === 0 && <Blankslate icon="tag" heading="No tags">Nothing is tagged in this repository.</Blankslate>}
          {tags.value.tags.map((t) => (
            <div key={t.name} className="list-row refs-row">
              <Icon name="tag" className="list-row-icon text-muted" />
              <div className="list-row-main">
                <Link className="branch-name" to={treeUrl(owner, repo, t.name)} title={t.label}>{t.label}</Link>
                {released.has(t.name) && <Link to={releasesUrl(owner, repo, t.name)} className="row-badge">Release</Link>}
                <div className="list-row-meta"><Link to={commitUrl(owner, repo, t.commit)}><code>{t.commit.slice(0, 7)}</code></Link></div>
              </div>
              <div className="list-row-side">
                <Link to={compareUrl(owner, repo, t.name, defaultBranch)} className="btn btn-small">Compare</Link>
              </div>
            </div>
          ))}
        </div>
      )}
    </RepoLayout>
  );
}
