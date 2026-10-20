import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CoverageGap, CoverageSummary, PendingReviewView, ThreadPlacement } from '../api';
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
  /** The viewer's pending review, if any. */
  pending?: PendingReviewView['review'];
  viewerIsAuthor?: boolean;
  lastReview?: string | null;
  viewed?: string[];
  /** The rendered file's coverage, and its gaps. */
  coverage?: 'complete' | 'partial';
  gaps?: CoverageGap[];
  /** The per-file summary the review dialog totals up. */
  summary?: CoverageSummary;
  /** The head moves the moment anything is written: a push landing mid-submit. */
  movesOnWrite?: boolean;
  /** The live title and description (changeable mid-test, as another person's edit would), and whether the viewer may edit. */
  live?: { title: string; markdown: string };
  canUpdate?: boolean;
  merged?: boolean;
  draftLive?: boolean;
  /** Whether the snapshot says the merge tier is on, and what the merge-state read answers. */
  canMerge?: boolean;
  merge?: Record<string, unknown>;
}

let calls: { url: string; snapshot: string | null; method: string; csrf: string | null; body: Record<string, unknown> | null }[] = [];

function answer(url: string, o: Options): unknown {
  if (url.endsWith('/snapshot')) {
    return {
      token, number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-17T09:00:00Z', draft: false,
      baseRef: 'main', baseLabel: 'main', headLabel: 'fix', baseSha: 'c'.repeat(40), mergeBaseSha: 'b'.repeat(40), headSha: head,
      issuedAt: '2026-10-17T09:00:00Z', html: '<div class="md-page"><p>The delay was wrong.</p></div>', writable: o.writable ?? false, canMerge: o.canMerge ?? false,
    };
  }
  if (url.endsWith('/status')) {
    const moved = (o.stale ?? false) || (o.movesOnWrite === true && calls.some((c) => c.method !== 'GET'));
    return {
      stale: moved, headMoved: moved, baseMoved: false, summaryError: null,
      summary: { state: 'OPEN', draft: o.draftLive ?? false, merged: o.merged ?? false, mergeable: 'MERGEABLE', reviewDecision: 'REVIEW_REQUIRED',
        labels: [{ name: 'plc', value: 'plc', color: '0e8a16' }], assignees: ['sam'],
        reviewers: [{ login: 'kim', state: 'REQUESTED' }], viewerIsAuthor: o.viewerIsAuthor ?? false, viewerLastReviewCommit: o.lastReview ?? null,
        title: o.live?.title ?? 'Fix interlock', titleText: o.live?.title ?? 'Fix interlock', markdown: o.live?.markdown ?? 'The delay was wrong.',
        html: `<div class="md-page"><p>${o.live?.markdown ?? 'The delay was wrong.'}</p></div>`, viewerCanUpdate: o.canUpdate ?? true },
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
      coverage: raw ? 'raw' : o.coverage ?? 'partial',
      gaps: raw ? [] : o.gaps ?? [],
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
  if (url.endsWith('/merge-state')) {
    return o.merge ?? { tier: false };
  }
  if (url.endsWith('/reviews/pending')) {
    return { review: o.pending ?? null };
  }
  if (url.endsWith('/viewed')) {
    return { paths: o.viewed ?? [] };
  }
  if (url.endsWith('/coverage')) {
    return o.summary ?? { files: [{ path: 'src/Pump.st', coverage: 'partial', gaps: 2 }] };
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
  if (url.endsWith('/labels')) {
    return { labels: [{ value: 'plc', label: 'plc', color: '0e8a16' }, { value: 'safety', label: 'safety', color: 'd73a4a' }] };
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
      // A string body is sent as it is: a proxy's HTML error page, not this server's JSON.
      return new Response(typeof reply.body === 'string' ? reply.body : JSON.stringify(reply.body), { status: reply.status, headers: { 'Content-Type': typeof reply.body === 'string' ? 'text/html' : 'application/json' } });
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

  // A gateway's 504 page or a server 500 may come after GitHub did the write: never a refusal.
  for (const [what, reply] of [
    ["a proxy's 504 page", { status: 504, body: '<html><body>Gateway Time-out</body></html>' }],
    ['a 500 with no outcome', { status: 500, body: { error: 'internal' } }],
  ] as const) {
    it(`keeps the key after ${what}, and the retry is the same operation`, async () => {
      const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': reply } });
      const box = host.querySelector('.comment-box')!;

      await type(box.querySelector('textarea')!, 'Maybe.');
      await click(button(box, 'Comment'), host);
      expect(host.querySelector('.comment-note')?.textContent).toContain('may or may not');
      await click(button(host.querySelector('.comment-box')!, 'Try again'), host);

      const [first, second] = writes();
      expect(second!.body?.opKey).toBe(first!.body?.opKey);
      act(() => root.unmount());
    });
  }

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

// Slice 4 (docs/web/PLAN.md, C2 and C4): a review is submitted on the commit it names, an older
// pending review only as what it is, and the dialog says what the reviewer may not have seen.
describe('PullPage review', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    await settle(host, () => false);
  }

  async function type(textarea: HTMLTextAreaElement, text: string) {
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, text);
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
  }

  const button = (root: ParentNode, label: string) => Array.from(root.querySelectorAll('button')).find((b) => b.textContent === label);
  const writes = () => calls.filter((c) => c.method !== 'GET' && !c.url.endsWith('/markdown'));
  const radio = (host: HTMLElement, value: string) => host.querySelector<HTMLInputElement>(`.review-dialog input[value="${value}"]`);
  const pendingOn = (commit: string) => ({ id: 'PRR_1', commit, onSnapshotHead: commit === head, comments: [{ id: 'C1', path: 'src/Pump.st', pathLabel: 'src/Pump.st', html: '<p>x</p>' }] });

  it('with no pending review, approves in one call on the reviewed head, then reads the review again', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    await click(button(host, 'Review changes')!, host);
    await click(radio(host, 'APPROVE')!, host);
    const before = calls.filter((c) => c.url.endsWith('/reviews/pending')).length;

    await click(button(host, 'Submit: Approve')!, host);

    const posted = writes()[0]!;
    expect(posted.url.endsWith('/pulls/12/reviews')).toBe(true);
    expect(posted.body).toMatchObject({ event: 'APPROVE', body: '' });
    expect(posted.body).not.toHaveProperty('commit');
    expect(posted.snapshot).toBe(token);
    expect(calls.filter((c) => c.url.endsWith('/reviews/pending')).length).toBeGreaterThan(before);
    expect(host.querySelector('.review-bar .notice')?.textContent).toContain(`Approve submitted on ${head.slice(0, 8)}`);
    act(() => root.unmount());
  });

  it('reads the status again right after a submit and says a push landed meanwhile, not at the next poll', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, movesOnWrite: true });
    await click(button(host, 'Review changes')!, host);
    await click(radio(host, 'APPROVE')!, host);
    const before = calls.filter((c) => c.url.endsWith('/status')).length;

    await click(button(host, 'Submit: Approve')!, host);

    expect(calls.filter((c) => c.url.endsWith('/status')).length).toBeGreaterThan(before);
    expect(host.querySelector('.review-bar .notice')?.textContent).toContain('your review stays on the commit you were shown');
    act(() => root.unmount());
  });

  it('after a submit whose answer was lost and a reload with no pending review left, the retry goes where the key was sent', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const first = await render('/acme/plant/pull/12', { writable: true, pending: pendingOn(head), writes: { '/reviews/PRR_1/submit': unknown } });
    await click(button(first.host, 'Finish your review')!, first.host);
    await click(radio(first.host, 'APPROVE')!, first.host);
    await click(button(first.host, 'Submit: Approve')!, first.host);
    const before = writes();
    act(() => first.root.unmount());
    document.body.innerHTML = '';

    // GitHub did submit it: the pending review is gone, which on its own would point at /reviews.
    const again = await render('/acme/plant/pull/12', { writable: true });
    await click(button(again.host, 'Review changes')!, again.host);
    expect(again.host.querySelector('.review-dialog')?.textContent).toContain('may or may not have gone through');
    await click(button(again.host, 'Try again')!, again.host);

    const sent = [...before, ...writes()];
    expect(sent.map((w) => w.url.slice(w.url.indexOf('/reviews')))).toEqual(['/reviews/PRR_1/submit', '/reviews/PRR_1/submit']);
    expect(sent[1]!.body?.opKey).toBe(sent[0]!.body?.opKey);
    act(() => again.root.unmount());
  });

  it('submits a pending review made on the reviewed head as that review', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, pending: pendingOn(head) });
    expect(host.querySelector('.review-bar')?.textContent).toContain('1 comment');
    await click(button(host, 'Finish your review')!, host);
    await click(button(host, 'Submit: Comment')!, host);

    expect(writes()[0]!.url.endsWith('/reviews/PRR_1/submit')).toBe(true);
    act(() => root.unmount());
  });

  it('submits an older pending review only as historical, naming its commit, and takes no more comments into it', async () => {
    const older = 'a'.repeat(40);
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { writable: true, pending: pendingOn(older) });
    expect(host.querySelector('.review-bar')?.textContent).toContain(older.slice(0, 8));
    await click(button(host, 'Finish your review')!, host);
    expect(host.querySelector('.review-dialog')?.textContent).toContain(`started on commit ${older.slice(0, 8)}`);

    await click(button(host, 'Submit: Comment')!, host);
    const posted = writes()[0]!;
    expect(posted.url.endsWith('/reviews/PRR_1/submit-historical')).toBe(true);
    expect(posted.body?.commit).toBe(older);

    await click(button(host, 'Comment on the whole file')!, host);
    expect(button(host.querySelector('.file-comment')!, 'Add to review')).toBeUndefined();
    expect(button(host.querySelector('.file-comment')!, 'Start a review')).toBeUndefined();
    act(() => root.unmount());
  });

  it('offers a comment into a pending review on the reviewed head as "Add to review"', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { writable: true, pending: pendingOn(head) });
    await click(button(host, 'Comment on the whole file')!, host);

    expect(button(host.querySelector('.file-comment')!, 'Add to review')).toBeDefined();
    act(() => root.unmount());
  });

  it('does not offer the author an approval or a request for changes on their own pull request', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, viewerIsAuthor: true });
    await click(button(host, 'Review changes')!, host);

    expect(radio(host, 'COMMENT')).not.toBeNull();
    expect(radio(host, 'APPROVE')).toBeNull();
    expect(radio(host, 'REQUEST_CHANGES')).toBeNull();
    act(() => root.unmount());
  });

  it('asks for words before a request for changes', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    await click(button(host, 'Review changes')!, host);
    await click(radio(host, 'REQUEST_CHANGES')!, host);

    expect(button(host, 'Submit: Request changes')!.disabled).toBe(true);
    await type(host.querySelector<HTMLTextAreaElement>('.review-dialog textarea')!, 'The delay must stay.');
    expect(button(host, 'Submit: Request changes')!.disabled).toBe(false);
    act(() => root.unmount());
  });

  it('reads coverage only when the dialog opens, and lists unopened files and files the drawing may not show', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    expect(calls.some((c) => c.url.endsWith('/coverage'))).toBe(false);

    await click(button(host, 'Review changes')!, host);

    expect(calls.some((c) => c.url.endsWith('/coverage'))).toBe(true);
    const notes = Array.from(host.querySelectorAll('.review-notes summary')).map((s) => s.textContent);
    expect(notes).toContain('1 file not opened here');
    expect(notes.some((n) => n?.includes('1 file with changes the drawing may not show'))).toBe(true);
    act(() => root.unmount());
  });

  it('does not count an opened, completely drawn file against the approval', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', {
      writable: true, coverage: 'complete', summary: { files: [{ path: 'src/Pump.st', coverage: 'complete', gaps: 0 }] },
    });
    await click(button(host, 'Review changes')!, host);

    expect(host.querySelectorAll('.review-notes')).toHaveLength(0);
    act(() => root.unmount());
  });

  it('adds a drafted comment to the review, and a retry after an unknown outcome goes nowhere else', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { writable: true, writes: { '/reviews/pending/threads': unknown } });

    await click(host.querySelector('tr[data-anchors~="r-new-0"] td.mt-name')!, host);
    const draft = () => host.querySelector('.thread-draft')!;
    await type(draft().querySelector('textarea')!, 'Why TRUE?');
    await click(button(draft(), 'Start a review')!, host);

    const first = writes()[0]!;
    expect(first.url.endsWith('/reviews/pending/threads')).toBe(true);
    expect(first.body).toMatchObject({ path: 'src/Pump.st', dom: 'r-new-0', body: 'Why TRUE?' });
    // The key belongs to the review now: the other target is not offered beside the retry.
    expect(button(draft(), 'Start a review')).toBeUndefined();
    await click(button(draft(), 'Try again')!, host);

    const second = writes()[1]!;
    expect(second.url.endsWith('/reviews/pending/threads')).toBe(true);
    expect(second.body?.opKey).toBe(first.body?.opKey);
    act(() => root.unmount());
  });

  it('says a complete drawing is complete, and lists each gap of a partial one', async () => {
    const complete = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { coverage: 'complete' });
    expect(complete.host.querySelector('.review-complete')).not.toBeNull();
    expect(complete.host.querySelector('.review-partial')).toBeNull();
    act(() => complete.root.unmount());
    document.body.innerHTML = '';

    const gaps: CoverageGap[] = [
      { path: 'src/Pump.xml', pathLabel: 'src/Pump.xml', side: 'new', start: 4, end: 6, reason: 'not-shown' },
      { path: 'src/Pump.st', pathLabel: 'src/Pump.st', side: 'old', start: 0, end: 0, reason: 'no-evidence' },
    ];
    const partial = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { gaps });
    const items = Array.from(partial.host.querySelectorAll('.review-gaps li')).map((li) => li.textContent);
    expect(items).toEqual([
      'src/Pump.xml, as it is, lines 4–6: changed, but not shown as changed',
      'src/Pump.st, as it was, the whole file: this kind of drawing cannot yet say what it shows',
    ]);
    expect(partial.host.querySelector('.review-complete')).toBeNull();
    act(() => partial.root.unmount());
  });

  it('marks a file viewed on GitHub, and takes the mark back when GitHub refuses', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', {
      writable: true, writes: { '/viewed': { status: 403, body: { error: 'refused', message: 'No.' } } },
    });
    const box = host.querySelector<HTMLInputElement>('.changes input.review-viewed')!;
    expect(box.checked).toBe(false);

    await click(box, host);

    expect(writes()[0]!.body).toMatchObject({ path: 'src/Pump.st', viewed: true });
    expect(host.querySelector<HTMLInputElement>('.changes input.review-viewed')!.checked).toBe(false);
    act(() => root.unmount());
  });

  it("shows GitHub's viewed marks", async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st', { writable: true, viewed: ['src/Pump.st'] });
    expect(host.querySelector<HTMLInputElement>('.changes input.review-viewed')!.checked).toBe(true);
    act(() => root.unmount());
  });

  it('links the changes since the viewer last reviewed, and only when there are some', async () => {
    const last = 'f'.repeat(40);
    const moved = await render('/acme/plant/pull/12', { lastReview: last });
    const link = Array.from(moved.host.querySelectorAll('a')).find((a) => a.textContent?.startsWith('Changes since your last review'));
    expect(link?.getAttribute('href')).toBe(`/acme/plant/compare/${last}...${head}`);
    act(() => moved.root.unmount());
    document.body.innerHTML = '';

    const same = await render('/acme/plant/pull/12', { lastReview: head });
    expect(Array.from(same.host.querySelectorAll('a')).some((a) => a.textContent?.startsWith('Changes since'))).toBe(false);
    act(() => same.root.unmount());
  });

  it('discards a pending review only after the person confirms', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, pending: pendingOn(head) });

    await click(button(host, 'Discard')!, host);
    expect(writes()).toHaveLength(0);
    await click(button(host, 'Discard')!, host);

    expect(confirm).toHaveBeenCalledTimes(2);
    expect(writes()[0]!.url.endsWith('/reviews/PRR_1/discard')).toBe(true);
    act(() => root.unmount());
  });

  it('asks for a review by login and withdraws an open request', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    const input = host.querySelector<HTMLInputElement>('.reviewers-add input')!;
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, 'lee');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await click(button(host, 'Ask')!, host);
    await click(button(host, 'remove')!, host);

    expect(writes().map((w) => w.body)).toMatchObject([{ add: ['lee'], remove: [] }, { add: [], remove: ['kim'] }]);
    act(() => root.unmount());
  });
});

