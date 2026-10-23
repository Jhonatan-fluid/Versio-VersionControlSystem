import { Link, useLocation, useParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { ErrorPanel } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { Avatar, Blankslate, Icon, LinkButton, Loading, RelativeTime } from '../components/ui';
import { newPullUrl, pullUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

/**
 * /owner/repo/pulls: GitHub's pull request list — a box with the open count at its head and one
 * row per pull request, each a link straight to its page. The server lists open ones only today
 * (docs/web/FINISH.md, B10, has the rest: closed, filters, search, paging).
 */
export function PullsPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const location = useLocation();
  const pulls = useAsync((s) => api.pulls(owner, repo, s), [owner, repo]);
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  usePageTitle('Pull requests', `${owner}/${repo}`);

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="pulls" crumb="Pull requests">
      <div className="pr-title-row" style={{ marginBottom: 16 }}>
        <div style={{ flex: 1 }} />
        {pulls.state === 'ok' && pulls.value.writable && (
          <LinkButton to={newPullUrl(owner, repo)} variant="primary">New pull request</LinkButton>
        )}
      </div>
      {pulls.state === 'error' ? (
        <ErrorPanel error={pulls.error} signInUrl={signIn} />
      ) : (
        <div className="list-box">
          <div className="list-head">
            <div className="list-head-states">
              <span className="is-current">
                <Icon name="git-pull-request" />
                {pulls.state === 'ok' ? `${pulls.value.pulls.length}${pulls.value.truncated ? '+' : ''} Open` : 'Open'}
              </span>
            </div>
          </div>
          {pulls.state === 'loading' && <Loading />}
          {pulls.state === 'ok' && pulls.value.pulls.length === 0 && (
            <Blankslate icon="git-pull-request" heading="There aren’t any open pull requests">
              {pulls.value.writable ? 'Open one from two branches of this repository.' : 'Nothing is waiting for review.'}
            </Blankslate>
          )}
          {pulls.state === 'ok' && pulls.value.pulls.map((p) => (
            <div key={p.number} className="list-row">
              <Icon name={p.draft ? 'git-pull-request-draft' : 'git-pull-request'} className={`list-row-icon ${p.draft ? 'is-draft' : 'is-open'}`} label={p.draft ? 'Draft' : 'Open'} />
              <div className="list-row-main">
                <Link className="list-row-title" to={pullUrl(owner, repo, p.number)}>{p.title}</Link>
                <div className="list-row-meta">
                  #{p.number} opened <RelativeTime when={p.when} /> by <span className="text-bold">{p.author}</span>
                  {' · '}
                  <span className="branch-name" title={p.headLabel}>{p.headLabel}</span> → <span className="branch-name" title={p.baseLabel}>{p.baseLabel}</span>
                </div>
              </div>
              <div className="list-row-side">
                <Avatar login={p.author} size={20} />
              </div>
            </div>
          ))}
          {pulls.state === 'ok' && pulls.value.truncated && (
            <div className="list-foot">The {pulls.value.pulls.length} most recently updated. The rest are on GitHub.</div>
          )}
        </div>
      )}
    </RepoLayout>
  );
}
