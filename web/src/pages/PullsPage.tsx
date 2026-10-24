import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import type { Me, Pull } from '../api';
import { api } from '../api';
import { ErrorPanel } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { Avatar, Blankslate, Icon, IssueLabel, LinkButton, Loading, RelativeTime, type IconName } from '../components/ui';
import { newPullUrl, pullUrl, pullsUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

/** The tokens of a query, so one can be replaced without touching the rest. */
function tokens(q: string): string[] {
  return q.match(/(?:[A-Za-z-]+:)?(?:"[^"]*"|\S+)/g) ?? [];
}

/** The query with `key:` set to `value` (or removed when empty), the other tokens kept in order. */
export function withToken(q: string, key: string, value: string | null): string {
  const rest = tokens(q).filter((t) => !t.toLowerCase().startsWith(`${key}:`));
  if (value !== null && value !== '') {
    rest.push(`${key}:${value.includes(' ') ? `"${value}"` : value}`);
  }
  return rest.join(' ');
}

function tokenValue(q: string, key: string): string | null {
  const t = tokens(q).find((x) => x.toLowerCase().startsWith(`${key}:`));
  return t ? t.slice(key.length + 1).replace(/^"|"$/g, '') : null;
}

/** GitHub's icon and colour for a row: open, draft, merged, closed. */
function stateLook(p: Pull): { icon: IconName; cls: string; word: string } {
  const state = p.state ?? 'OPEN';
  if (state === 'MERGED') {
    return { icon: 'git-merge', cls: 'is-merged', word: 'Merged' };
  }
  if (state === 'CLOSED') {
    return { icon: 'git-pull-request-closed', cls: 'is-closed', word: 'Closed' };
  }
  return p.draft ? { icon: 'git-pull-request-draft', cls: 'is-draft', word: 'Draft' } : { icon: 'git-pull-request', cls: 'is-open', word: 'Open' };
}

const reviewWords: Record<string, { icon: IconName; cls: string; word: string }> = {
  APPROVED: { icon: 'check', cls: 'text-success', word: 'Approved' },
  CHANGES_REQUESTED: { icon: 'x', cls: 'text-danger', word: 'Changes requested' },
  REVIEW_REQUIRED: { icon: 'eye', cls: 'text-muted', word: 'Review required' },
};

const checkWords: Record<string, { icon: IconName; cls: string; word: string }> = {
  SUCCESS: { icon: 'check', cls: 'text-success', word: 'All checks passed' },
  FAILURE: { icon: 'x', cls: 'text-danger', word: 'Some checks failed' },
  ERROR: { icon: 'x', cls: 'text-danger', word: 'Some checks errored' },
  PENDING: { icon: 'dot-fill', cls: 'text-attention', word: 'Checks in progress' },
  EXPECTED: { icon: 'dot-fill', cls: 'text-attention', word: 'Checks expected' },
};

/**
 * /owner/repo/pulls?q=: GitHub's pull request list (docs/web/FINISH.md, B10) — the search box with
 * GitHub's grammar, the Open / Closed tabs with their counts, the filter menus that edit the
 * query, and one row per pull request with its state icon, labels, review and check state,
 * comment count and assignees, each a link straight to its page. A signed-in person gets GitHub's
 * search; without one the server lists by state and says so.
 */
export function PullsPage({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const [search] = useSearchParams();
  const q = search.get('q') ?? 'is:open';
  const after = search.get('after') ?? undefined;
  const [typed, setTyped] = useState(q);
  // The box follows the URL: a filter pill, a tab, Next or Back changes ?q=, and a box still
  // showing the old query dropped that filter on the next Enter (review, 2026-10-24).
  useEffect(() => setTyped(q), [q]);
  const pulls = useAsync((s) => api.pulls(owner, repo, s, q, after), [owner, repo, q, after]);
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  usePageTitle('Pull requests', `${owner}/${repo}`);

  const go = (next: string, cursor?: string) => navigate(`${pullsUrl(owner, repo, next)}${cursor ? `&after=${encodeURIComponent(cursor)}` : ''}`);
  const stateNow = tokenValue(q, 'is') === 'closed' || tokenValue(q, 'is') === 'merged' ? 'closed' : 'open';
  const list = pulls.state === 'ok' ? pulls.value : null;
  // The menus offer what the page can see: the rows' own authors, labels and assignees, so a
  // choice is always one GitHub will find something for.
  const distinct = (values: string[]) => [...new Set(values)].sort();
  const authors = distinct(list?.pulls.map((p) => p.author) ?? []);
  const labels = distinct(list?.pulls.flatMap((p) => (p.labels ?? []).map((l) => l.value)) ?? []);
  const assignees = distinct(list?.pulls.flatMap((p) => (p.assignees ?? []).map((a) => a.login)) ?? []);
  const count = (n: number | null | undefined, word: string) => (n === null || n === undefined ? word : `${n} ${word}`);
  const menu = (key: string, label: string, options: { value: string; label: string }[]) => (
    <label className="list-filter">
      <span className="visually-hidden">{label}</span>
      <select value={tokenValue(q, key) ?? ''} onChange={(e) => go(withToken(q, key, e.target.value || null))} aria-label={label}>
        <option value="">{label}</option>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  );

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="pulls" crumb="Pull requests">
      <form className="list-toolbar" onSubmit={(e) => { e.preventDefault(); go(typed.trim() || 'is:open'); }} role="search">
        <div className="list-search">
          <Icon name="search" className="text-muted" />
          <input value={typed} onChange={(e) => setTyped(e.target.value)} aria-label="Search all pull requests" placeholder="is:open author:me label:plc" />
        </div>
        {list?.writable && <LinkButton to={newPullUrl(owner, repo)} variant="primary">New pull request</LinkButton>}
      </form>
      {pulls.state === 'error' ? (
        <ErrorPanel error={pulls.error} signInUrl={signIn} />
      ) : (
        <div className="list-box">
          <div className="list-head">
            <div className="list-head-states">
              <button type="button" className={stateNow === 'open' ? 'is-current' : ''} onClick={() => go(withToken(q, 'is', 'open'))}>
                <Icon name="git-pull-request" /> {count(list?.open, 'Open')}
              </button>
              <button type="button" className={stateNow === 'closed' ? 'is-current' : ''} onClick={() => go(withToken(q, 'is', 'closed'))}>
                <Icon name="check" /> {count(list?.closed, 'Closed')}
              </button>
            </div>
            <div className="list-filters">
              {menu('author', 'Author', authors.map((a) => ({ value: a, label: a })))}
              {menu('label', 'Label', labels.map((l) => ({ value: l, label: l })))}
              {menu('review', 'Reviews', [{ value: 'none', label: 'No reviews' }, { value: 'required', label: 'Review required' }, { value: 'approved', label: 'Approved review' }, { value: 'changes_requested', label: 'Changes requested' }])}
              {menu('assignee', 'Assignee', assignees.map((a) => ({ value: a, label: a })))}
              {menu('sort', 'Sort', [{ value: 'updated-desc', label: 'Recently updated' }, { value: 'created-desc', label: 'Newest' }, { value: 'created-asc', label: 'Oldest' }, { value: 'comments-desc', label: 'Most commented' }, { value: 'updated-asc', label: 'Least recently updated' }])}
            </div>
          </div>
          {list && list.searched === false && (
            <div className="list-note text-muted text-small">Listed by state, not searched: sign in for GitHub's search and the counts.</div>
          )}
          {pulls.state === 'loading' && <Loading />}
          {list && list.pulls.length === 0 && (
            <Blankslate icon="git-pull-request" heading={stateNow === 'open' ? 'There aren’t any open pull requests' : 'No results matched your search'}>
              {list.writable && stateNow === 'open' ? 'Open one from two branches of this repository.' : 'Try another query.'}
            </Blankslate>
          )}
          {list?.pulls.map((p) => {
            const look = stateLook(p);
            const review = p.reviewDecision ? reviewWords[p.reviewDecision] : undefined;
            const check = p.checkRollup ? checkWords[p.checkRollup] : undefined;
            const verb = look.word === 'Merged' ? 'merged' : look.word === 'Closed' ? 'closed' : 'opened';
            return (
              <div key={p.number} className="list-row">
                <Icon name={look.icon} className={`list-row-icon ${look.cls}`} label={look.word} />
                <div className="list-row-main">
                  <Link className="list-row-title" to={pullUrl(owner, repo, p.number)}>{p.title}</Link>
                  {p.labels && p.labels.length > 0 && (
                    <span className="list-row-labels">
                      {p.labels.map((l) => (
                        <button key={l.value} type="button" className="label-button" onClick={() => go(withToken(q, 'label', l.value))} title={`Filter by ${l.name}`}>
                          <IssueLabel name={l.name} color={l.color} />
                        </button>
                      ))}
                    </span>
                  )}
                  <div className="list-row-meta">
                    #{p.number} {verb} <RelativeTime when={p.createdAt ?? p.when} /> by <button type="button" className="link-button text-bold" onClick={() => go(withToken(q, 'author', p.author))}>{p.author}</button>
                    {' · '}
                    <span className="branch-name" title={p.headLabel}>{p.headLabel}</span> → <span className="branch-name" title={p.baseLabel}>{p.baseLabel}</span>
                  </div>
                </div>
                <div className="list-row-side">
                  {check && <span className={`list-row-mark ${check.cls}`} title={check.word}><Icon name={check.icon} size={14} /></span>}
                  {review && <span className={`list-row-mark ${review.cls}`} title={review.word}><Icon name={review.icon} size={14} /></span>}
                  {p.assignees && p.assignees.length > 0 && (
                    <span className="list-row-assignees" title={`Assigned to ${p.assignees.map((a) => a.login).join(', ')}`}>
                      {p.assignees.map((a) => <Avatar key={a.login} login={a.login} url={a.avatar} size={20} />)}
                    </span>
                  )}
                  {p.comments !== undefined && p.comments > 0 && (
                    <Link to={pullUrl(owner, repo, p.number)} className="list-row-comments text-muted" title={`${p.comments} comment${p.comments === 1 ? '' : 's'}`}>
                      <Icon name="comment" size={14} /> {p.comments}
                    </Link>
                  )}
                </div>
              </div>
            );
          })}
          {list && (list.next || after) && (
            <div className="list-foot list-paging">
              {/* Back through this site's own history when there is one; on a pasted ?after= link
                  there is none, and "Previous" would have left the site — so it goes to the first page. */}
              {after && <button type="button" className="btn btn-small" onClick={() => ((window.history.state as { idx?: number } | null)?.idx ? navigate(-1) : go(q))}>Previous</button>}
              {list.next && <button type="button" className="btn btn-small" onClick={() => go(q, list.next!)}>Next</button>}
              {list.truncated && !list.next && <span>The rest are on GitHub.</span>}
            </div>
          )}
        </div>
      )}
    </RepoLayout>
  );
}
