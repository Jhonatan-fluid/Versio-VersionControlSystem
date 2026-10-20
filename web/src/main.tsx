import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes } from 'react-router';
import { api } from './api';
import { useAsync } from './useAsync';
import { Home } from './pages/Home';
import { RepoPage } from './pages/RepoPage';
import { ComparePage } from './pages/ComparePage';
import { DocsPage } from './pages/DocsPage';
import { HistoryPage } from './pages/HistoryPage';
import { PullPage } from './pages/PullPage';
import { NewPullPage } from './pages/NewPullPage';
import { PullsPage } from './pages/PullsPage';
import { ReleasesPage } from './pages/ReleasesPage';
import './styles/app.css';

function App() {
  const me = useAsync((s) => api.me(s), []);
  const who = me.state === 'ok' ? me.value : null;
  return (
    <Routes>
      <Route path="/" element={<Home me={who} />} />
      <Route path="/:owner/:repo" element={<RepoPage me={who} />} />
      <Route path="/:owner/:repo/tree/:ref" element={<RepoPage me={who} />} />
      <Route path="/:owner/:repo/blob/:ref/*" element={<RepoPage me={who} />} />
      <Route path="/:owner/:repo/compare/:range" element={<ComparePage me={who} />} />
      <Route path="/:owner/:repo/history/:ref/*" element={<HistoryPage me={who} />} />
      <Route path="/:owner/:repo/docs/:ref" element={<DocsPage me={who} />} />
      <Route path="/:owner/:repo/pulls" element={<PullsPage me={who} />} />
      <Route path="/:owner/:repo/pulls/new" element={<NewPullPage me={who} />} />
      <Route path="/:owner/:repo/pull/:number" element={<PullPage me={who} />} />
      <Route path="/:owner/:repo/releases" element={<ReleasesPage me={who} />} />
      <Route path="*" element={<Home me={who} />} />
    </Routes>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>,
);
