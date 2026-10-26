import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BranchesPage, TagsPage } from './RefsPage';

// GitHub's branch and tag lists (docs/web/FINISH.md, Step 8), each row the way into the tree, the
// commits and the comparison.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

function answer(url: string): unknown {
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'fix', label: 'fix', commit: 'b'.repeat(40) }, { name: 'main', label: 'main', commit: 'a'.repeat(40) }], truncated: false };
  }
  if (url.endsWith('/tags')) {
    return { tags: [{ name: 'v2.0', label: 'v2.0', commit: 'a'.repeat(40) }, { name: 'v1.0', label: 'v1.0', commit: 'c'.repeat(40) }] };
  }
  if (url.endsWith('/releases')) {
    return { truncated: false, releases: [{ tag: 'v2.0', tagLabel: 'v2.0', name: 'Two', author: 'jo', when: '2026-10-01T00:00:00Z', prerelease: false }] };
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
          <Route path="/:owner/:repo/branches" element={<BranchesPage me={null} />} />
          <Route path="/:owner/:repo/tags" element={<TagsPage me={null} />} />
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

describe('Branches and tags', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('lists the branches with the default first and marked, each with its commits and its comparison', async () => {
    const { host, root } = await render('/acme/plant/branches');
    const rows = Array.from(host.querySelectorAll('.list-row'));

    expect(rows.map((r) => r.querySelector('.branch-name')?.textContent)).toEqual(['main', 'fix']);
    expect(rows[0]?.querySelector('.row-badge')?.textContent).toBe('Default');
    expect(rows[1]?.querySelector('a[href="/acme/plant/commits/fix"]')).not.toBeNull();
    expect(rows[1]?.querySelector('a[href="/acme/plant/compare/main...fix"]')).not.toBeNull();
    expect(rows[0]?.querySelector('a[href="/acme/plant/compare/main...main"]')).toBeNull();
    act(() => root.unmount());
  });

  it('lists the tags, marking the ones with a release, each with its commit and its comparison with the default', async () => {
    const { host, root } = await render('/acme/plant/tags');
    const rows = Array.from(host.querySelectorAll('.list-row'));

    expect(rows.map((r) => r.querySelector('.branch-name')?.textContent)).toEqual(['v2.0', 'v1.0']);
    expect(rows[0]?.querySelector('a.row-badge')?.getAttribute('href')).toBe('/acme/plant/releases/tag/v2.0');
    expect(rows[1]?.querySelector('a.row-badge')).toBeNull();
    expect(rows[1]?.querySelector(`a[href="/acme/plant/commit/${'c'.repeat(40)}"]`)).not.toBeNull();
    act(() => root.unmount());
  });
});
