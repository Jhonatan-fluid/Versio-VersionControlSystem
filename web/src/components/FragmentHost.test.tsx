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
});
