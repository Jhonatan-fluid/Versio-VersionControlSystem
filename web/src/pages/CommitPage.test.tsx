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

function answer(url: string): unknown {
  if (url.includes('/commit?')) {
    return {
      sha, headline: 'Change the pump', body: 'It was slow.', author: 'Jo Git', login: 'jo', avatar: null, when: '2026-10-17T08:00:00Z',
      committer: 'GitHub', committed: '2026-10-17T08:01:00Z', parents: [parent], additions: 2, deletions: 1, verified: true, url: 'https://github.com/acme/plant/commit/' + sha, filesTruncated: false,
      files: [
        { path: 'src/Pump.st', label: 'src/Pump.st', status: 'modified', additions: 1, deletions: 1, previousPath: null, previousLabel: null },
        { path: 'src/New.st', label: 'src/New.st', status: 'added', additions: 1, deletions: 0, previousPath: null, previousLabel: null },
      ],
    };
  }
  if (url.includes('/tree?')) {
    return { commit: sha, base: parent, isVersio: true, truncated: false, problems: [], nodes: [
      { name: 'src', path: 'src', label: 'src', kind: 'folder', primary: null, files: [], changedDescendants: 2, rollUp: 'added', children: [
        { name: 'Pump', path: 'src/Pump.st', label: 'src/Pump.st', kind: 'object', primary: 'src/Pump.st', files: ['src/Pump.st'], children: [], status: 'modified', typeName: 'Program' },
        { name: 'New', path: 'src/New.st', label: 'src/New.st', kind: 'object', primary: 'src/New.st', files: ['src/New.st'], children: [], status: 'added', typeName: 'Program' },
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

async function render(path: string) {
  urls = [];
  writes = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    urls.push(url);
    if (init?.method === 'POST') {
      const headers = (init.headers ?? {}) as Record<string, string>;
      writes.push({ url, body: JSON.parse(init.body as string) as Record<string, unknown>, csrf: headers['X-Versio-Request'] ?? null });
      return new Response(JSON.stringify({ outcome: 'done', id: 'CC_3' }), { status: 200, headers: { 'Content-Type': 'application/json' } });
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
    const sections = Array.from(host.querySelectorAll('.file-review'));
    expect(sections.map((s) => s.getAttribute('data-file'))).toEqual(['src/Pump.st', 'src/New.st']);
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
});
