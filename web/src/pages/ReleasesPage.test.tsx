import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ReleasesPage } from './ReleasesPage';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

/** The releases GitHub lists, newest first; the view answers for whichever tag the URL names. */
let tags = ['v2.0', 'v1.0'];
/** Whether the list is the server's cap, with older releases past it (the view still answers for those). */
let truncated = false;

function answer(url: string): unknown {
  if (url.includes('/releases/view')) {
    const tag = new URL(url, 'http://x').searchParams.get('tag') ?? '';
    return {
      tag, tagLabel: tag, name: 'Line 3', author: 'jo', when: '2026-10-01T00:00:00Z', prerelease: true,
      html: `<div class="md-page"><h2>Notes</h2><p>First, of ${tag}.</p></div>`,
    };
  }
  if (url.includes('/releases')) {
    return {
      truncated,
      releases: tags.map((tag, i) => (
        i === 0
          ? { tag, tagLabel: tag, name: 'Line 3', author: 'jo', when: '2026-10-01T00:00:00Z', prerelease: true }
          : { tag, tagLabel: tag, name: 'One', author: 'jo', when: '2026-09-01T00:00:00Z', prerelease: false }
      )),
    };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'main', label: 'main', commit: 'x' }], truncated: false };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
}

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}</output>;
}

async function render(path: string) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(answer(url)), { status: 200, headers: { 'Content-Type': 'application/json' } })));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={[path]}>
        <Where />
        <Routes>
          <Route path="/:owner/:repo/releases" element={<ReleasesPage me={null} />} />
          <Route path="/:owner/:repo/releases/latest" element={<ReleasesPage me={null} latest />} />
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
    tags = ['v2.0', 'v1.0'];
    truncated = false;
  });

  async function settle(host: HTMLElement) {
    for (let i = 0; i < 20 && !host.querySelector('.fragment'); i++) {
      await act(async () => {
        await new Promise((r) => setTimeout(r, 5));
      });
    }
  }

  // /releases/latest is GitHub's route marker; a release may be TAGGED "latest". The page once
  // folded the marker into the tag's string, so /releases/tag/latest redirected to the newest
  // release or loaded forever (the 2026-10-24 review's item 6).
  it('opens a release whose tag is literally "latest" on its own page', async () => {
    tags = ['latest', 'v1.0'];
    const { host, root } = await render('/acme/plant/releases/tag/latest');
    await settle(host);

    const fetched = (fetch as unknown as { mock: { calls: [string][] } }).mock.calls.map((c) => c[0]);
    expect(fetched.some((u) => u.includes('/releases/view') && u.includes('tag=latest'))).toBe(true);
    const rows = Array.from(host.querySelectorAll('article.box'));
    expect(rows[0]?.getAttribute('aria-current')).toBe('true');
    expect(rows[0]?.querySelector('.fragment')?.textContent).toContain('First, of latest.');
    expect(host.querySelector('[data-testid="where"]')?.textContent).toBe('/acme/plant/releases/tag/latest');
    act(() => root.unmount());
  });

  // A direct link to a release older than the capped list fetched its notes and never showed
  // them: the page drew an article per LISTED release, and the fetched view had no article to
  // land in (the 2026-10-24 review's item 12). The listed releases stay below it, closed.
  it('opens a release older than the capped list on its own page', async () => {
    truncated = true;
    const { host, root } = await render('/acme/plant/releases/tag/v0.9');
    await settle(host);

    const open = host.querySelector('article.box[aria-current="true"]');
    expect(open?.querySelector('.fragment')?.textContent).toContain('First, of v0.9.');
    expect(open?.querySelector('a.list-row-title')?.getAttribute('href')).toBe('/acme/plant/releases/tag/v0.9');
    expect(open?.textContent).toContain('Pre-release');
    const rows = Array.from(host.querySelectorAll('article.box'));
    expect(rows).toHaveLength(3);
    expect(rows[0]).toBe(open);
    expect(rows.slice(1).every((r) => r.querySelector('.fragment') === null)).toBe(true);
    expect(host.textContent).toContain('The latest 2.');
    act(() => root.unmount());
  });

  it('/releases/latest lands on the newest release’s own page', async () => {
    const { host, root } = await render('/acme/plant/releases/latest');
    await settle(host);

    expect(host.querySelector('[data-testid="where"]')?.textContent).toBe('/acme/plant/releases/tag/v2.0');
    expect(host.querySelector('article.box[aria-current="true"] .fragment')?.textContent).toContain('First, of v2.0.');
    act(() => root.unmount());
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
    expect(rows[0]?.querySelector('.fragment')?.textContent).toContain('First, of v2.0.');
    expect(rows[1]?.querySelector('.fragment')).toBeNull();
    act(() => root.unmount());
  });
});
