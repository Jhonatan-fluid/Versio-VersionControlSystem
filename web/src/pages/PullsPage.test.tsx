import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PullsPage } from './PullsPage';

// GitHub's pull request list: a box with the open count at its head and one row per pull request,
// each a link straight to its page — no side pane to pick into (docs/web/FINISH.md, B10). What a
// pull request changed is the review page's business, from the merge-base (FINDINGS 2026-10-17).

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const headSha = 'd'.repeat(40);
let writable = false;

function answer(url: string): unknown {
  if (url.includes('/pulls')) {
    return {
      truncated: false,
      writable,
      pulls: [
        { number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-15T10:00:00Z', base: 'main', baseLabel: 'main', head: 'fix', headLabel: 'fix', headSha, draft: false },
        { number: 13, title: 'Rework cooling', author: 'sam', when: '2026-10-16T10:00:00Z', base: 'main', baseLabel: 'main', head: 'cool', headLabel: 'cool', headSha, draft: true },
      ],
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
  for (let i = 0; i < 20 && !host.querySelector('.list-row'); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('Pulls', () => {
  afterEach(() => {
    writable = false;
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('lists the open pull requests, each linking to its own page, with the count at the head', async () => {
    const { host, root } = await render('/acme/plant/pulls');

    const rows = Array.from(host.querySelectorAll('.list-row'));
    expect(rows).toHaveLength(2);
    expect(rows[0]?.querySelector('a.list-row-title')?.textContent).toBe('Fix interlock');
    expect(rows[0]?.querySelector('a.list-row-title')?.getAttribute('href')).toBe('/acme/plant/pull/12');
    expect(rows[0]?.textContent).toContain('#12 opened');
    expect(rows[0]?.textContent).toContain('by jo');
    expect(host.querySelector('.list-head')?.textContent).toContain('2 Open');
    // No pane to pick into: the row IS the way in.
    expect(host.querySelector('.pane-empty')).toBeNull();
    act(() => root.unmount());
  });

  it('marks a draft with its own icon, so a reviewer sees it before opening it', async () => {
    const { host, root } = await render('/acme/plant/pulls');

    const rows = Array.from(host.querySelectorAll('.list-row'));
    expect(rows[1]?.querySelector('.list-row-icon')?.getAttribute('aria-label')).toBe('Draft');
    expect(rows[0]?.querySelector('.list-row-icon')?.getAttribute('aria-label')).toBe('Open');
    act(() => root.unmount());
  });

  it('offers to open one only where one can be opened', async () => {
    const off = await render('/acme/plant/pulls');
    expect(Array.from(off.host.querySelectorAll('a')).some((a) => a.textContent === 'New pull request')).toBe(false);
    act(() => off.root.unmount());
    document.body.innerHTML = '';

    writable = true;
    const on = await render('/acme/plant/pulls');
    expect(Array.from(on.host.querySelectorAll('a')).find((a) => a.textContent === 'New pull request')?.getAttribute('href')).toBe('/acme/plant/pulls/new');
    act(() => on.root.unmount());
  });
});
