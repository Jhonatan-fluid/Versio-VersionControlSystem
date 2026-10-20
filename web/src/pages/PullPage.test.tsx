import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ThreadPlacement } from '../api';
import { PullPage, placementSelector, threadSelector } from './PullPage';

// The review page (docs/web/PLAN.md, *Pull request review*, slice 1): everything read against one
// snapshot, threads under their lines on Raw, nothing unplaceable lost, and a reload offered —
// never forced, never silent — when the pull request moves on.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const head = 'd'.repeat(40);
const token = 'sealed-token';

// A Raw text diff as TextDiffHtml draws it: rows carry the old and new file lines.
const rawDiff = `<div class="cd-page cd-diff">
  <div class="cd-drow" data-old-line="2" data-line="2"><span class="cd-num">2</span>VAR</div>
  <div class="cd-drow" data-old-line="3"><span class="cd-num">3</span>-  run : BOOL;</div>
  <div class="cd-drow" data-line="3"><span class="cd-num">3</span>+  run : BOOL := TRUE;</div>
</div>`;

// A rendered body as SourceHtml draws it: data-line is the line within its SECTION, not the file
// line GitHub means. A thread placed by it would sit on the wrong line, which is why rendered views
// list threads instead of placing them until anchors exist (slice 3).
// A drawn interface: a member table parsed from the .xml layer, and the code body under it, which
// is a block of ANOTHER file, the .st — exactly why every block names its own file.
const renderedSource =
  '<div class="mt-page" data-src-path="src/Pump.xml"><table class="mt-table"><tbody>' +
  '<tr class="mt-row mt-modified" data-path="run" data-change="modified" data-anchors="r-new-0 r-old-0"><td class="mt-mark">M</td><td class="mt-name">run</td><td class="mt-type">BOOL</td></tr>' +
  '</tbody></table></div>' +
  '<div class="cd-page" data-src-path="src/Pump.st"><div class="cd-row" data-line="3" data-file-line="9"><span class="cd-num">3</span>run := TRUE;</div></div>';

const hostile = '<div class="md-page"><p>Why?<img src="x" onerror="window.__pwned=1"><script>window.__pwned=1</script></p></div>';

interface Options {
  stale?: boolean;
  writable?: boolean;
  /** What each write answers, by path suffix; done by default. */
  writes?: Record<string, { status: number; body: unknown }>;
  /** Where the server placed each thread on the drawing. */
  placements?: ThreadPlacement[];
}

let calls: { url: string; snapshot: string | null; method: string; csrf: string | null; body: Record<string, unknown> | null }[] = [];

