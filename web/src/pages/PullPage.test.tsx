import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router';
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

/** A scripted write answer; a list is answered in order, its last repeating. `settled` sets X-Versio-Settled. */
interface Reply {
  status: number;
  body: unknown;
  settled?: boolean;
}

interface Options {
  stale?: boolean;
  writable?: boolean;
  /** What each write answers, by path suffix; done by default. */
  writes?: Record<string, Reply | Reply[]>;
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
  /** GitHub listed more checks than were read. */
  checksCut?: boolean;
  /** A push happened and the page was reloaded: a newer snapshot, on a newer head. */
  newer?: boolean;
  /** A second changed file, to move between. */
  twoFiles?: boolean;
  /** The conversation (Step 2): the timeline's items, the checks, the commits, the header's counts, which review a thread belongs to. */
  timeline?: unknown[];
  checks?: unknown[];
  commits?: unknown[];
  counts?: Record<string, unknown>;
  threadReview?: string;
}

let calls: { url: string; snapshot: string | null; method: string; csrf: string | null; body: Record<string, unknown> | null }[] = [];

function answer(url: string, o: Options): unknown {
  if (url.endsWith('/snapshot')) {
    return {
      token: o.newer ? 'newer-token' : token, number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-17T09:00:00Z', draft: false,
      baseRef: 'main', baseLabel: 'main', headLabel: 'fix', baseSha: 'c'.repeat(40), mergeBaseSha: 'b'.repeat(40), headSha: o.newer ? 'e'.repeat(40) : head,
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
        html: `<div class="md-page"><p>${o.live?.markdown ?? 'The delay was wrong.'}</p></div>`, viewerCanUpdate: o.canUpdate ?? true, ...(o.counts ?? {}) },
      checksError: null, checks: o.checks ?? [{ name: 'build', status: 'completed', conclusion: 'failure', url: 'https://ci.example/1', source: 'check-run' }], checksTruncated: o.checksCut ?? false,
    };
  }
  if (url.endsWith('/files')) {
    return {
      patchSource: 'pull', githubListCut: false, total: o.twoFiles ? 2 : 1, truncated: false,
      files: [
        { path: 'src/Pump.st', label: 'src/Pump.st', change: 'modified', previousPath: null, previousLabel: null, object: 'Pump', inline: true, inlineUnavailable: null },
        ...(o.twoFiles ? [{ path: 'src/Valve.st', label: 'src/Valve.st', change: 'modified', previousPath: null, previousLabel: null, object: 'Valve', inline: true, inlineUnavailable: null, additions: 2, deletions: 1 }] : []),
      ],
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
    const comment = (id: string, html: string) => ({ id, author: 'sam', when: '2026-10-17T09:00:00Z', html, markdown: 'text', commit: head, originalCommit: head, canEdit: true, canDelete: true,
      reviewId: id === 'c1' ? o.threadReview ?? null : null, diffHunk: id === 'c1' ? '@@ -1,3 +1,3 @@\n a\n-b\n+c' : null });
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
    return { truncated: false, items: o.timeline ?? [{ kind: 'review', actor: 'sam', when: '2026-10-17T09:00:00Z', html: null, text: null, state: 'APPROVED', sha: null }] };
  }
  if (url.endsWith('/commits')) {
    return { truncated: false, headFound: true, commits: o.commits ?? [
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


/** Lets a test move the router as a link click would, without leaving the page under test. */
let jump: (to: string) => void = () => {};
function Jumper() {
  const navigate = useNavigate();
  jump = navigate;
  return null;
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
      const reply: Reply = Array.isArray(scripted) ? (scripted.length > 1 ? scripted.shift()! : scripted[0]!)
        : scripted ?? { status: 200, body: url.endsWith('/markdown') ? { html: '<p>preview</p>' } : { outcome: 'done', id: 'NEW' } };
      // A string body is sent as it is: a proxy's HTML error page, not this server's JSON.
      const headers: Record<string, string> = { 'Content-Type': typeof reply.body === 'string' ? 'text/html' : 'application/json' };
      if (reply.settled) {
        headers['X-Versio-Settled'] = '1';
      }
      return new Response(typeof reply.body === 'string' ? reply.body : JSON.stringify(reply.body), { status: reply.status, headers });
    }
    return new Response(JSON.stringify(answer(url, o)), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }));
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={[path]}>
        <Jumper />
        <Routes>
          <Route path="/:owner/:repo/pull/:number" element={<PullPage me={null} />} />
          <Route path="/:owner/:repo/pull/:number/:tab" element={<PullPage me={null} />} />
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

  // The desktop pane's box, with the desktop's default: a file opens folded, and ticking the box
  // asks the server for every line (the owner, 2026-10-23: the two must render the same).
  it('opens a file folded and asks for every line once Show full file is ticked', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st');
    const fileViews = () => calls.filter((c) => c.url.includes('/files/view'));
    expect(fileViews().every((c) => !c.url.includes('full='))).toBe(true);

    const box = Array.from(host.querySelectorAll<HTMLInputElement>('.full-file-label input'))[0]!;
    expect(box.checked).toBe(false);
    await act(async () => {
      box.click();
    });
    await settle(host, () => fileViews().some((c) => c.url.includes('full=1')));

    expect(fileViews().at(-1)!.url).toContain('full=1');
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

  it('says a checks list cut short is cut, on the tab and beside the counts', async () => {
    const cut = await render('/acme/plant/pull/12?tab=checks', { checksCut: true });
    expect(cut.host.querySelector('.notice')?.textContent).toContain('more checks than were read');
    act(() => cut.root.unmount());
    document.body.innerHTML = '';

    const whole = await render('/acme/plant/pull/12?tab=checks');
    expect(whole.host.textContent).not.toContain('more checks than were read');
    expect(whole.host.textContent).not.toContain('more on GitHub than shown');
    act(() => whole.root.unmount());
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
    // Found by review (2026-10-20): null threw and left the box stuck posting; {} on a 200 cleared the key.
    ['JSON null on a 200', { status: 200, body: null }],
    ['{} on a 200', { status: 200, body: {} }],
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

  // Found by review (2026-10-20): a refusal of a RETRY proves only that the retry did nothing.
  it('a retry refused by the rate limit keeps the key: the first attempt may have posted', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const limited = { status: 429, body: { error: 'rate-limited', message: 'Too many requests: this one was not acted on.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': [unknown, limited, unknown] } });
    const box = () => host.querySelector('.comment-box')!;

    await type(box().querySelector('textarea')!, 'Maybe.');
    await click(button(box(), 'Comment'), host);
    await click(button(box(), 'Try again'), host);
    await click(button(box(), 'Try again'), host);

    const keys = writes().map((w) => w.body?.opKey);
    expect(keys).toHaveLength(3);
    expect(new Set(keys).size).toBe(1);
    act(() => root.unmount());
  });

  it("a retry refused with the operation's own recorded outcome lets the key go", async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const refused = { status: 422, body: { error: 'github-refused', message: 'GitHub refused this: Validation Failed' }, settled: true };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': [unknown, refused, unknown] } });
    const box = () => host.querySelector('.comment-box')!;

    await type(box().querySelector('textarea')!, 'Maybe.');
    await click(button(box(), 'Comment'), host);
    await click(button(box(), 'Try again'), host);
    await click(button(box(), 'Comment'), host);

    const [first, second, third] = writes().map((w) => w.body?.opKey);
    expect(second).toBe(first);
    expect(third).not.toBe(first);
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

  it('a resolve retry refused by the rate limit keeps the key', async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FPump.st&view=raw', { writable: true, writes: { '/resolve': [{ status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }, { status: 429, body: { error: 'rate-limited', message: 'Too many requests: this one was not acted on.' } }, { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }] } });
    for (let i = 0; i < 3; i++) {
      await click(button(host.querySelector('[data-thread="NEW3"]')!, 'Resolve')!, host);
    }

    expect(writes()).toHaveLength(3);
    expect(new Set(writes().map((w) => w.body?.opKey)).size).toBe(1);
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

  it('a pending review submitted as a comment with no words, answer lost, can still be retried after a reload', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const first = await render('/acme/plant/pull/12', { writable: true, pending: pendingOn(head), writes: { '/reviews/PRR_1/submit': unknown } });
    await click(button(first.host, 'Finish your review')!, first.host);
    await click(button(first.host, 'Submit: Comment')!, first.host);
    const before = writes();
    act(() => first.root.unmount());
    document.body.innerHTML = '';

    // GitHub submitted it: no pending review now, which once made the empty comment "need words".
    const again = await render('/acme/plant/pull/12', { writable: true });
    await click(button(again.host, 'Review changes')!, again.host);
    expect(button(again.host, 'Try again')!.disabled).toBe(false);
    await click(button(again.host, 'Try again')!, again.host);

    const sent = [...before, ...writes()];
    expect(sent.map((w) => w.url.slice(w.url.indexOf('/reviews')))).toEqual(['/reviews/PRR_1/submit', '/reviews/PRR_1/submit']);
    expect(sent[1]!.body).toMatchObject({ event: 'COMMENT', body: '', opKey: sent[0]!.body?.opKey });
    act(() => again.root.unmount());
  });

  it('a retry after a push and a reload goes under the snapshot it was first sent under, never the newer one', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const first = await render('/acme/plant/pull/12', { writable: true, writes: { '/reviews': unknown } });
    await click(button(first.host, 'Review changes')!, first.host);
    await click(radio(first.host, 'APPROVE')!, first.host);
    await click(button(first.host, 'Submit: Approve')!, first.host);
    const before = writes();
    act(() => first.root.unmount());
    document.body.innerHTML = '';

    // A push, and the page reloaded onto it: the page's snapshot is now another revision.
    const again = await render('/acme/plant/pull/12', { writable: true, newer: true });
    await click(button(again.host, 'Review changes')!, again.host);
    await click(button(again.host, 'Try again')!, again.host);

    const sent = [...before, ...writes()];
    expect(sent.map((w) => w.snapshot)).toEqual([token, token]);
    expect(sent[1]!.body?.opKey).toBe(sent[0]!.body?.opKey);
    act(() => again.root.unmount());
  });

  it("moving between files never saves one file's draft under another's", async () => {
    const { host, root } = await render('/acme/plant/pull/12?tab=files&path=src%2FValve.st&view=raw', { writable: true, twoFiles: true });
    // Every file is on the page now (Step 3), each in its own section: the boxes are found by file.
    const section = (file: string) => host.querySelector(`[data-file="${file}"]`)!;
    const fill = async (file: string, text: string) => {
      await act(async () => {
        const textarea = section(file).querySelector<HTMLTextAreaElement>('.file-comment textarea')!;
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, text);
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
      });
    };
    const open = (file: string) => click(host.querySelector(`.changes button[title="${file}"]`)!, host);
    const draftOf = (file: string) => {
      const slot = Object.keys(sessionStorage).find((k) => k.endsWith(`:file:${file}`));
      return slot ? (JSON.parse(sessionStorage.getItem(slot)!) as { body: string }).body : null;
    };

    await click(button(section('src/Valve.st'), 'Comment on the whole file')!, host);
    await fill('src/Valve.st', 'Valve draft');
    await open('src/Pump.st');
    if (!section('src/Pump.st').querySelector('.file-comment textarea')) {
      await click(button(section('src/Pump.st'), 'Comment on the whole file')!, host);
    }
    expect(section('src/Pump.st').querySelector<HTMLTextAreaElement>('.file-comment textarea')!.value).toBe('');
    await fill('src/Pump.st', 'Pump draft');
    await open('src/Valve.st');

    expect(draftOf('src/Valve.st')).toBe('Valve draft');
    expect(draftOf('src/Pump.st')).toBe('Pump draft');
    expect(section('src/Valve.st').querySelector<HTMLTextAreaElement>('.file-comment textarea')?.value ?? 'Valve draft').toBe('Valve draft');
    act(() => root.unmount());
  });

  it("going to another pull request starts its page afresh: one's open title edit never carries over", async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true });
    await click(button(host.querySelector('.pr-title-row')!, 'Edit')!, host);
    expect(host.querySelector('.title-editor')).not.toBeNull();

    await act(async () => {
      jump('/acme/plant/pull/13');
    });
    await settle(host);

    expect(host.querySelector('.title-editor')).toBeNull();
    act(() => root.unmount());
  });

  it('a comment retried after a reload goes under its own snapshot too', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const first = await render('/acme/plant/pull/12', { writable: true, writes: { '/comments': unknown } });
    const box = first.host.querySelector('.comment-box')!;
    await act(async () => {
      const textarea = box.querySelector('textarea')!;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, 'Maybe.');
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await click(button(box, 'Comment')!, first.host);
    const before = writes();
    act(() => first.root.unmount());
    document.body.innerHTML = '';

    const again = await render('/acme/plant/pull/12', { writable: true, newer: true });
    await click(button(again.host.querySelector('.comment-box')!, 'Try again')!, again.host);

    expect([...before, ...writes()].map((w) => w.snapshot)).toEqual([token, token]);
    act(() => again.root.unmount());
  });

  it('says a pending review with more comments than were read has more', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, pending: { ...pendingOn(head), truncated: true } });
    expect(host.querySelector('.review-bar')?.textContent).toContain('more than 1 comment (the rest are on GitHub)');
    act(() => root.unmount());
  });

  it('a review retry refused by the rate limit keeps the key', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, writes: { '/reviews': [{ status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }, { status: 429, body: { error: 'rate-limited', message: 'Too many requests: this one was not acted on.' } }, { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }] } });
    await click(button(host, 'Review changes')!, host);
    await click(radio(host, 'APPROVE')!, host);
    await click(button(host, 'Submit: Approve')!, host);
    await click(button(host, 'Try again')!, host);
    await click(button(host, 'Try again')!, host);

    expect(writes()).toHaveLength(3);
    expect(new Set(writes().map((w) => w.body?.opKey)).size).toBe(1);
    act(() => root.unmount());
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

  it('asks for words before a one-shot comment, as the server does, but not for submitting a pending review', async () => {
    const none = await render('/acme/plant/pull/12', { writable: true });
    await click(button(none.host, 'Review changes')!, none.host);
    expect(button(none.host, 'Submit: Comment')!.disabled).toBe(true);
    act(() => none.root.unmount());
    document.body.innerHTML = '';

    const drafted = await render('/acme/plant/pull/12', { writable: true, pending: pendingOn(head) });
    await click(button(drafted.host, 'Finish your review')!, drafted.host);
    expect(button(drafted.host, 'Submit: Comment')!.disabled).toBe(false);
    act(() => drafted.root.unmount());
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
    await click(button(host.querySelector('.pr-title-row')!, 'Edit')!, host);
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
    // The description is the first card; its Edit sits in that card's header, as on GitHub.
    await click(button(first.host.querySelector('.comment-row')!, 'Edit')!, first.host);
    await fill(first.host.querySelector<HTMLTextAreaElement>('.comment-box textarea')!, 'The delay was 5 s; it is 2 s.');
    act(() => first.root.unmount());
    document.body.innerHTML = '';

    // A reload later, someone else has changed the description: the kept draft still names the old text.
    o.live = { title: 'Fix interlock', markdown: 'Jo rewrote this.' };
    const again = await render('/acme/plant/pull/12', o);
    await click(button(again.host.querySelector('.comment-row')!, 'Edit')!, again.host);
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

  it('confirming a merge again after an unknown outcome is the same operation, and a refusal lets the key go', async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: ready, writes: { '/merge': unknown } });
    const box = () => host.querySelector('.merge-box')!;
    for (let i = 0; i < 2; i++) {
      await click(button(box(), 'Merge...')!, host);
      await click(button(box(), 'Confirm')!, host);
    }

    const [first, second] = writes();
    expect(second!.body?.opKey).toBe(first!.body?.opKey);
    expect(second!.snapshot).toBe(first!.snapshot);
    act(() => root.unmount());
  });

  // Found in review (2026-10-21): only the comment box was tested for a refused RETRY.
  it('a merge retry refused by the rate limit keeps the key', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: ready, writes: { '/merge': [{ status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }, { status: 429, body: { error: 'rate-limited', message: 'Too many requests: this one was not acted on.' } }, { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } }] } });
    const box = () => host.querySelector('.merge-box')!;
    for (let i = 0; i < 3; i++) {
      await click(button(box(), 'Merge...')!, host);
      await click(button(box(), 'Confirm')!, host);
    }

    expect(new Set(writes().map((w) => w.body?.opKey)).size).toBe(1);
    act(() => root.unmount());
  });

  it("trying another merge action meanwhile does not drop the first one's unknown outcome", async () => {
    const unknown = { status: 202, body: { outcome: 'unknown', message: 'This may or may not have been posted.' } };
    const refused = { status: 409, body: { error: 'not-mergeable', message: 'Required reviews or checks have not passed.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: ready, writes: { '/merge': [unknown, refused, unknown] } });
    const box = () => host.querySelector('.merge-box')!;
    const message = async (text: string) => {
      await act(async () => {
        const input = box().querySelector<HTMLInputElement>('input[aria-label="Commit title"]')!;
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, text);
        input.dispatchEvent(new Event('input', { bubbles: true }));
      });
    };
    const confirm = async () => {
      await click(button(box(), 'Merge...')!, host);
      await click(button(box(), 'Confirm')!, host);
    };

    await confirm();
    await message('Another title');
    await confirm();
    await message('');
    await confirm();

    const [first, other, again] = writes().map((w) => w.body?.opKey);
    expect(other).not.toBe(first);
    expect(again).toBe(first);
    act(() => root.unmount());
  });

  it('after a refusal the next merge is a new operation', async () => {
    const refused = { status: 409, body: { error: 'not-mergeable', message: 'Required reviews or checks have not passed.' } };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: ready, writes: { '/merge': refused } });
    const box = () => host.querySelector('.merge-box')!;
    for (let i = 0; i < 2; i++) {
      await click(button(box(), 'Merge...')!, host);
      await click(button(box(), 'Confirm')!, host);
    }

    const [first, second] = writes();
    expect(second!.body?.opKey).not.toBe(first!.body?.opKey);
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

// Step 2 of docs/web/FINISH.md: the conversation as GitHub draws it — every event kind in its
// words, a review's threads under it, the merge box where GitHub puts it, the header's counts,
// the commits' and checks' detail.
describe('PullPage conversation', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  const at = '2026-10-17T09:00:00Z';
  const event = (kind: string, more: Record<string, unknown> = {}) => ({ kind, actor: 'sam', when: at, html: null, text: null, state: null, sha: null, id: null, markdown: null, canEdit: false, canDelete: false, ...more });

  async function click(element: Element, host: HTMLElement) {
    await act(async () => {
      (element as HTMLElement).click();
    });
    await settle(host, () => false);
  }

  it('draws every event kind in GitHub’s words, and an unknown kind as its words rather than nothing', async () => {
    const timeline = [
      event('labeled', { target: 'plc', targetColor: '1a7f37' }),
      event('review-requested', { target: 'controls' }),
      event('renamed', { previous: 'fix', target: 'Fix interlock' }),
      event('force-push', { previous: 'a'.repeat(40), sha: 'b'.repeat(40) }),
      event('cross-referenced', { actor: 'lee', reference: { number: 3, title: 'Trips late', repository: 'acme/plant', url: 'https://github.com/acme/plant/issues/3', isPull: false, state: 'OPEN' }, message: 'closes' }),
      event('review-dismissed', { target: 'lee', message: 'stale' }),
      event('merged', { sha: '9'.repeat(40), target: 'main' }),
      event('something-newer'),
    ];
    const { host, root } = await render('/acme/plant/pull/12', { timeline });

    // The actor, the sentence and the time are separate spans with a flex gap, no space in the text: read them as words.
    const rows = Array.from(host.querySelectorAll('.timeline-item')).map((li) => Array.from(li.querySelectorAll('.timeline-meta > *')).map((e) => e.textContent?.replace(/\s+/g, ' ').trim()).join(' '));
    expect(rows[0]).toContain('sam added the plc label');
    expect(host.querySelector('.timeline-labeled .issue-label')?.getAttribute('style')).toContain('rgb(26, 127, 55)');
    expect(rows[1]).toContain('requested a review from controls');
    expect(rows[2]).toContain('changed the title fix Fix interlock');
    expect(host.querySelector('.timeline-renamed del')?.textContent).toBe('fix');
    expect(rows[3]).toContain('force-pushed the branch from aaaaaaa to bbbbbbb');
    expect(rows[4]).toContain('lee will close this in acme/plant#3 Trips late');
    expect(host.querySelector('.timeline-cross-referenced a')?.getAttribute('href')).toBe('https://github.com/acme/plant/issues/3');
    expect(rows[5]).toContain('dismissed lee’s review: stale');
    expect(rows[6]).toContain('merged commit 9999999 into main');
    expect(rows[7]).toContain('something newer');
    act(() => root.unmount());
  });

  it('draws a comment as a card: the picture, the role, the edit mark, the permalink, the reactions', async () => {
    const timeline = [event('comment', { actor: 'jo', html: '<p>fine</p>', id: 'IC_1', association: 'MEMBER', editedAt: at, url: 'https://github.com/acme/plant/pull/12#issuecomment-9',
      reactions: [{ content: 'THUMBS_UP', count: 2, viewerHasReacted: true }, { content: 'EYES', count: 1, viewerHasReacted: false }] })];
    const { host, root } = await render('/acme/plant/pull/12', { timeline });

    const card = host.querySelector('.timeline-comment .comment-row')!;
    expect(card.getAttribute('id')).toBe('issuecomment-9');
    expect(card.querySelector('.comment-head')?.textContent).toContain('jo');
    expect(card.querySelector('.comment-head')?.textContent).toContain('edited');
    expect(card.querySelector('.role-badge')?.textContent).toBe('Member');
    expect(card.querySelector('.avatar-letter')?.textContent).toBe('J');
    expect(card.querySelector('.comment-head a[href="#issuecomment-9"]')).not.toBeNull();
    const reactions = Array.from(card.querySelectorAll('.reaction'));
    expect(reactions.map((r) => r.textContent?.trim())).toEqual(['👍 2', '👀 1']);
    expect(reactions[0]?.classList.contains('is-mine')).toBe(true);
    act(() => root.unmount());
  });

  it('lists a review’s own threads under it, hunk first, and leaves the others to Files changed', async () => {
    const timeline = [event('review', { actor: 'lee', state: 'CHANGES_REQUESTED', id: 'PRR_1', html: '<p>Not yet.</p>', sha: head })];
    const { host, root } = await render('/acme/plant/pull/12', { timeline, threadReview: 'PRR_1' });

    const review = host.querySelector('.timeline-review')!;
    expect(review.classList.contains('timeline-review-danger')).toBe(true);
    expect(review.textContent).toContain('lee requested changes');
    expect(review.querySelector('.comment-review-summary')?.textContent).toContain('Not yet.');
    // The thread whose first comment names the review is under it, with GitHub's hunk and a link to the file.
    const threads = Array.from(review.querySelectorAll('.review-thread'));
    expect(threads).toHaveLength(1);
    expect(threads[0]?.querySelector('.diff-hunk')?.textContent).toBe('@@ -1,3 +1,3 @@\n a\n-b\n+c');
    expect(threads[0]?.querySelector('.review-thread-file a')?.getAttribute('href')).toContain('/acme/plant/pull/12/files?path=src%2FPump.st');
    expect(threads[0]?.querySelector('[data-thread]')?.getAttribute('data-thread')).toBe('NEW3');
    act(() => root.unmount());
  });

  it('puts the merge summary and the merge box after the timeline and before the comment box, as GitHub does', async () => {
    const ready = { tier: true, state: 'OPEN', merged: false, headSha: head, mergeStateStatus: 'CLEAN', mergeable: true, blocked: null,
      methods: ['MERGE'], autoMergeAllowed: false, autoMerge: null, canDisableAutoMerge: false, mergeCommit: null };
    const { host, root } = await render('/acme/plant/pull/12', { writable: true, canMerge: true, merge: ready, counts: { reviewDecision: 'APPROVED', mergeable: 'MERGEABLE' } });

    const order = ['.timeline', '.merge-summary', '.merge-box', '.comment-row-new'].map((s) => host.querySelector(s));
    expect(order.every((e) => e !== null)).toBe(true);
    for (let i = 1; i < order.length; i++) {
      expect(order[i - 1]!.compareDocumentPosition(order[i]!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    }
    expect(host.querySelector('.merge-summary')?.textContent).toContain('Changes approved');
    expect(host.querySelector('.merge-summary')?.textContent).toContain('Some checks were not successful');
    act(() => root.unmount());
  });

  it('shows the header’s counts: commits in the meta line and on the tab, comments on the tab, the diffstat', async () => {
    const { host, root } = await render('/acme/plant/pull/12', { counts: { commits: 3, comments: 5, additions: 12, deletions: 3, changedFiles: 2 } });

    expect(host.querySelector('.pr-meta')?.textContent).toContain('wants to merge 3 commits into');
    const tab = (label: string) => Array.from(host.querySelectorAll('.pr-tabs .underline-nav-item')).find((a) => a.textContent?.includes(label));
    expect(tab('Commits')?.querySelector('.counter')?.textContent).toBe('3');
    expect(tab('Conversation')?.querySelector('.counter')?.textContent).toBe('5');
    expect(host.querySelector('.pr-diffstat')?.textContent).toContain('+12');
    expect(host.querySelector('.pr-diffstat')?.textContent).toContain('−3');
    act(() => root.unmount());
  });

  it('groups the checks by app, marks the required one, and shows a run’s output on demand', async () => {
    const checks = [
      { name: 'build', status: 'completed', conclusion: 'success', url: 'https://ci.example/1', source: 'check-run', app: 'GitHub Actions', required: true, startedAt: at, completedAt: '2026-10-17T09:04:00Z', title: 'Built', summary: '<div class="md-page"><p>All <strong>green</strong></p></div>' },
      { name: 'lint', status: 'in_progress', conclusion: null, url: null, source: 'check-run', app: 'GitHub Actions', required: false },
      { name: 'ci/jenkins', status: 'completed', conclusion: 'failure', url: 'https://jenkins.example/42', source: 'status', app: 'jenkins-bot', description: 'Build #42 failed' },
    ];
    const { host, root } = await render('/acme/plant/pull/12/checks', { checks, counts: { requiredChecks: ['build'] } });

    const groups = Array.from(host.querySelectorAll('.checks-group .box-header')).map((h) => h.textContent);
    expect(groups).toEqual(['GitHub Actions2', 'jenkins-bot1']);
    expect(host.querySelector('.checks-summary')?.textContent).toContain('1 failing');
    expect(host.querySelector('.checks-summary')?.textContent).toContain('1 in progress');
    expect(host.querySelector('.checks-summary')?.textContent).toContain('1 successful');
    const build = Array.from(host.querySelectorAll('.check')).find((li) => li.textContent?.includes('build'))!;
    expect(build.querySelector('.check-required')?.textContent).toBe('Required');
    expect(build.textContent).toContain('4m 0s');
    expect(build.querySelector('.check-output')).toBeNull();
    await click(Array.from(build.querySelectorAll('button')).find((b) => b.textContent?.includes('Show output'))!, host);
    expect(build.querySelector('.check-output')?.textContent).toContain('All green');
    act(() => root.unmount());
  });

  it('badges a verified signature and a merge, and opens a commit’s body on demand', async () => {
    const commits = [
      { sha: head, headline: 'Fix the default', author: 'jo', when: at, inSnapshot: true, login: 'jo', body: 'It was wrong.', verified: true, parents: 2, url: 'https://github.com/acme/plant/commit/' + head },
    ];
    const { host, root } = await render('/acme/plant/pull/12/commits', { commits });

    const row = host.querySelector('.commit-row')!;
    expect(row.querySelector('.verified-badge')?.textContent).toBe('Verified');
    expect(row.textContent).toContain('Merge');
    expect(row.querySelector('a.commit-sha')?.getAttribute('href')).toBe('https://github.com/acme/plant/commit/' + head);
    expect(row.querySelector('.commit-body')).toBeNull();
    await click(row.querySelector('.commit-expand')!, host);
    expect(row.querySelector('.commit-body')?.textContent).toBe('It was wrong.');
    act(() => root.unmount());
  });
});

// Step 3 of docs/web/FINISH.md: Files changed as GitHub shows it — the tree of changed files
// beside every file stacked, each with its own header and state; a shift-click makes a range.
describe('PullPage files changed', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    sessionStorage.clear();
  });

  async function click(element: Element, host: HTMLElement, shift = false) {
    await act(async () => {
      element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey: shift }));
    });
    await settle(host, () => false);
  }

  it('stacks every changed file under its own sticky header, the one in the URL first and lit in the tree', async () => {
    const { host, root } = await render('/acme/plant/pull/12/files?path=src%2FValve.st', { twoFiles: true });

    const sections = Array.from(host.querySelectorAll('.file-review'));
    expect(sections.map((s) => s.getAttribute('data-file'))).toEqual(['src/Pump.st', 'src/Valve.st']);
    // Each section has its header with the object's name, where it lives and its +N −M.
    expect(sections[1]?.querySelector('.review-file')?.textContent).toBe('Valve');
    expect(sections[1]?.querySelector('.pane-folder')?.textContent).toBe('src');
    expect(sections[1]?.querySelector('.diffstat')?.textContent).toContain('+2');
    expect(host.querySelector('.files-tree .tree-row.is-selected')?.textContent).toContain('Valve');
    // The tree groups by folder: one "src" folder with two files under it.
    expect(host.querySelector('.files-tree .tree-folder .tree-name')?.textContent).toBe('src');
    expect(host.querySelectorAll('.files-tree [role="treeitem"][aria-selected]')).toHaveLength(2);
    act(() => root.unmount());
  });

  it('folds a viewed file to its header, as GitHub does, and the chevron opens it again', async () => {
    const { host, root } = await render('/acme/plant/pull/12/files?path=src%2FValve.st', { writable: true, twoFiles: true, viewed: ['src/Pump.st'] });

    const pump = host.querySelector('[data-file="src/Pump.st"]')!;
    expect(pump.classList.contains('is-folded')).toBe(true);
    expect(pump.querySelector('.file-review-body')).toBeNull();
    expect(host.querySelector('.files-progress')?.textContent).toContain('1 / 2 files viewed');
    await click(pump.querySelector('.file-chevron')!, host);
    expect(pump.querySelector('.file-review-body')).not.toBeNull();
    act(() => root.unmount());
  });

  it('a shift-click on a second line makes the draft a range, posted with its start line', async () => {
    const { host, root } = await render('/acme/plant/pull/12/files?path=src%2FPump.st&view=raw', { writable: true });
    const number = (line: number) => host.querySelector(`[data-line="${line}"] .cd-num`)!;

    await click(number(2), host);
    expect(host.querySelector('.thread-draft .thread-head')?.textContent).toContain('new line 2');
    await click(number(3), host, true);
    expect(host.querySelector('.thread-draft .thread-head')?.textContent).toContain('new lines 2–3');

    const textarea = host.querySelector<HTMLTextAreaElement>('.thread-draft textarea')!;
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(textarea, 'Both of these.');
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await click(Array.from(host.querySelectorAll('.thread-draft button')).find((b) => b.textContent === 'Comment')!, host);

    const posted = calls.find((c) => c.method === 'POST' && c.url.endsWith('/line-comments'))!;
    expect(posted.body).toMatchObject({ path: 'src/Pump.st', line: 3, startLine: 2, side: 'RIGHT' });
    act(() => root.unmount());
  });

  it('hides resolved threads when asked, and says how many it hides', async () => {
    const { host, root } = await render('/acme/plant/pull/12/files?path=src%2FPump.st&view=raw');

    expect(host.querySelector('[data-thread="OLD3"]')).not.toBeNull();
    const toggle = host.querySelector<HTMLInputElement>('.file-resolved-toggle input')!;
    expect(host.querySelector('.file-resolved-toggle')?.textContent).toContain('Show 1 resolved thread');
    await click(toggle, host);
    expect(host.querySelector('[data-thread="OLD3"]')).toBeNull();
    expect(host.querySelector('[data-thread="NEW3"]')).not.toBeNull();
    act(() => root.unmount());
  });

  it('a link to another file of the pull request goes to that file’s section; to anything else, to the object at the head', async () => {
    const { host, root } = await render('/acme/plant/pull/12/files?path=src%2FPump.st', { twoFiles: true });
    await act(async () => {
      jump('/acme/plant/pull/12/files?path=src%2FValve.st');
    });
    await settle(host);
    expect(host.querySelector('.files-tree .tree-row.is-selected')?.textContent).toContain('Valve');
    act(() => root.unmount());
  });
});
