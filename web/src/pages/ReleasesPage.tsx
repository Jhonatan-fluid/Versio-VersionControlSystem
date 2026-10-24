import { useEffect } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { FragmentHost } from '../components/FragmentHost';
import { ErrorPanel } from '../components/Pane';
import { RepoLayout } from '../components/RepoLayout';
import { Avatar, Blankslate, Icon, Loading, RelativeTime } from '../components/ui';
import { blobUrl, releasesUrl } from '../routes';
import { usePageTitle } from '../title';
import { useAsync } from '../useAsync';

/**
 * /owner/repo/releases and /releases/tag/v1.2: GitHub's releases page — each release a box with
 * its tag, its author and its notes. The list carries meta only; one release's notes are
 * fetched when it is opened, because fifty rendered bodies would be paid for rows nobody reads.
 */
export function ReleasesPage({ me, latest = false }: { me: Me | null; latest?: boolean }) {
  const { owner = '', repo = '', tag: tagParam } = useParams();
  const picked = latest ? 'latest' : tagParam;
  const location = useLocation();
  const navigate = useNavigate();

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const releases = useAsync((s) => api.releases(owner, repo, s), [owner, repo]);
  const release = useAsync(
    (s) => (picked && picked !== 'latest' ? api.releaseView(owner, repo, picked, s) : new Promise<never>(() => {})),
    [owner, repo, picked],
  );
  // GitHub's /releases/latest: the newest release's own page, once the list says which that is.
  useEffect(() => {
    if (picked === 'latest' && releases.state === 'ok') {
      const newest = releases.value.releases[0]?.tag;
      navigate(newest ? releasesUrl(owner, repo, newest) : releasesUrl(owner, repo), { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [picked, releases.state]);

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  usePageTitle(picked ? `Release ${picked}` : 'Releases', `${owner}/${repo}`);

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="releases" crumb="Releases">
      {releases.state === 'loading' && <Loading />}
      {releases.state === 'error' && <ErrorPanel error={releases.error} signInUrl={signIn} />}
      {releases.state === 'ok' && releases.value.releases.length === 0 && (
        <Blankslate icon="tag" heading="No releases yet" spacious>Releases published on GitHub show here with their notes.</Blankslate>
      )}
      {releases.state === 'ok' && releases.value.releases.map((r) => {
        const open = r.tag === picked;
        return (
          <article key={r.tag} className="box" style={{ marginBottom: 16 }} aria-current={open ? 'true' : undefined}>
            <div className="box-header">
              <Icon name="tag" className="text-muted" />
              <Link to={releasesUrl(owner, repo, r.tag)} className="list-row-title">{r.name}</Link>
              {r.prerelease && <span className="label" style={{ color: 'var(--color-attention-fg)', borderColor: 'var(--color-attention-fg)' }}>Pre-release</span>}
              <span className="text-muted text-small" style={{ marginLeft: 'auto', display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                <code>{r.tagLabel}</code>
                <RelativeTime when={r.when} />
              </span>
            </div>
            {open && (
              <div className="box-body">
                {release.state === 'loading' && <Loading what="Reading the notes…" />}
                {release.state === 'error' && <p className="text-danger">{release.error.message}</p>}
                {release.state === 'ok' && (
                  <>
                    <p className="text-muted text-small" style={{ display: 'flex', gap: 6, alignItems: 'center', marginTop: 0 }}>
                      <Avatar login={release.value.author} size={16} />
                      <span className="text-bold">{release.value.author}</span> released this <RelativeTime when={release.value.when} />
                    </p>
                    {release.value.html ? (
                      // A link in the notes opens the file at the release's own tag, not at the default branch's tip.
                      <FragmentHost html={release.value.html} path="" onOpenPath={(p) => navigate(blobUrl(owner, repo, picked ?? defaultBranch, p))} line={null} onLine={() => {}} />
                    ) : (
                      <p className="text-muted">No notes.</p>
                    )}
                  </>
                )}
              </div>
            )}
          </article>
        );
      })}
      {releases.state === 'ok' && releases.value.truncated && (
        <p className="text-muted">The latest {releases.value.releases.length}. Older ones are on GitHub.</p>
      )}
    </RepoLayout>
  );
}