function answer(url: string, o: Options): unknown {
  if (url.endsWith('/snapshot')) {
    return {
      token, number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-17T09:00:00Z', draft: false,
      baseRef: 'main', baseLabel: 'main', headLabel: 'fix', baseSha: 'c'.repeat(40), mergeBaseSha: 'b'.repeat(40), headSha: head,
      issuedAt: '2026-10-17T09:00:00Z', html: '<div class="md-page"><p>The delay was wrong.</p></div>', writable: o.writable ?? false,
    };
  }
  if (url.endsWith('/status')) {
    return {
      stale: o.stale ?? false, headMoved: o.stale ?? false, baseMoved: false, summaryError: null,
      summary: { state: 'OPEN', draft: false, merged: false, mergeable: 'MERGEABLE', reviewDecision: 'REVIEW_REQUIRED', labels: [], assignees: [], reviewers: [] },
      checksError: null, checks: [{ name: 'build', status: 'completed', conclusion: 'failure', url: 'https://ci.example/1', source: 'check-run' }],
    };
  }
  if (url.endsWith('/files')) {
    return {
      patchSource: 'pull', githubListCut: false, total: 1, truncated: false,
      files: [{ path: 'src/Pump.st', label: 'src/Pump.st', change: 'modified', previousPath: null, previousLabel: null, object: 'Pump', inline: true, inlineUnavailable: null }],
    };
  }
  if (url.includes('/files/view')) {
    const raw = url.includes('view=raw');
    return {
      coverage: raw ? 'raw' : 'partial',
      placements: raw ? [] : o.placements ?? [],
      view: { kind: raw ? 'diff' : 'table', title: 'Pump', path: 'src/Pump.st', html: raw ? rawDiff : renderedSource, notices: [], layers: ['src/Pump.st'], layerLabels: ['src/Pump.st'] },
    };
  }
  if (url.endsWith('/threads')) {
    const comment = (id: string, html: string) => ({ id, author: 'sam', when: '2026-10-17T09:00:00Z', html, markdown: 'text', commit: head, originalCommit: head, canEdit: true, canDelete: true });
    const may = { canReply: true, canResolve: true, canUnresolve: true };
    return {
      truncated: false,
      threads: [
        { id: 'NEW3', path: 'src/Pump.st', pathLabel: 'src/Pump.st', resolved: false, outdated: false, line: 3, startLine: null, side: 'RIGHT', originalLine: 3, subject: 'LINE', truncated: false, ...may, comments: [comment('c1', hostile)] },
        { id: 'OLD3', path: 'src/Pump.st', pathLabel: 'src/Pump.st', resolved: true, outdated: false, line: 3, startLine: null, side: 'LEFT', originalLine: 3, subject: 'LINE', truncated: false, ...may, comments: [comment('c2', '<p>was it ever?</p>')] },
        { id: 'GONE', path: 'src/Pump.st', pathLabel: 'src/Pump.st', resolved: false, outdated: true, line: null, startLine: null, side: 'RIGHT', originalLine: 9, subject: 'LINE', truncated: false, ...may, comments: [comment('c3', '<p>an old remark</p>')] },
      ],
    };
  }
  if (url.endsWith('/timeline')) {
    return { truncated: false, items: [{ kind: 'review', actor: 'sam', when: '2026-10-17T09:00:00Z', html: null, text: null, state: 'APPROVED', sha: null }] };
  }
  if (url.endsWith('/commits')) {
    return { truncated: false, headFound: true, commits: [
      { sha: head, headline: 'Fix the default', author: 'jo', when: '2026-10-17T09:00:00Z', inSnapshot: true },
      { sha: 'e'.repeat(40), headline: 'And another', author: 'jo', when: '2026-10-17T09:05:00Z', inSnapshot: false },
    ] };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'main', label: 'main', commit: 'x' }], truncated: false };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
}

async function render(path: string, o: Options = {}) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const headers = (init?.headers ?? {}) as Record<string, string>;
    const method = init?.method ?? 'GET';
    const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : null;
    calls.push({ url, snapshot: headers['X-Versio-Snapshot'] ?? null, method, csrf: headers['X-Versio-Request'] ?? null, body });
    if (method !== 'GET') {
      const scripted = Object.entries(o.writes ?? {}).find(([suffix]) => url.endsWith(suffix))?.[1];
      const reply = scripted ?? { status: 200, body: url.endsWith('/markdown') ? { html: '<p>preview</p>' } : { outcome: 'done', id: 'NEW' } };
      return new Response(JSON.stringify(reply.body), { status: reply.status, headers: { 'Content-Type': 'application/json' } });
    }
    return new Response(JSON.stringify(answer(url, o)), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/:owner/:repo/pull/:number" element={<PullPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  await settle(host);
  return { host, root };
}

async function settle(host: HTMLElement, until?: () => boolean) {
  for (let i = 0; i < 40 && !(until?.() ?? false); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
    if (!until && host.querySelector('.pane-head')) {
      // Let the dependent reads land too.
      for (let j = 0; j < 10; j++) {
        await act(async () => {
          await new Promise((r) => setTimeout(r, 5));
        });
      }
      return;
    }
  }
}

