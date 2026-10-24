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
let searched = true;
let next: string | null = null;
let urls: string[] = [];

function answer(url: string): unknown {
  if (url.includes('/pulls')) {
    const q = decodeURIComponent(new URL(url, 'http://x').searchParams.get('q') ?? 'is:open');
    const closed = q.includes('is:closed');
    return {
      truncated: next !== null,
      writable,
      searched,
      query: q,
      total: closed ? 1 : 2,
      open: 2,
      closed: 1,
      next,
      pulls: closed ? [
        { number: 11, title: 'Old idea', author: 'kim', when: '2026-10-01T10:00:00Z', createdAt: '2026-09-30T10:00:00Z', state: 'MERGED', base: 'main', baseLabel: 'main', head: 'old', headLabel: 'old', headSha, draft: false, labels: [], assignees: [], reviewDecision: null, checkRollup: null, comments: 0 },
      ] : [
        { number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-15T10:00:00Z', createdAt: '2026-10-14T10:00:00Z', state: 'OPEN', base: 'main', baseLabel: 'main', head: 'fix', headLabel: 'fix', headSha, draft: false,
          labels: [{ name: 'plc', value: 'plc', color: '0e8a16' }], assignees: [{ login: 'sam', avatar: null }], reviewDecision: 'APPROVED', checkRollup: 'FAILURE', comments: 3 },
        { number: 13, title: 'Rework cooling', author: 'sam', when: '2026-10-16T10:00:00Z', createdAt: '2026-10-16T10:00:00Z', state: 'OPEN', base: 'main', baseLabel: 'main', head: 'cool', headLabel: 'cool', headSha, draft: true, labels: [], assignees: [], reviewDecision: null, checkRollup: null, comments: 0 },
      ],
    };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'main', label: 'main', commit: 'x' }], truncated: false };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
}

async function render(path: string) {
  urls = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    urls.push(url);
    return new Response(JSON.stringify(answer(url)), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }));
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
    searched = true;
    next = null;
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    for (let i = 0; i < 20; i++) {
      await act(async () => {
        await new Promise((r) => setTimeout(r, 5));
      });
    }
    void host;
  }

  const lastQuery = () => decodeURIComponent(new URL(urls.filter((u) => u.includes('/pulls')).at(-1)!, 'http://x').searchParams.get('q') ?? '');

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

  // Step 7 (FINISH.md B10): GitHub's list — the tabs with their counts, the search box and the
  // filter menus that edit the query, each row's labels, marks, people and comment count, paging.
  it('shows the Open and Closed counts, and the Closed tab lists the closed ones', async () => {
    const { host, root } = await render('/acme/plant/pulls');
    expect(host.querySelector('.list-head-states')?.textContent).toContain('2 Open');
    expect(host.querySelector('.list-head-states')?.textContent).toContain('1 Closed');

    await click(Array.from(host.querySelectorAll('.list-head-states button')).find((b) => b.textContent?.includes('Closed'))!, host);

    expect(lastQuery()).toBe('is:closed');
    expect(host.querySelector('.list-row-icon')?.getAttribute('aria-label')).toBe('Merged');
    act(() => root.unmount());
  });

  it('a row carries its labels, review and check marks, assignees and comment count, and a label filters by it', async () => {
    const { host, root } = await render('/acme/plant/pulls');
    const row = host.querySelector('.list-row')!;
    expect(row.querySelector('.list-row-labels')?.textContent).toContain('plc');
    expect(row.querySelector('.list-row-mark.text-success')?.getAttribute('title')).toBe('Approved');
    expect(row.querySelector('.list-row-mark.text-danger')?.getAttribute('title')).toBe('Some checks failed');
    expect(row.querySelector('.list-row-assignees')?.getAttribute('title')).toBe('Assigned to sam');
    expect(row.querySelector('.list-row-comments')?.textContent?.trim()).toBe('3');

    await click(row.querySelector('.label-button')!, host);

    expect(lastQuery()).toBe('is:open label:plc');
    act(() => root.unmount());
  });

  it('the search box and the menus edit the query in GitHub’s grammar, one token at a time', async () => {
    const { host, root } = await render('/acme/plant/pulls?q=is%3Aopen+author%3Ajo');
    const input = host.querySelector<HTMLInputElement>('.list-search input')!;
    expect(input.value).toBe('is:open author:jo');

    await act(async () => {
      const select = host.querySelector<HTMLSelectElement>('select[aria-label="Reviews"]')!;
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')!.set!.call(select, 'approved');
      select.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await click(host, host);
    expect(lastQuery()).toBe('is:open author:jo review:approved');

    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, 'interlock is:closed');
      input.dispatchEvent(new Event('input', { bubbles: true }));
      host.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    });
    await click(host, host);
    expect(lastQuery()).toBe('interlock is:closed');
    act(() => root.unmount());
  });

  it('says when the list was not searched, and pages on with the cursor', async () => {
    searched = false;
    next = 'abc';
    const { host, root } = await render('/acme/plant/pulls');
    expect(host.querySelector('.list-note')?.textContent).toContain('not searched');

    await click(Array.from(host.querySelectorAll('.list-paging button')).find((b) => b.textContent === 'Next')!, host);

    expect(urls.filter((u) => u.includes('/pulls')).at(-1)).toContain('after=abc');
    act(() => root.unmount());
  });
});
