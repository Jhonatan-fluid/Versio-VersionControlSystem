import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { compareUrl } from '../routes';
import { HistoryPage } from './HistoryPage';

// Found in review: History's Compare link compared the ref with itself, so it always opened on
// "no differences". Nothing rendered a page in a test, so nothing could see a link it built.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let commitCount = 51;
let truncatedBranches = false;

function answer(url: string): unknown {
  if (url.includes('/history')) {
    return Array.from({ length: commitCount }, (_, i) => ({ sha: `${i}`.padStart(40, '0'), message: `c${i}`, author: 'a', when: '2026-10-01T00:00:00Z' }));
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'develop', commit: 'x' }, { name: 'feature', commit: 'y' }], truncated: truncatedBranches };
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
    commitCount = 51;
    truncatedBranches = false;
  });

  it('links Compare to the default branch against the ref shown, never the ref against itself', async () => {
    const { host, root } = await render('/acme/plant/history/feature');

    const compare = Array.from(host.querySelectorAll('a')).find((a) => a.textContent === 'Compare');
    expect(compare?.getAttribute('href')).toBe(compareUrl('acme', 'plant', 'develop', 'feature'));
    act(() => root.unmount());
  });

  it('says the list is only the latest commits when the server sent one more, and shows 50', async () => {
    const { host, root } = await render('/acme/plant/history/feature');

    expect(host.textContent).toContain('The latest 50 commits');
    expect(host.querySelectorAll('.commits > li')).toHaveLength(50);
    // The 51st is the older side of the 50th's comparison.
    expect(host.querySelectorAll('.commits > li')[49]?.textContent).toContain('What changed');
    act(() => root.unmount());
  });

  it('says nothing about a cut list for a repository of exactly 50 commits', async () => {
    // Found in review: a full page was the only signal, so exactly 50 said "the latest 50".
    commitCount = 50;
    const { host, root } = await render('/acme/plant/history/feature');

    expect(host.textContent).not.toContain('The latest 50 commits');
    act(() => root.unmount());
  });

  it('says when the branch picker lists only the first branches', async () => {
    // Found in review: the server stopped at its cap in silence.
    truncatedBranches = true;
    const { host, root } = await render('/acme/plant/history/feature');

    expect(host.querySelector('select option[disabled]')?.textContent).toBe('Only the first 2 branches are listed');
    act(() => root.unmount());
  });
});