describe('PullPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    delete (window as { __pwned?: number }).__pwned;
  });

  it('carries the sealed snapshot on every review read and never puts it in a URL', async () => {
    const { root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw');

    const review = calls.filter((c) => c.url.includes('/pulls/12/') && !c.url.endsWith('/snapshot'));
    expect(review.length).toBeGreaterThan(2);
    expect(review.every((c) => c.snapshot === token)).toBe(true);
    expect(calls.some((c) => c.url.includes(token))).toBe(false);
    act(() => root.unmount());
  });

  it('places each thread under its own line and side on Raw, and lists the outdated one above', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw');

    const rows = Array.from(host.querySelectorAll('.cd-drow'));
    const newRow = rows.find((r) => r.getAttribute('data-line') === '3' && !r.hasAttribute('data-old-line'))!;
    const oldRow = rows.find((r) => r.getAttribute('data-old-line') === '3')!;
    expect(newRow.nextElementSibling?.querySelector('[data-thread="NEW3"]')).not.toBeNull();
    expect(oldRow.nextElementSibling?.querySelector('[data-thread="OLD3"]')).not.toBeNull();
    const unplaced = host.querySelector('.unplaced')!;
    expect(unplaced.querySelector('[data-thread="GONE"]')).not.toBeNull();
    expect(unplaced.querySelector('[data-thread="NEW3"]')).toBeNull();
    act(() => root.unmount());
  });

  it('on the drawing, says it may be partial and lists every thread rather than guessing where they go', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st');

    expect(host.querySelector('.review-partial')?.textContent).toContain('may not show every change');
    expect(host.querySelectorAll('.unplaced [data-thread]').length).toBe(3);
    act(() => root.unmount());
  });

  it('draws a hostile comment as text: no script runs and no handler survives', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw');

    const thread = host.querySelector('[data-thread="NEW3"]')!;
    expect(thread.textContent).toContain('Why?');
    expect(thread.querySelector('script')).toBeNull();
    expect(thread.querySelector('[onerror]')).toBeNull();
    expect((window as { __pwned?: number }).__pwned).toBeUndefined();
    act(() => root.unmount());
  });

  it('offers a reload when the pull request moved, and loads a new snapshot only when asked', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { stale: true });

    const banner = host.querySelector('.review-stale')!;
    expect(banner.textContent).toContain('changed after you opened it');
    expect(calls.filter((c) => c.url.endsWith('/snapshot')).length).toBe(1);
    await act(async () => {
      banner.querySelector('button')!.click();
    });
    await settle(host, () => calls.filter((c) => c.url.endsWith('/snapshot')).length === 2);
    expect(calls.filter((c) => c.url.endsWith('/snapshot')).length).toBe(2);
    act(() => root.unmount());
  });

  it('marks commits that arrived after the reviewed head', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=commits');

    const newer = host.querySelector('.commit-new');
    expect(newer?.textContent).toContain('And another');
    expect(newer?.textContent).toContain('new since you opened this review');
    expect(host.querySelectorAll('.commit-new').length).toBe(1);
    act(() => root.unmount());
  });
});

describe('threadSelector', () => {
  const base = { id: 't', path: 'a', pathLabel: 'a', resolved: false, outdated: false, startLine: null, originalLine: 1, truncated: false, canReply: false, canResolve: false, canUnresolve: false, comments: [] };

  it('uses the new side for RIGHT, the old for LEFT, and nothing for files or outdated threads', () => {
    expect(threadSelector({ ...base, line: 4, side: 'RIGHT', subject: 'LINE' })).toBe('[data-line="4"]');
    expect(threadSelector({ ...base, line: 4, side: 'LEFT', subject: 'LINE' })).toBe('[data-old-line="4"]');
    expect(threadSelector({ ...base, line: null, side: 'RIGHT', subject: 'LINE' })).toBeNull();
    expect(threadSelector({ ...base, line: 4, side: 'RIGHT', subject: 'FILE' })).toBeNull();
  });
});

