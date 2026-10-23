import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes, useLocation, useParams } from 'react-router';
import type { Me } from './api';
import { api } from './api';
import { useAsync } from './useAsync';
import { applyColorMode, readColorMode } from './theme';
import { Home } from './pages/Home';
import { NotFound } from './pages/NotFound';
import { RepoPage } from './pages/RepoPage';
import { ComparePage } from './pages/ComparePage';
import { DocsPage } from './pages/DocsPage';
import { CommitsPage } from './pages/CommitsPage';
import { PullPage } from './pages/PullPage';
import { NewPullPage } from './pages/NewPullPage';
import { PullsPage } from './pages/PullsPage';
import { ReleasesPage } from './pages/ReleasesPage';
import { commitsUrl, pullUrl, releasesUrl } from './routes';
import './styles/app.css';

/**
 * The routes, in GitHub's shapes (docs/web/FINISH.md, F): a link copied from github.com, with
 * the host swapped, opens the same thing here. The older shapes this page used to write
 * (/history, ?tab=, ?n=, ?tag=) redirect, so a link pasted into a review last week still opens.
 */
function App() {
  const me = useAsync((s) => api.me(s), []);
  const who = me.state === 'ok' ? me.value : null;
  return (
    <Routes>
      <Route path="/" element={<Home me={who} />} />
      <Route path="/:owner/:repo" element={<RepoPage me={who} />} />
      <Route path="/:owner/:repo/tree/:ref/*" element={<RepoPage me={who} />} />
      <Route path="/:owner/:repo/blob/:ref/*" element={<RepoPage me={who} />} />
      <Route path="/:owner/:repo/compare/:range" element={<ComparePage me={who} />} />
      <Route path="/:owner/:repo/commits/:ref/*" element={<CommitsPage me={who} />} />
      <Route path="/:owner/:repo/history/:ref/*" element={<HistoryRedirect />} />
      <Route path="/:owner/:repo/docs/:ref" element={<DocsPage me={who} />} />
      <Route path="/:owner/:repo/pulls" element={<PullsRedirectOrList me={who} />} />
      <Route path="/:owner/:repo/pulls/new" element={<NewPullPage me={who} />} />
      <Route path="/:owner/:repo/pull/:number" element={<PullPage me={who} />} />
      <Route path="/:owner/:repo/pull/:number/:tab" element={<PullPage me={who} />} />
      <Route path="/:owner/:repo/releases" element={<ReleasesRedirectOrList me={who} />} />
      <Route path="/:owner/:repo/releases/tag/:tag" element={<ReleasesPage me={who} />} />
      <Route path="*" element={<NotFound me={who} />} />
    </Routes>
  );
}

/** /history/ref/path was this page's name for GitHub's /commits/ref/path. */
function HistoryRedirect() {
  const { owner = '', repo = '', ref = '', '*': path } = useParams();
  return <Navigate to={commitsUrl(owner, repo, ref, path || undefined)} replace />;
}

/** /pulls?n=12 used to pick a pull request in a side pane; it is /pull/12 now. */
function PullsRedirectOrList({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const n = Number(new URLSearchParams(useLocation().search).get('n'));
  return n > 0 ? <Navigate to={pullUrl(owner, repo, n)} replace /> : <PullsPage me={me} />;
}

/** /releases?tag=v1 is /releases/tag/v1 now. */
function ReleasesRedirectOrList({ me }: { me: Me | null }) {
  const { owner = '', repo = '' } = useParams();
  const tag = new URLSearchParams(useLocation().search).get('tag');
  return tag ? <Navigate to={releasesUrl(owner, repo, tag)} replace /> : <ReleasesPage me={me} />;
}

applyColorMode(readColorMode());

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>,
);
