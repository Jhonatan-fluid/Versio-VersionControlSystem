import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { compareUrl } from '../routes';
import { CommitsPage } from './CommitsPage';

// Found in review: History's Compare link compared the ref with itself, so it always opened on
// "no differences". Nothing rendered a page in a test, so nothing could see a link it built.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let commitCount = 51;
let truncatedBranches = false;
/** When set, the history asked for is an object's with two files (2026-10-27): the server names it and says which file each commit touched. */
let objectHistory = false;

function answer(url: string): unknown {
  if (url.includes('/history')) {
    const commits = Array.from({ length: commitCount }, (_, i) => ({ sha: `${i}`.padStart(40, '0'), message: `c${i}`, author: 'a', when: '2026-10-01T00:00:00Z', touched: objectHistory ? (i === 0 ? ['src/Glob_Var.gvl.xml'] : ['src/Glob_Var.gvl.st', 'src/Glob_Var.gvl.xml']) : [] }));
    return objectHistory
      ? { object: 'Glob_Var', objectLabel: 'src/Glob_Var.gvl.st', layers: [{ path: 'src/Glob_Var.gvl.st', label: 'src/Glob_Var.gvl.st' }, { path: 'src/Glob_Var.gvl.xml', label: 'src/Glob_Var.gvl.xml' }], layersCut: false, commits }
      : { object: null, objectLabel: null, layers: [], layersCut: false, commits };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'develop', label: 'develop', commit: 'x' }, { name: 'feature', label: 'feature', commit: 'y' }], truncated: truncatedBranches };
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
          <Route path="/:owner/:repo/commits/:ref/*" element={<CommitsPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 40 && !host.querySelector('.commit-row'); i++) {
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
    objectHistory = false;
  });

  // An object's history covered one layer until 2026-10-27 (the path GitHub was asked about). The
  // server now lists every file of the object and says which each commit touched; the page names
  // the object, offers a chip per file, and marks the touched one on each row.
  it("names the object whose history this is, offers a chip per file, and marks each row's touched files", async () => {
    objectHistory = true;
    commitCount = 2;
    const { host, root } = await render('/acme/plant/commits/feature/src/Glob_Var.gvl.st');

    expect(host.querySelector('.history-object')?.textContent).toBe('History for Glob_Var · 2 files');
    const chips = Array.from(host.querySelectorAll('.layers a.chip'));
    expect(chips.map((c) => c.textContent)).toEqual(['Glob_Var.gvl.st', 'Glob_Var.gvl.xml']);
    expect(chips[0]?.classList.contains('is-on')).toBe(true);
    expect(chips[1]?.getAttribute('href')).toBe('/acme/plant/commits/feature/src/Glob_Var.gvl.xml');
    const rows = host.querySelectorAll('.commit-row');
    expect(Array.from(rows[0]!.querySelectorAll('.commit-touched')).map((c) => c.textContent)).toEqual(['Glob_Var.gvl.xml']);
    expect(Array.from(rows[1]!.querySelectorAll('.commit-touched')).map((c) => c.textContent)).toEqual(['Glob_Var.gvl.st', 'Glob_Var.gvl.xml']);
    act(() => root.unmount());
  });

  it('says nothing of objects or touched files on a branch’s history', async () => {
    commitCount = 2;
    const { host, root } = await render('/acme/plant/commits/feature');

    expect(host.querySelector('.history-object')).toBeNull();
    expect(host.querySelector('.layers')).toBeNull();
    expect(host.querySelector('.commit-touched')).toBeNull();
    act(() => root.unmount());
  });

  it('links Compare to the default branch against the ref shown, never the ref against itself', async () => {
    const { host, root } = await render('/acme/plant/commits/feature');

    const compare = Array.from(host.querySelectorAll('a')).find((a) => a.textContent === 'Compare');
    expect(compare?.getAttribute('href')).toBe(compareUrl('acme', 'plant', 'develop', 'feature'));
    act(() => root.unmount());
  });

  it('says the list is only the latest commits when the server sent one more, and shows 50', async () => {
    const { host, root } = await render('/acme/plant/commits/feature');

    expect(host.textContent).toContain('The latest 50 commits');
    const rows = host.querySelectorAll('.commit-row');
    expect(rows).toHaveLength(50);
    // The subject opens the commit's own page (Step 8), which draws what it changed from its parent.
    expect(rows[49]?.querySelector('a.commit-subject')?.getAttribute('href')).toBe(`/acme/plant/commit/${'49'.padStart(40, '0')}`);
    act(() => root.unmount());
  });

  it('says nothing about a cut list for a repository of exactly 50 commits', async () => {
    // Found in review: a full page was the only signal, so exactly 50 said "the latest 50".
    commitCount = 50;
    const { host, root } = await render('/acme/plant/commits/feature');

    expect(host.textContent).not.toContain('The latest 50 commits');
    act(() => root.unmount());
  });

  it('says when the branch picker lists only the first branches', async () => {
    // Found in review: the server stopped at its cap in silence.
    truncatedBranches = true;
    const { host, root } = await render('/acme/plant/commits/feature');

    expect(host.querySelector('select option[disabled]')?.textContent).toBe('Only the first 2 branches are listed');
    act(() => root.unmount());
  });
});