describe('PullPage writes', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  async function type(textarea: HTMLTextAreaElement, text: string) {
    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!;
      setter.call(textarea, text);
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
  }

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    await settle(host, () => false);
  }

  const button = (root: ParentNode, label: string) => Array.from(root.querySelectorAll('button')).find((b) => b.textContent === label)!;
  const writes = () => calls.filter((c) => c.method !== 'GET' && !c.url.endsWith('/markdown'));

  it('draws no write control when the snapshot says nothing may be written', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw');

    expect(host.querySelector('.comment-box')).toBeNull();
    expect(button(host, 'Reply')).toBeUndefined();
    expect(button(host, 'Resolve')).toBeUndefined();
    act(() => root.unmount());
  });

  it('posts a conversation comment with the forgery header, an operation key and the snapshot, then re-reads', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    const box = host.querySelector('.comment-box')!;
    const before = calls.filter((c) => c.url.endsWith('/timeline')).length;

    await type(box.querySelector('textarea')!, 'Looks right.');
    await click(button(box, 'Comment'), host);

    const posted = writes()[0]!;
    expect(posted.url.endsWith('/pulls/12/comments')).toBe(true);
    expect(posted.csrf).toBe('1');
    expect(posted.snapshot).toBe(token);
    expect(posted.body?.body).toBe('Looks right.');
    expect(String(posted.body?.opKey)).toMatch(/^\d{13}-[A-Za-z0-9]{16,}$/);
    expect(calls.filter((c) => c.url.endsWith('/timeline')).length).toBeGreaterThan(before);
    expect(host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!.value).toBe('');
    act(() => root.unmount());
  });

  it('keeps the draft when the outcome is unknown, and the retry carries the same key', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': unknown } });
    const box = host.querySelector('.comment-box')!;

    await type(box.querySelector('textarea')!, 'Maybe.');
    await click(button(box, 'Comment'), host);
    expect(host.querySelector('.comment-note')?.textContent).toContain('may or may not');
    expect(host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!.value).toBe('Maybe.');
    await click(button(host.querySelector('.comment-box')!, 'Try again'), host);

    const [first, second] = writes();
    expect(first!.body?.opKey).toBe(second!.body?.opKey);
    act(() => root.unmount());
  });

  it('writes a line comment from a gutter click on Raw, on the side of the row clicked', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw', { writable: true });
    const oldRow = Array.from(host.querySelectorAll('.cd-drow')).find((r) => r.getAttribute('data-old-line') === '3' && !r.hasAttribute('data-line'))!;

    await click(oldRow.querySelector('.cd-num')!, host);
    const draft = host.querySelector('.thread-draft')!;
    expect(draft.textContent).toContain('old line 3');
    await type(draft.querySelector('textarea')!, 'Was this ever right?');
    await click(button(draft, 'Comment'), host);

    const posted = writes()[0]!;
    expect(posted.url.endsWith('/line-comments')).toBe(true);
    expect(posted.body).toMatchObject({ path: 'src/Pump.st', line: 3, side: 'LEFT', body: 'Was this ever right?' });
    act(() => root.unmount());
  });

  it('after a refusal nothing was posted, so the text unlocks and the next submission is a new operation', async () => {
    const refused = { status: 422, body: { error: 'line-not-in-diff', message: 'GitHub only takes comments on lines shown in its diff of this file.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': refused } });
    const box = host.querySelector('.comment-box')!;

    await type(box.querySelector('textarea')!, 'First try.');
    await click(button(box, 'Comment'), host);
    const textarea = host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!;
    expect(textarea.readOnly).toBe(false);
    expect(button(host.querySelector('.comment-box')!, 'Comment')).toBeDefined();
    await click(button(host.querySelector('.comment-box')!, 'Comment'), host);

    const [first, second] = writes();
    expect(first!.body?.opKey).not.toBe(second!.body?.opKey);
    act(() => root.unmount());
  });

  it('says the pull request moved when a write is refused as stale, and keeps the draft', async () => {
    const stale = { status: 409, body: { error: 'snapshot-stale', message: 'This pull request changed after you opened it.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': stale } });
    const box = host.querySelector('.comment-box')!;

    await type(box.querySelector('textarea')!, 'Hold on.');
    await click(button(box, 'Comment'), host);

    expect(host.querySelector('.review-stale')?.textContent).toContain('drafts are kept');
    expect(host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!.value).toBe('Hold on.');
    act(() => root.unmount());
  });

  it('resolves a thread with one click, naming the thread and the state wanted', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw', { writable: true });

    await click(button(host.querySelector('[data-thread="NEW3"]')!, 'Resolve'), host);

    const posted = writes()[0]!;
    expect(posted.url.endsWith('/threads/NEW3/resolve')).toBe(true);
    expect(posted.body?.resolved).toBe(true);
    act(() => root.unmount());
  });
});