// Slice 5 (docs/web/PLAN.md): what a pull request says about itself, edited from what was shown —
// never from whatever the last poll brought in, which may be someone else's newer text.
describe('PullPage metadata', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    await settle(host, () => false);
  }

  async function fill(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string) {
    await act(async () => {
      const proto = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : element instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value')!.set!.call(element, value);
      element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }));
    });
  }

  /** A poll, as the page makes one when it becomes visible again. */
  async function poll(host: HTMLElement) {
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await settle(host, () => false);
  }

  const button = (root: ParentNode, label: string) => Array.from(root.querySelectorAll('button')).find((b) => b.textContent === label);
  const writes = () => calls.filter((c) => c.method !== 'GET' && !c.url.endsWith('/markdown'));

  it('edits the title naming the one it replaces, as it was when editing began, and reads the state again after', async () => {
    const o = { writable: true, live: { title: 'Fix interlock', markdown: 'The delay was wrong.' } };
    const { host, root } = await render('/acme/plant/pull/12', o);
    await click(button(host.querySelector('h1')!, 'Edit')!, host);
    // Someone else renames it while this person types: the poll brings their title in.
    o.live = { title: 'Fix interlock (jo)', markdown: 'The delay was wrong.' };
    await poll(host);
    await fill(host.querySelector<HTMLInputElement>('.title-editor input')!, 'Fix the interlock delay');
    const before = calls.filter((c) => c.url.endsWith('/status')).length;

    await click(button(host.querySelector('.title-editor')!, 'Save')!, host);

    const posted = writes()[0]!;
    expect(posted.url.endsWith('/pulls/12/edit')).toBe(true);
    expect(posted.body).toMatchObject({ title: 'Fix the interlock delay', expectTitle: 'Fix interlock' });
    expect(calls.filter((c) => c.url.endsWith('/status')).length).toBeGreaterThan(before);
    act(() => root.unmount());
  });

  it('edits the description from the text it showed, and a draft kept over a reload keeps the text it was written against', async () => {
    const o = { writable: true, live: { title: 'Fix interlock', markdown: 'The delay was wrong.' } };
    const first = await render('/acme/plant/pull/12', o);
    await click(button(first.host, 'Edit the description')!, first.host);
    await fill(first.host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!, 'The delay was 5 s; it is 2 s.');
    act(() => first.root.unmount());
    document.body.innerHTML = '';

    // A reload later, someone else has changed the description: the kept draft still names the old text.
    o.live = { title: 'Fix interlock', markdown: 'Jo rewrote this.' };
    const again = await render('/acme/plant/pull/12', o);
    await click(button(again.host, 'Edit the description')!, again.host);
    expect(again.host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!.value).toBe('The delay was 5 s; it is 2 s.');
    await click(button(again.host.querySelector('.comment-box')!, 'Save')!, again.host);

    expect(writes()[0]!.body).toMatchObject({ body: 'The delay was 5 s; it is 2 s.', expectBody: 'The delay was wrong.' });
    act(() => again.root.unmount());
  });

  it('offers no edit to someone GitHub does not let edit', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canUpdate: false });

    expect(button(host.querySelector('h1')!, 'Edit')).toBeUndefined();
    expect(button(host, 'Edit the description')).toBeUndefined();
    expect(button(host, 'Close')).toBeUndefined();
    expect(host.querySelector('.labels-editor')).toBeNull();
    act(() => root.unmount());
  });

  it('closes only after the person confirms, and converts to a draft', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });

    await click(button(host, 'Close')!, host);
    expect(writes()).toHaveLength(0);
    await click(button(host, 'Close')!, host);
    await click(button(host, 'Convert to draft')!, host);

    expect(confirm).toHaveBeenCalledTimes(2);
    expect(writes().map((w) => [w.url.slice(w.url.lastIndexOf('/')), w.body?.open ?? w.body?.draft])).toEqual([['/state', false], ['/draft', true]]);
    act(() => root.unmount());
  });

  it('offers neither close nor draft on a merged pull request', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, merged: true });

    expect(button(host, 'Close')).toBeUndefined();
    expect(button(host, 'Convert to draft')).toBeUndefined();
    act(() => root.unmount());
  });

  it("takes a label off by GitHub's own name, offers only labels not on, and assigns by login", async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    const offered = Array.from(host.querySelectorAll('.labels-editor option')).map((o) => o.getAttribute('value'));
    expect(offered).toEqual(['', 'safety']);

    await click(host.querySelector('[aria-label="Remove the label plc"]')!, host);
    await fill(host.querySelector<HTMLSelectElement>('.labels-editor select')!, 'safety');
    await fill(host.querySelector<HTMLInputElement>('.assignees-editor input')!, 'lee');
    await click(button(host.querySelector('.assignees-editor')!, 'Assign')!, host);

    expect(writes().map((w) => [w.url.slice(w.url.lastIndexOf('/')), w.body?.add, w.body?.remove])).toEqual([
      ['/labels', [], ['plc']],
      ['/labels', ['safety'], []],
      ['/assignees', ['lee'], []],
    ]);
    act(() => root.unmount());
  });

  it('changes the base, and then says the pull request changed as for a push', async () => {
    const o: { writable: boolean; stale?: boolean } = { writable: true };
    const { host, root } = await render('/acme/plant/pull/12', o);
    await click(button(host, 'change base')!, host);
    // The fixture's only other branch is main itself; a second one is offered once the list has it.
    expect(host.querySelector('.base-editor select')).not.toBeNull();
    o.stale = true;
    await click(button(host, 'Cancel')!, host);
    await poll(host);

    expect(host.querySelector('.review-stale')).not.toBeNull();
    act(() => root.unmount());
  });
});

