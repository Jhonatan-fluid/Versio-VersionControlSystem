import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PullPage, threadSelector } from './PullPage';

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
const renderedSource = '<div class="cd-page"><div class="cd-row" data-line="3" data-file-line="9"><span class="cd-num">3</span>run := TRUE;</div></div>';

const hostile = '<div class="md-page"><p>Why?<img src="x" onerror="window.__pwned=1"><script>window.__pwned=1</script></p></div>';

interface Options {
  stale?: boolean;
}

let calls: { url: string; snapshot: string | null }[] = [];

function answer(url: string, o: Options): unknown {
  if (url.endsWith('/snapshot')) {
    return {
      token, number: 12, title: 'Fix interlock', author: 'jo', when: '2026-10-17T09:00:00Z', draft: false,
      baseRef: 'main', baseLabel: 'main', headLabel: 'fix', baseSha: 'c'.repeat(40), mergeBaseSha: 'b'.repeat(40), headSha: head,
      issuedAt: '2026-10-17T09:00:00Z', html: '<div class="md-page"><p>The delay was wrong.</p></div>',
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
      view: { kind: raw ? 'diff' : 'table', title: 'Pump', path: 'src/Pump.st', html: raw ? rawDiff : renderedSource, notices: [], layers: ['src/Pump.st'], layerLabels: ['src/Pump.st'] },
    };
  }
  if (url.endsWith('/threads')) {
    const comment = (id: string, html: string) => ({ id, author: 'sam', when: '2026-10-17T09:00:00Z', html, commit: head, originalCommit: head });
    return {
      truncated: false,
      threads: [
        { id: 'NEW3', path: 'src/Pump.st', pathLabel: 'src/Pump.st', resolved: false, outdated: false, line: 3, startLine: null, side: 'RIGHT', originalLine: 3, subject: 'LINE', truncated: false, comments: [comment('c1', hostile)] },
        { id: 'OLD3', path: 'src/Pump.st', pathLabel: 'src/Pump.st', resolved: true, outdated: false, line: 3, startLine: null, side: 'LEFT', originalLine: 3, subject: 'LINE', truncated: false, comments: [comment('c2', '<p>was it ever?</p>')] },
        { id: 'GONE', path: 'src/Pump.st', pathLabel: 'src/Pump.st', resolved: false, outdated: true, line: null, startLine: null, side: 'RIGHT', originalLine: 9, subject: 'LINE', truncated: false, comments: [comment('c3', '<p>an old remark</p>')] },
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
    calls.push({ url, snapshot: headers['X-Versio-Snapshot'] ?? null });
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
  const base = { id: 't', path: 'a', pathLabel: 'a', resolved: false, outdated: false, startLine: null, originalLine: 1, truncated: false, comments: [] };

  it('uses the new side for RIGHT, the old for LEFT, and nothing for files or outdated threads', () => {
    expect(threadSelector({ ...base, line: 4, side: 'RIGHT', subject: 'LINE' })).toBe('[data-line="4"]');
    expect(threadSelector({ ...base, line: 4, side: 'LEFT', subject: 'LINE' })).toBe('[data-old-line="4"]');
    expect(threadSelector({ ...base, line: null, side: 'RIGHT', subject: 'LINE' })).toBeNull();
    expect(threadSelector({ ...base, line: 4, side: 'RIGHT', subject: 'FILE' })).toBeNull();
  });
});
