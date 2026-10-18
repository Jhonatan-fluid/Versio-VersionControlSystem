import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { compareUrl } from '../routes';
import { PullsPage } from './PullsPage';

// The merge-requests view: the list in the panel, the picked one in the pane, and its diff one
// click away in the compare view — by the head COMMIT, so a fork's pull request still compares.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const headSha = 'd'.repeat(40);
const mergeBase = 'b'.repeat(40);
let answeredMergeBase: string | null = mergeBase;

function answer(url: string): unknown {
  if (url.includes('/pulls/view')) {
    return {
      number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-15T10:00:00Z',
      base: 'main', baseLabel: 'main', headLabel: 'fix', headSha, mergeBase: answeredMergeBase, draft: false,
      html: '<div class="md-page"><h2>Why</h2><p>The delay was wrong.</p></div>',
    };
  }
  if (url.includes('/pulls')) {
    return {
      truncated: false,
      pulls: [{ number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-15T10:00:00Z', base: 'main', baseLabel: 'main', head: 'fix', headLabel: 'fix', headSha, draft: false }],
    };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'main', label: 'main', commit: 'x' }], truncated: false };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
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
          <Route path="/:owner/:repo/pulls" element={<PullsPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 20 && !host.querySelector('.row-pick'); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('Pulls', () => {
  afterEach(() => {
    answeredMergeBase = mergeBase;
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('lists the open pull requests and hints until one is picked', async () => {
    const { host, root } = await render('/acme/plant/pulls');

    expect(host.querySelector('.row-pick')?.textContent).toContain('Fix interlock');
    expect(host.querySelector('.pane-empty')?.textContent).toContain('Pick a merge request');
    act(() => root.unmount());
  });

  async function picked() {
    const rendered = await render('/acme/plant/pulls?n=12');
    for (let i = 0; i < 20 && !rendered.host.querySelector('.pane-head'); i++) {
      await act(async () => {
        await new Promise((r) => setTimeout(r, 5));
      });
    }
    return rendered;
  }

  // From the merge-base, not the base branch: comparing with the branch's tip showed every commit
  // merged to main since the branch was cut as reverted by this pull request (FINDINGS 2026-10-17).
  it('shows the picked pull with its description and its compare link from the merge-base to the head commit', async () => {
    const { host, root } = await picked();

    expect(host.querySelector('.pane-title h1')?.textContent).toBe('Fix interlock');
    expect(host.querySelector('.fragment')?.textContent).toContain('The delay was wrong.');
    const compare = Array.from(host.querySelectorAll('a')).find((a) => a.textContent === 'What changed');
    expect(compare?.getAttribute('href')).toBe(compareUrl('acme', 'plant', mergeBase, headSha));
    act(() => root.unmount());
  });

  it('offers no comparison at all when there is no merge-base, rather than the wrong one', async () => {
    answeredMergeBase = null;
    const { host, root } = await picked();

    expect(Array.from(host.querySelectorAll('a')).some((a) => a.textContent === 'What changed')).toBe(false);
    expect(host.querySelector('.pane-tools')?.textContent).toContain('unavailable');
    expect(host.querySelector('.fragment')?.textContent).toContain('The delay was wrong.');
    act(() => root.unmount());
  });
});
