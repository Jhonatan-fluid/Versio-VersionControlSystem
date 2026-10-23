import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ReleasesPage } from './ReleasesPage';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

function answer(url: string): unknown {
  if (url.includes('/releases/view')) {
    return {
      tag: 'v2.0', tagLabel: 'v2.0', name: 'Line 3', author: 'jo', when: '2026-10-01T00:00:00Z', prerelease: true,
      html: '<div class="md-page"><h2>Notes</h2><p>First.</p></div>',
    };
  }
  if (url.includes('/releases')) {
    return {
      truncated: false,
      releases: [
        { tag: 'v2.0', tagLabel: 'v2.0', name: 'Line 3', author: 'jo', when: '2026-10-01T00:00:00Z', prerelease: true },
        { tag: 'v1.0', tagLabel: 'v1.0', name: 'One', author: 'jo', when: '2026-09-01T00:00:00Z', prerelease: false },
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
          <Route path="/:owner/:repo/releases" element={<ReleasesPage me={null} />} />
          <Route path="/:owner/:repo/releases/tag/:tag" element={<ReleasesPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 20 && !host.querySelector('article.box'); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('Releases', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('lists the releases with their badges and shows the picked notes', async () => {
    const { host, root } = await render('/acme/plant/releases/tag/v2.0');
    for (let i = 0; i < 20 && !host.querySelector('.fragment'); i++) {
      await act(async () => {
        await new Promise((r) => setTimeout(r, 5));
      });
    }

    const rows = Array.from(host.querySelectorAll('article.box'));
    expect(rows).toHaveLength(2);
    expect(rows[0]?.textContent).toContain('Pre-release');
    expect(rows[1]?.textContent).not.toContain('Pre-release');
    // The picked one is open with its notes; its title links to its own page.
    expect(rows[0]?.getAttribute('aria-current')).toBe('true');
    expect(rows[0]?.querySelector('a.list-row-title')?.getAttribute('href')).toBe('/acme/plant/releases/tag/v2.0');
    expect(rows[0]?.querySelector('.fragment')?.textContent).toContain('First.');
    expect(rows[1]?.querySelector('.fragment')).toBeNull();
    act(() => root.unmount());
  });
});
