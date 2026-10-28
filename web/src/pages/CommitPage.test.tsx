import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CommitPage } from './CommitPage';

// GitHub's commit page with the desktop's commit card (docs/web/FINISH.md, Step 8): the message,
// the people, the stats and the parents; the changed objects as the marked tree; every file drawn
// from the first parent.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const sha = 'd'.repeat(40);
const parent = 'c'.repeat(40);
let urls: string[] = [];
/** When set, the commit also renames a file (and edits it), as GitHub reports with previous_filename. */
let renamed = false;
/** When set, the commit also edits the native XML layer of a two-layer object (and nothing of its readable layer). */
let twoLayers = false;
/** When set, the server could not group the files: `objects` is empty and says why. */
let ungrouped: string | null = null;

const xml = { path: 'src/Glob_Var.gvl.xml', label: 'src/Glob_Var.gvl.xml', status: 'modified', additions: 3, deletions: 2, previousPath: null, previousLabel: null };
const st = { path: 'src/Glob_Var.gvl.st', label: 'src/Glob_Var.gvl.st', status: 'unmodified', additions: 0, deletions: 0, previousPath: null, previousLabel: null };

function answer(url: string): unknown {
  if (url.includes('/commit?')) {
    const pump = { path: 'src/Pump.st', label: 'src/Pump.st', status: 'modified', additions: 1, deletions: 1, previousPath: null, previousLabel: null };
    const fresh = { path: 'src/New.st', label: 'src/New.st', status: 'added', additions: 1, deletions: 0, previousPath: null, previousLabel: null };
    const valve = { path: 'src/Valve.st', label: 'src/Valve.st', status: 'renamed', additions: 1, deletions: 1, previousPath: 'src/OldValve.st', previousLabel: 'src/OldValve.st' };
    const one = (f: typeof valve | typeof pump, name: string) => ({ key: f.path, kind: 'object', name, label: f.label, path: f.path, from: f.previousPath, type: 'POU', typeName: 'Program', toolchain: 'Acme IDE 7', status: f.status, additions: f.additions, deletions: f.deletions, layers: [{ ...f, changed: true }] });
    return {
      sha, headline: 'Change the pump', body: 'It was slow.', author: 'Jo Git', login: 'jo', avatar: null, when: '2026-10-17T08:00:00Z',
      committer: 'GitHub', committed: '2026-10-17T08:01:00Z', parents: [parent], additions: 2, deletions: 1, verified: true, url: 'https://github.com/acme/plant/commit/' + sha, filesTruncated: false,
      files: [pump, fresh, ...(renamed ? [valve] : []), ...(twoLayers ? [xml] : [])],
      objects: ungrouped ? [] : [
        one(pump, 'Pump'),
        one(fresh, 'New'),
        ...(renamed ? [one(valve, 'Valve')] : []),
        // The object is keyed by its first layer; the drawn layer is the XML, the only one the commit touched.
        ...(twoLayers ? [{ key: 'src/Glob_Var.gvl.st', kind: 'object', name: 'Glob_Var', label: 'src/Glob_Var.gvl.st', path: xml.path, from: null, type: 'GVL', typeName: 'Global variables', toolchain: 'Acme IDE 7', status: 'modified', additions: 3, deletions: 2, layers: [{ ...st, changed: false }, { ...xml, changed: true }] }] : []),
      ],
      objectsUnavailable: ungrouped,
    };
  }
  if (url.includes('/tree?')) {
    return { commit: sha, base: parent, isVersio: true, truncated: false, problems: [], nodes: [
      { name: 'src', path: 'src', label: 'src', kind: 'folder', primary: null, files: [], changedDescendants: 2, rollUp: 'added', children: [
        { name: 'Pump', path: 'src/Pump.st', label: 'src/Pump.st', kind: 'object', primary: 'src/Pump.st', files: ['src/Pump.st'], children: [], status: 'modified', typeName: 'Program' },
        { name: 'New', path: 'src/New.st', label: 'src/New.st', kind: 'object', primary: 'src/New.st', files: ['src/New.st'], children: [], status: 'added', typeName: 'Program' },
        ...(twoLayers ? [{ name: 'Glob_Var', path: 'src/Glob_Var.gvl.st', label: 'src/Glob_Var.gvl.st', kind: 'object', primary: 'src/Glob_Var.gvl.st', files: ['src/Glob_Var.gvl.st', 'src/Glob_Var.gvl.xml'], children: [], status: 'modified', typeName: 'Global variables' }] : []),
      ] },
    ] };
  }
  if (url.includes('/compare/view')) {
    return { kind: 'source', title: 'Pump', path: 'src/Pump.st', html: '<div class="cd-page">drawn</div>', notices: [], layers: ['src/Pump.st'], layerLabels: ['src/Pump.st'] };
  }
  if (url.includes('/commit/comments')) {
    return { writable: true, comments: [
      { id: 'CC_1', author: 'lee', avatar: null, html: '<div class="md-page"><p>Right for the small plant?</p></div>', when: '2026-10-17T09:00:00Z', path: null, pathLabel: null, line: null, url: 'https://github.com/acme/plant/commit/x#commitcomment-1' },
      { id: 'CC_2', author: 'jo', avatar: null, html: '<div class="md-page"><p>Yes.</p></div>', when: '2026-10-17T09:10:00Z', path: 'src/Pump.st', pathLabel: 'src/Pump.st', line: 2, url: null },
    ] };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
}

let writes: { url: string; body: Record<string, unknown>; csrf: string | null }[] = [];
/** What the server answers a post with; a test sets it to see the box hold its key. */
let postAnswer: Record<string, unknown> = { outcome: 'done', id: 'CC_3' };

async function render(path: string) {
  urls = [];
  writes = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    urls.push(url);
    if (init?.method === 'POST') {
      const headers = (init.headers ?? {}) as Record<string, string>;
      writes.push({ url, body: JSON.parse(init.body as string) as Record<string, unknown>, csrf: headers['X-Versio-Request'] ?? null });
      return new Response(JSON.stringify(postAnswer), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }
    return new Response(JSON.stringify(answer(url)), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/:owner/:repo/commit/:sha" element={<CommitPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 30; i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('CommitPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
    postAnswer = { outcome: 'done', id: 'CC_3' };
    renamed = false;
    twoLayers = false;
    ungrouped = null;
  });

  // 2026-10-27: the page drew one section per layer FILE, headed by its raw path, with no object
  // name, type chip or vendor pill — and an object whose native XML alone changed had no section
  // at the id the tree's pick scrolled to (the primary file's). One section per object now, under
  // the same header the review's Files changed uses, with a chip per layer.
  it('draws one section per object with its name, type chip, vendor pill and a chip per layer, the touched layer drawn', async () => {
    twoLayers = true;
    const { host, root } = await render(`/acme/plant/commit/${sha}`);

    const sections = Array.from(host.querySelectorAll('.file-review'));
    expect(sections.map((s) => s.getAttribute('data-file'))).toEqual(['src/Pump.st', 'src/New.st', 'src/Glob_Var.gvl.st']);
    const gvl = sections[2]!;
    expect(gvl.querySelector('.review-file')?.textContent).toBe('Glob_Var');
    expect(gvl.querySelector('.tree-annotation')?.textContent).toBe('Global variables');
    expect(gvl.querySelector('.tree-pill')?.textContent).toBe('Acme IDE 7');
    expect(gvl.querySelector('.pane-head .change')?.textContent).toBe('M');
    expect(gvl.querySelector('.diffstat')?.textContent).toContain('+3');
    // A chip per layer, the drawn one lit, the touched one marked; a one-layer object has no chips.
    const chips = Array.from(gvl.querySelectorAll('.layers .chip'));
    expect(chips.map((c) => c.textContent)).toEqual(['Glob_Var.gvl.st', 'Glob_Var.gvl.xml M']);
    expect(chips[1]?.classList.contains('is-on')).toBe(true);
    expect(sections[0]?.querySelector('.layers')).toBeNull();
    // The drawing asked for is the XML's, the layer the commit touched.
    const asked = urls.filter((u) => u.includes('/compare/view')).map((u) => decodeURIComponent(u));
    expect(asked.some((u) => u.includes('path=src/Glob_Var.gvl.xml'))).toBe(true);
    expect(asked.some((u) => u.includes('path=src/Glob_Var.gvl.st'))).toBe(false);

    // A chip switches the drawn layer: the readable file is asked for from the same two commits.
    await act(async () => {
      (chips[0] as HTMLButtonElement).click();
      await new Promise((r) => setTimeout(r, 20));
    });
    const after = urls.filter((u) => u.includes('/compare/view')).map((u) => decodeURIComponent(u));
    expect(after.find((u) => u.includes('path=src/Glob_Var.gvl.st'))).toContain(`base=${parent}`);
    expect(gvl.querySelector('.layers .chip.is-on')?.textContent).toBe('Glob_Var.gvl.st');
    act(() => root.unmount());
  });

  it("scrolls a tree pick to the object's section, keyed by the row's path, not by its primary file", async () => {
    twoLayers = true;
    const scrolled: string[] = [];
    const original = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function (this: Element) { scrolled.push(this.id); };
    try {
      const { host, root } = await render(`/acme/plant/commit/${sha}`);
      const row = Array.from(host.querySelectorAll('.files-tree .tree-row')).find((r) => r.querySelector('.tree-name')?.textContent === 'Glob_Var')!;
      await act(async () => {
        (row as HTMLButtonElement).click();
      });
      // The tree scrolls its own selected row too (no id); the section is the one with one.
      expect(scrolled.filter((id) => id.startsWith('file-'))).toEqual(['file-src/Glob_Var.gvl.st']);
      expect(host.querySelector('[data-file="src/Glob_Var.gvl.st"]')?.classList.contains('is-active')).toBe(true);
      act(() => root.unmount());
    } finally {
      Element.prototype.scrollIntoView = original;
    }
  });

  it('draws the raw files as plain sections under a notice when the server could not group them', async () => {
    ungrouped = 'The objects could not be listed: GitHub’s rate limit is spent for now.';
    const { host, root } = await render(`/acme/plant/commit/${sha}`);

    expect(host.querySelector('.files-main .notice')?.textContent).toContain('rate limit is spent');
    const sections = Array.from(host.querySelectorAll('.file-review'));
    expect(sections.map((s) => s.getAttribute('data-file'))).toEqual(['src/Pump.st', 'src/New.st']);
    expect(sections[0]?.getAttribute('data-kind')).toBe('file');
    expect(sections[0]?.querySelector('.review-file')?.textContent).toBe('Pump.st');
    expect(sections[0]?.querySelector('.tree-pill')).toBeNull();
    expect(sections[0]?.querySelector('.cd-page')?.textContent).toBe('drawn');
    act(() => root.unmount());
  });

  // Review, 2026-10-25 (P2): the page received a renamed file's previous path and asked for the
  // comparison with the new path only, so a renamed-and-edited file was drawn wholly added — two
  // additions, no removals. The old path goes with the request, as the pull request page sends it,
  // and a file that was not renamed sends none.
  it('asks for a renamed file compared from its old path, and for the others from none', async () => {
    renamed = true;
    const { host, root } = await render(`/acme/plant/commit/${sha}`);

    const compares = urls.filter((u) => u.includes('/compare/view')).map((u) => decodeURIComponent(u));
    const valve = compares.find((u) => u.includes('path=src/Valve.st'));
    expect(valve).toContain('from=src/OldValve.st');
    expect(compares.filter((u) => !u.includes('path=src/Valve.st'))).toHaveLength(2);
    expect(compares.filter((u) => !u.includes('path=src/Valve.st')).some((u) => u.includes('from='))).toBe(false);
    expect(host.querySelector('[data-file="src/Valve.st"] .pane-title')?.textContent).toContain('renamed from src/OldValve.st');
    act(() => root.unmount());
  });

  it('draws the card: message and body, both people, the signature, the stats, the parent, Browse files', async () => {
    const { host, root } = await render(`/acme/plant/commit/${sha}`);
    const card = host.querySelector('.commit-card')!;

    expect(card.querySelector('.commit-card-title')?.textContent).toBe('Change the pump');
    expect(card.querySelector('.commit-body')?.textContent).toBe('It was slow.');
    expect(card.querySelector('.commit-byline')?.textContent).toContain('jo');
    expect(card.querySelector('.commit-byline')?.textContent).toContain('committed by GitHub');
    expect(card.querySelector('.verified-badge')).not.toBeNull();
    expect(card.querySelector('.diffstat')?.textContent).toContain('+2');
    expect(card.querySelector(`a[href="/acme/plant/commit/${parent}"]`)?.textContent).toBe(parent.slice(0, 7));
    expect(card.querySelector(`a[href="/acme/plant/tree/${sha}"]`)?.textContent).toBe('Browse files');
    act(() => root.unmount());
  });

  it('lists the changed objects as the marked tree from the first parent, and stacks every file drawn from it', async () => {
    const { host, root } = await render(`/acme/plant/commit/${sha}`);

    expect(urls.find((u) => u.includes('/tree?'))).toContain(`base=${parent}`);
    expect(urls.find((u) => u.includes('/tree?'))).toContain('changed=1');
    const rows = Array.from(host.querySelectorAll('.files-tree .tree-row'));
    expect(rows.find((r) => r.querySelector('.tree-name')?.textContent === 'New')?.querySelector('.change')?.textContent).toBe('A');
    // One section per OBJECT, keyed by the object's path (here, each object's one file).
    const sections = Array.from(host.querySelectorAll('.file-review'));
    expect(sections.map((s) => s.getAttribute('data-file'))).toEqual(['src/Pump.st', 'src/New.st']);
    expect(sections[0]?.querySelector('.review-file')?.textContent).toBe('Pump');
    expect(sections[0]?.querySelector('.diffstat')?.textContent).toContain('+1');
    expect(urls.filter((u) => u.includes('/compare/view')).every((u) => u.includes(`base=${parent}`) && u.includes(`head=${sha}`))).toBe(true);
    expect(sections[0]?.querySelector('.cd-page')?.textContent).toBe('drawn');
    act(() => root.unmount());
  });

  // Step 3b: GitHub's commit comments under the files, one on a line naming its file; a new one
  // posted on the commit with an operation key and the header no cross-site request can send.
  it("lists the commit's comments, a line comment naming its file, and posts a new one with an operation key", async () => {
    const { host, root } = await render(`/acme/plant/commit/${sha}`);
    const section = host.querySelector('.commit-comments')!;

    expect(section.querySelector('.panel-title')?.textContent).toBe('2 comments on this commit');
    const cards = Array.from(section.querySelectorAll('.comment'));
    expect(cards[0]?.textContent).toContain('Right for the small plant?');
    expect(cards[0]?.closest('.comment-row')?.id).toBe('commitcomment-1');
    expect(cards[1]?.querySelector(`a[href="/acme/plant/blob/${sha}/src/Pump.st"]`)?.textContent).toBe('src/Pump.st:L2');

    const textarea = section.querySelector('textarea')!;
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, 'Looks right.');
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await act(async () => {
      Array.from(section.querySelectorAll('button')).find((b) => b.textContent === 'Comment on this commit')!.click();
      await new Promise((r) => setTimeout(r, 20));
    });

    expect(writes).toHaveLength(1);
    expect(writes[0]!.url).toContain(`/commits/${sha}/comments`);
    expect(writes[0]!.body).toMatchObject({ body: 'Looks right.' });
    expect(typeof writes[0]!.body.opKey).toBe('string');
    expect(writes[0]!.csrf).toBe('1');
    // Posted: read again, so the new comment shows without a reload.
    expect(urls.filter((u) => u.includes('/commit/comments')).length).toBe(2);
    act(() => root.unmount());
  });

  // The key is held across a reload (review, 2026-10-24): a post whose outcome is unknown, then
  // the page opened again, finds the same words and the same key, so the retry is the same
  // operation to the server and never a second comment.
  it('keeps the words and the held key across a reload, so a retry is the same operation', async () => {
    postAnswer = { outcome: 'unknown', message: 'The connection was lost while posting.' };
    const first = await render(`/acme/plant/commit/${sha}`);
    const box = first.host.querySelector('.commit-comment-new')!;
    const textarea = box.querySelector('textarea')!;
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, 'Looks right.');
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await act(async () => {
      Array.from(box.querySelectorAll('button')).find((b) => b.textContent === 'Comment on this commit')!.click();
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(writes).toHaveLength(1);
    const key = writes[0]!.body.opKey as string;
    expect(box.querySelector('.comment-note')?.textContent).toContain('connection was lost');
    act(() => first.root.unmount());

    postAnswer = { outcome: 'done', id: 'CC_3' };
    const second = await render(`/acme/plant/commit/${sha}`);
    const again = second.host.querySelector('.commit-comment-new')!;
    expect(again.querySelector('textarea')?.value).toBe('Looks right.');
    expect(again.querySelector('textarea')?.readOnly).toBe(true);
    const retry = Array.from(again.querySelectorAll('button')).find((b) => b.textContent === 'Try again');
    expect(retry).toBeDefined();
    await act(async () => {
      retry!.click();
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(writes).toHaveLength(1);
    expect(writes[0]!.body.opKey).toBe(key);
    expect(again.querySelector('textarea')?.value).toBe('');
    expect(sessionStorage.length).toBe(0);
    act(() => second.root.unmount());
  });
});