describe('PullPage on a drawing', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  const placement = (threadId: string, rest: Partial<ThreadPlacement>): ThreadPlacement => ({
    threadId, outcome: 'unplaced', dom: null, reason: null, hintPath: null, hintSide: null, hintLine: null, ...rest,
  });

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    await settle(host, () => false);
  }

  const writes = () => calls.filter((c) => c.method !== 'GET' && !c.url.endsWith('/markdown'));

  it('puts a thread the server placed on a member row under that row, in a table row of its own', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', {
      placements: [placement('NEW3', { outcome: 'exact', dom: 'r-new-0' }), placement('GONE', { reason: 'gone' })],
    });

    const row = host.querySelector('tr[data-anchors~="r-new-0"]')!;
    expect(row.nextElementSibling?.matches('tr.thread-row')).toBe(true);
    expect(row.nextElementSibling?.querySelector('[data-thread="NEW3"]')).not.toBeNull();
    // Not placed by the server: listed, never guessed onto something.
    expect(host.querySelector('.unplaced [data-thread="GONE"]')).not.toBeNull();
    expect(host.querySelector('.unplaced [data-thread="NEW3"]')).toBeNull();
    act(() => root.unmount());
  });

  it('says so when a thread was found again on a renumbered element', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', {
      placements: [placement('NEW3', { outcome: 'moved', dom: 'r-new-0' })],
    });

    expect(host.querySelector('[data-thread="NEW3"]')?.textContent).toContain('moved here');
    act(() => root.unmount());
  });

  it('puts a hinted thread on the drawn line of ITS file, not on a line of the same number elsewhere', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', {
      placements: [placement('NEW3', { outcome: 'hint', hintPath: 'src/Pump.st', hintSide: 'RIGHT', hintLine: 9 })],
    });

    const line = host.querySelector('[data-src-path="src/Pump.st"] [data-file-line="9"]')!;
    expect(line.nextElementSibling?.querySelector('[data-thread="NEW3"]')).not.toBeNull();
    act(() => root.unmount());
  });

  it("opens a comment on a member from its name and posts only the element's id, never what it is", async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { writable: true });

    await click(host.querySelector('tr[data-anchors~="r-new-0"] td.mt-name')!, host);
    const draft = host.querySelector('.thread-draft')!;
    expect(draft.textContent).toContain('this member');
    await act(async () => {
      const textarea = draft.querySelector('textarea')!;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, 'Why TRUE?');
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await click(Array.from(draft.querySelectorAll('button')).find((b) => b.textContent === 'Comment')!, host);

    const posted = writes()[0]!;
    expect(posted.url.endsWith('/anchor-comments')).toBe(true);
    expect(posted.body).toMatchObject({ path: 'src/Pump.st', dom: 'r-new-0', body: 'Why TRUE?' });
    expect(Object.keys(posted.body!).sort()).toEqual(['body', 'dom', 'opKey', 'path']);
    act(() => root.unmount());
  });

  it("comments on a drawn code line as a line of the code's own file", async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { writable: true });

    await click(host.querySelector('[data-src-path="src/Pump.st"] .cd-num')!, host);
    const draft = host.querySelector('.thread-draft')!;
    await act(async () => {
      const textarea = draft.querySelector('textarea')!;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, 'And here?');
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await click(Array.from(draft.querySelectorAll('button')).find((b) => b.textContent === 'Comment')!, host);

    // File line 9 of the .st, not section line 3, and not a line of the .xml the table came from.
    expect(writes()[0]!.body).toMatchObject({ path: 'src/Pump.st', line: 9, side: 'RIGHT' });
    act(() => root.unmount());
  });
});

describe('placementSelector', () => {
  const base: ThreadPlacement = { threadId: 't', outcome: 'exact', dom: 'n-new-3', reason: null, hintPath: null, hintSide: null, hintLine: null };

  it('places only what the server placed, by an id of the shape renderers emit', () => {
    expect(placementSelector(base)).toBe('[data-anchors~="n-new-3"]');
    expect(placementSelector({ ...base, outcome: 'unplaced' })).toBeNull();
    expect(placementSelector({ ...base, dom: 'n-new-3"] , body [x' })).toBeNull();
  });

  it('keeps a hinted path inside its attribute', () => {
    const selector = placementSelector({ ...base, outcome: 'hint', dom: null, hintPath: 'a"b\\c.st', hintSide: 'LEFT', hintLine: 4 })!;
    expect(selector).toBe('[data-src-path="a\\"b\\\\c.st"] [data-old-file-line="4"]');
    expect(() => document.querySelector(selector)).not.toThrow();
  });
});
