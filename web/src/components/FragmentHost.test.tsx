import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FragmentHost } from './FragmentHost';

// FragmentHost's click handling (review, 2026-10-24): a click inside a nested host — a review
// thread's comment drawn in a slot between the drawing's rows — is handled by that host alone.
// The slot is a DOM descendant of the drawing's div, which suggested a double handling; it is
// not one, because the portal's React parent is the host's fragment root, and this pins that
// (a guard added for it survived its own mutation and was taken out). And a fold row asks the
// page for the whole file.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

function mount(element: React.ReactElement) {
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  act(() => root.render(element));
  return { host, root };
}

describe('FragmentHost', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('a link in a comment drawn between the rows is handled once, by the comment’s own host', () => {
    const open = vi.fn();
    vi.stubGlobal('open', open);
    const outer = vi.fn();
    const inner = vi.fn();
    const drawing = '<div class="cd-page"><div class="cd-row" data-line="1"><span class="cd-num">1</span><code class="cd-code">x</code></div></div>';
    const comment = '<div class="md-page"><p><a data-href="https://example.test/doc">doc</a> and <a data-href="notes.md">notes</a></p></div>';
    const { host, root } = mount(
      <FragmentHost html={drawing} path="src/Pump.st" onOpenPath={outer} line={null} onLine={() => {}}
        annotations={[{ key: 't', selector: '[data-line="1"]', content: <FragmentHost html={comment} path="" onOpenPath={inner} line={null} onLine={() => {}} /> }]} />,
    );

    act(() => host.querySelector<HTMLElement>('.thread-slot a[data-href="https://example.test/doc"]')!.click());
    expect(open).toHaveBeenCalledTimes(1);

    // A relative link in the comment resolves against the comment's (empty) path, never the
    // reviewed file's: the outer host must not open src/notes.md.
    act(() => host.querySelector<HTMLElement>('.thread-slot a[data-href="notes.md"]')!.click());
    expect(outer).not.toHaveBeenCalled();
    act(() => root.unmount());
  });

  it('a fold row asks for the whole file', () => {
    const unfold = vi.fn();
    const drawing = '<div class="cd-page"><div class="cd-fold" data-count="12"><span>12 unchanged lines</span></div></div>';
    const { host, root } = mount(<FragmentHost html={drawing} path="src/Pump.st" onOpenPath={() => {}} line={null} onLine={() => {}} onUnfold={unfold} />);

    act(() => host.querySelector<HTMLElement>('.cd-fold span')!.click());

    expect(unfold).toHaveBeenCalledTimes(1);
    act(() => root.unmount());
  });

  // The slot row's cell spans the member row's columns. The count was read from the element the
  // slot goes after, which for a SECOND thread on the same row is the first thread's one-cell slot
  // row: the second thread sat in one column of a four-column table (the 2026-10-24 review's item 17).
  it('every thread on one member row spans the row’s columns', () => {
    const table =
      '<div class="mt-page"><table class="mt-table"><tbody>' +
      '<tr class="mt-row" data-path="run" data-anchors="r-new-0"><td class="mt-mark">M</td><td class="mt-name">run</td><td class="mt-type">BOOL</td><td class="mt-comment">x</td></tr>' +
      '</tbody></table></div>';
    const { host, root } = mount(
      <FragmentHost html={table} path="src/Pump.xml" onOpenPath={() => {}} line={null} onLine={() => {}}
        annotations={[
          { key: 'a', selector: '[data-anchors~="r-new-0"]', content: <p>first</p> },
          { key: 'b', selector: '[data-anchors~="r-new-0"]', content: <p>second</p> },
        ]} />,
    );

    const cells = Array.from(host.querySelectorAll<HTMLTableCellElement>('tr.thread-row td'));
    expect(cells.map((c) => c.textContent)).toEqual(['first', 'second']);
    expect(cells.map((c) => c.colSpan)).toEqual([4, 4]);
    act(() => root.unmount());
  });
});
