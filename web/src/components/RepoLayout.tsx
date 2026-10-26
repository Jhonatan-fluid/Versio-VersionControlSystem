import type { ReactNode } from 'react';
import type { Me } from '../api';
import { api } from '../api';
import { useAsync } from '../useAsync';
import { commitsUrl, docsUrl, pullsUrl, releasesUrl, repoUrl } from '../routes';
import { AppHeader } from './AppHeader';
import { Icon, UnderlineNav, type NavItem } from './ui';

/** Which tab of the repository's nav is lit. */
export type RepoNav = 'code' | 'pulls' | 'commits' | 'releases' | 'docs';

interface Props {
  me: Me | null;
  owner: string;
  repo: string;
  nav: RepoNav;
  /** The page's crumb in the global header ("Pull requests"); none means the repository alone. */
  crumb?: string;
  /** The ref the Commits and Docs tabs open on; the default branch when the page has none. */
  refName?: string;
  /** The repository's visibility, when the page has read it. */
  isPrivate?: boolean;
  /** Full-width content (the code view); else GitHub's centred container. */
  wide?: boolean;
  children: ReactNode;
}

/**
 * GitHub's repository page frame: the global header, then the repository's own header — its
 * icon, owner / name, a Private badge — and the underline nav (Code · Pull requests · Commits ·
 * Releases · Docs), then the page. Replaces the desktop-shell workbench (docs/web/FINISH.md).
 *
 * The nav's counters come from the same cached answers the pages read (`api.repo`, `api.pulls`),
 * so a page that already asked pays nothing more.
 */
export function RepoLayout({ me, owner, repo, nav, crumb, refName, isPrivate, wide = false, children }: Props) {
  const info = useAsync((s) => api.repo(owner, repo, s), [owner, repo]);
  const pulls = useAsync((s) => api.pulls(owner, repo, s), [owner, repo]);
  const ref = refName ?? (info.state === 'ok' ? info.value.defaultBranch : 'main');
  const isPrivateNow = isPrivate ?? (info.state === 'ok' ? info.value.private : false);
  // The list is capped (ApiEndpoints lists 51 and says when it was cut); a cut list's count
  // would be the cap, not the truth, so the counter is left off until the server counts.
  const openPulls = pulls.state === 'ok' && Array.isArray(pulls.value.pulls) && !pulls.value.truncated ? pulls.value.pulls.length : null;
  const items: NavItem[] = [
    { id: 'code', label: 'Code', to: repoUrl(owner, repo), icon: 'code' },
    { id: 'pulls', label: 'Pull requests', to: pullsUrl(owner, repo), icon: 'git-pull-request', count: openPulls },
    { id: 'commits', label: 'Commits', to: commitsUrl(owner, repo, ref), icon: 'git-commit' },
    { id: 'releases', label: 'Releases', to: releasesUrl(owner, repo), icon: 'tag' },
    { id: 'docs', label: 'Docs', to: docsUrl(owner, repo, ref), icon: 'book' },
  ];
  return (
    <div className="app">
      <AppHeader me={me} repo={{ owner, name: repo }} crumb={crumb} />
      <div className="repo-header">
        <div className="repo-header-inner">
          <div className="repo-title">
            <Icon name={isPrivateNow ? 'lock' : 'repo'} className="repo-title-icon" />
            <span className="repo-title-owner">{owner}</span>
            <span className="repo-title-sep" aria-hidden="true">/</span>
            <strong className="repo-title-name">{repo}</strong>
            {isPrivateNow && <span className="label-outline">Private</span>}
          </div>
          <UnderlineNav items={items} current={nav} label="Repository" />
        </div>
      </div>
      <main className={`page${wide ? ' page-wide' : ''}`}>{children}</main>
    </div>
  );
}