// Slice 6 (docs/web/PLAN.md, S11): the merge box merges only the reviewed commit, only when anyone
// could, and asks first in words that name the pull request, its branches and the commit.
describe('PullPage merge box', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    await settle(host, () => false);
  }

  const button = (root: ParentNode, label: string) => Array.from(root.querySelectorAll('button')).find((b) => b.textContent === label);
  const writes = () => calls.filter((c) => c.method !== 'GET' && !c.url.endsWith('/markdown'));
  const ready = { tier: true, state: 'OPEN', merged: false, headSha: head, mergeStateStatus: 'CLEAN', mergeable: true, blocked: null,
    methods: ['MERGE', 'SQUASH'], autoMergeAllowed: true, autoMerge: null, canDisableAutoMerge: false, mergeCommit: null };

  it('draws nothing and asks nothing without the merge tier', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: false, merge: ready });

    expect(host.querySelector('.merge-box')).toBeNull();
    expect(calls.some((c) => c.url.endsWith('/merge-state'))).toBe(false);
    act(() => root.unmount());
  });

  it('asks first, naming the pull request, its branches, the commit and the method, and merges only on yes', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: ready });
    const box = () => host.querySelector('.merge-box')!;
    await act(async () => {
      const select = box().querySelector<HTMLSelectElement>('select')!;
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')!.set!.call(select, 'SQUASH');
      select.dispatchEvent(new Event('change', { bubbles: true }));
    });

    await click(button(box(), 'Merge...')!, host);
    expect(box().querySelector('.merge-confirm')?.textContent).toContain(`Merge #12 (fix → main) at commit ${head.slice(0, 8)} by squash`);
    await click(button(box(), 'Cancel')!, host);
    expect(writes()).toHaveLength(0);

    await click(button(box(), 'Merge...')!, host);
    await click(button(box(), 'Confirm')!, host);
    const posted = writes()[0]!;
    expect(posted.url.endsWith('/pulls/12/merge')).toBe(true);
    expect(posted.body).toMatchObject({ method: 'SQUASH' });
    expect(posted.snapshot).toBe(token);
    act(() => root.unmount());
  });

  it('offers no merge past what protects the branch, and auto-merge only with a yes to later commits being merged', async () => {
    const blocked = { ...ready, mergeStateStatus: 'BLOCKED', mergeable: false, blocked: 'Required reviews or checks have not passed.' };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: blocked });
    const box = () => host.querySelector('.merge-box')!;

    expect(box().textContent).toContain('Required reviews or checks have not passed.');
    expect(button(box(), 'Merge...')).toBeUndefined();
    await click(button(box(), 'Merge automatically when ready...')!, host);
    // What GitHub does, not what it was once said to: later pushes by writers are merged too.
    expect(box().querySelector('.merge-confirm')?.textContent).toContain('commits pushed after your review');
    expect(box().querySelector('.merge-confirm')?.textContent).not.toContain('only commit');
    expect(button(box(), 'Confirm')!.disabled).toBe(true);
    await click(box().querySelector('.merge-accept input')!, host);
    await click(button(box(), 'Confirm')!, host);

    expect(writes()[0]!.url.endsWith('/auto-merge')).toBe(true);
    expect(writes()[0]!.body).toMatchObject({ enable: true, method: 'MERGE', acceptsLaterCommits: true });
    act(() => root.unmount());
  });

  it('brings a branch that is behind up to date, after asking', async () => {
    const behind = { ...ready, mergeStateStatus: 'BEHIND', mergeable: false, blocked: 'The branch must be brought up to date with its base first.' };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: behind });

    await click(button(host.querySelector('.merge-box')!, 'Update branch...')!, host);
    await click(button(host.querySelector('.merge-box')!, 'Confirm')!, host);

    expect(writes()[0]!.url.endsWith('/update-branch')).toBe(true);
    act(() => root.unmount());
  });

  it('offers nothing when GitHub cannot say, or when the pull request has moved past the reviewed commit', async () => {
    const unknown = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: { tier: true, error: 'upstream' } });
    expect(unknown.host.querySelector('.merge-box')?.textContent).toContain('no merge is offered');
    expect(unknown.host.querySelectorAll('.merge-box button')).toHaveLength(0);
    act(() => unknown.root.unmount());
    document.body.innerHTML = '';

    const moved = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: { ...ready, headSha: 'f'.repeat(40) } });
    expect(moved.host.querySelector('.merge-box')?.textContent).toContain('Load the latest to merge it');
    expect(button(moved.host.querySelector('.merge-box')!, 'Merge...')).toBeUndefined();
    act(() => moved.root.unmount());
  });

  it('says auto-merge is on and turns it off, and says a merged pull request is merged', async () => {
    const on = await render('/acme/plant/pull/12', { writable: true, canMerge: true,
      merge: { ...ready, mergeable: false, mergeStateStatus: 'BLOCKED', blocked: 'x', autoMerge: { method: 'SQUASH', by: 'jo' }, canDisableAutoMerge: true } });
    expect(on.host.querySelector('.merge-auto')?.textContent).toContain('set by jo');
    await click(button(on.host.querySelector('.merge-box')!, 'Turn off')!, on.host);
    await click(button(on.host.querySelector('.merge-box')!, 'Confirm')!, on.host);
    expect(writes()[0]!.body).toMatchObject({ enable: false });
    act(() => on.root.unmount());
    document.body.innerHTML = '';

    const merged = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: { ...ready, merged: true, state: 'MERGED', mergeCommit: 'a'.repeat(40) } });
    expect(merged.host.querySelector('.merge-box')?.textContent).toContain('Merged as aaaaaaaa');
    act(() => merged.root.unmount());
  });
});
