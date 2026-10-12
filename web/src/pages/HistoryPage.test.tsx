import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { compareUrl } from '../routes';
import { HistoryPage } from './HistoryPage';

// Found in review: History's Compare link compared the ref with itself, so it always opened on
// "no differences". Nothing rendered a page in a test, so nothing could see a link it built.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

function answer(url: string): unknown {
  if (url.includes('/history')) {
    return Array.from({ length: 50 }, (_, i) => ({ sha: `${i}`.padStart(40, '0'), message: `c${i}`, author: 'a', when: '2026-10-01T00:00:00Z' }));
  }
  if (url.endsWith('/branches')) {
    return [{ name: 'develop', commit: 'x' }, { name: 'feature', commit: 'y' }];
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'develop' };
}

async function render(path: string) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(answer(url)), { status: 200, headers: { 'Content-Type': 'application/json' } })));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/:owner/:repo/history/:ref/*" element={<HistoryPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 20 && !host.querySelector('.commits'); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('History', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('links Compare to the default branch against the ref shown, never the ref against itself', async () => {
    const { host, root } = await render('/acme/plant/history/feature');

    const compare = Array.from(host.querySelectorAll('a')).find((a) => a.textContent === 'Compare');
    expect(compare?.getAttribute('href')).toBe(compareUrl('acme', 'plant', 'develop', 'feature'));
    act(() => root.unmount());
  });

  it('says a full page is only the latest commits', async () => {
    const { host, root } = await render('/acme/plant/history/feature');

    expect(host.textContent).toContain('The latest 50 commits');
    act(() => root.unmount());
  });
});
