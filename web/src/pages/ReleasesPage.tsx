import { useEffect } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import type { Me, Release } from '../api';
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
  // The route marker (/releases/latest, the `latest` prop) and the tag (/releases/tag/:tag) are
  // kept apart: a release may be TAGGED "latest", and folding the marker into the tag's string
  // sent /releases/tag/latest to the newest release instead, or left it loading forever (the
  // 2026-10-24 review's item 6). The marker's route has no tag param, so nothing is picked there.
  const { owner = '', repo = '', tag: picked } = useParams();
  const location = useLocation();
  const navigate = useNavigate();

  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const releases = useAsync((s) => api.releases(owner, repo, s), [owner, repo]);
  const release = useAsync(
    (s) => (picked ? api.releaseView(owner, repo, picked, s) : new Promise<never>(() => {})),
    [owner, repo, picked],
  );
  // GitHub's /releases/latest: the newest release's own page, once the list says which that is.
  useEffect(() => {
    if (latest && releases.state === 'ok') {
      const newest = releases.value.releases[0]?.tag;
      navigate(newest ? releasesUrl(owner, repo, newest) : releasesUrl(owner, repo), { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [latest, releases.state]);

  const defaultBranch = info.state === 'ok' ? info.value.defaultBranch : 'main';
  const signIn = me?.signInAvailable && !me.signedIn ? api.signInUrl(location.pathname + location.search) : null;
  usePageTitle(picked ? `Release ${picked}` : 'Releases', `${owner}/${repo}`);

  // The list is the server's cap (the latest fifty). A direct link to a release past it fetched
  // the notes and had nowhere to put them until 2026-10-25: the page drew one article per LISTED
  // release and the picked one was not among them, so the list came up closed and the notes were
  // dropped on the floor (the 2026-10-24 review's item 12). Such a release gets its own article
  // first, built from the view's meta — spelled by the server, as a listed row's is — with the
  // list below it, closed. The header needs the view, so while it loads or after it fails the
  // box carries only the body: the raw tag from the URL is the one name here nobody has spelled.
  const unlisted = picked !== undefined && releases.state === 'ok' && !releases.value.releases.some((r) => r.tag === picked);

  const header = (r: Release) => (
    <div className="box-header">
      <Icon name="tag" className="text-muted" />
      <Link to={releasesUrl(owner, repo, r.tag)} className="list-row-title">{r.name}</Link>
      {r.prerelease && <span className="label" style={{ color: 'var(--color-attention-fg)', borderColor: 'var(--color-attention-fg)' }}>Pre-release</span>}
      <span className="text-muted text-small" style={{ marginLeft: 'auto', display: 'inline-flex', gap: 6, alignItems: 'center' }}>
        <code>{r.tagLabel}</code>
        <RelativeTime when={r.when} />
      </span>
    </div>
  );

  const notes = (
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
  );

  return (
    <RepoLayout me={me} owner={owner} repo={repo} nav="releases" crumb="Releases">
      {releases.state === 'loading' && <Loading />}
      {releases.state === 'error' && <ErrorPanel error={releases.error} signInUrl={signIn} />}
      {releases.state === 'ok' && releases.value.releases.length === 0 && (
        <Blankslate icon="tag" heading="No releases yet" spacious>Releases published on GitHub show here with their notes.</Blankslate>
      )}
      {unlisted && (
        <article className="box" style={{ marginBottom: 16 }} aria-current="true">
          {release.state === 'ok' && header(release.value)}
          {notes}
        </article>
      )}
      {releases.state === 'ok' && releases.value.releases.map((r) => {
        const open = r.tag === picked;
        return (
          <article key={r.tag} className="box" style={{ marginBottom: 16 }} aria-current={open ? 'true' : undefined}>
            {header(r)}
            {open && notes}
          </article>
        );
      })}
      {releases.state === 'ok' && releases.value.truncated && (
        <p className="text-muted">The latest {releases.value.releases.length}. Older ones are on GitHub.</p>
      )}
    </RepoLayout>
  );
}
