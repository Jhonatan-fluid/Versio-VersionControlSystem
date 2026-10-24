import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it } from 'vitest';
import type { View } from '../api';
import { Pane } from './Pane';

// An HTML object's Web page tab (docs/web/FINISH.md, Step 9): the desktop's third chip, drawn
// here as the raw route framed sandboxed with nothing allowed — no script, no origin, no form.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const view = (kind: string): View => ({ kind, title: 'manual', path: 'docs/manual.html', html: '<div class="cd-page">source</div>', notices: [], layers: ['docs/manual.html'], layerLabels: ['docs/manual.html'] });

async function render(kind: string, pageUrl: string | null) {
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(<Pane result={{ state: 'ok', value: view(kind) }} view="rendered" layout="unified" onView={() => {}} onLayout={() => {}} onLayer={() => {}} onOpenPath={() => {}} line={null} onLine={() => {}} comparing={false} signInUrl={null} pageUrl={pageUrl} />);
  });
  return { host, root };
}

describe('Pane', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('offers a Web page tab for an HTML object, which frames the raw route with every sandbox flag off', async () => {
    const { host, root } = await render('webpage', '/api/repos/acme/plant/raw?ref=main&path=docs%2Fmanual.html');
    const tab = Array.from(host.querySelectorAll('[role="tab"]')).find((b) => b.textContent === 'Web page')!;
    expect(tab).toBeDefined();
    expect(host.querySelector('.cd-page')?.textContent).toBe('source');

    await act(async () => {
      (tab as HTMLElement).click();
    });

    const frame = host.querySelector<HTMLIFrameElement>('iframe.page-frame')!;
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame.getAttribute('src')).toBe('/api/repos/acme/plant/raw?ref=main&path=docs%2Fmanual.html');
    expect(host.querySelector('.cd-page')).toBeNull();
    act(() => root.unmount());
  });

  it('offers no Web page tab for anything else, or without a raw route to frame', async () => {
    const source = await render('source', '/api/repos/acme/plant/raw?ref=main&path=x');
    expect(Array.from(source.host.querySelectorAll('[role="tab"]')).map((b) => b.textContent)).toEqual(['Rendered', 'Raw']);
    act(() => source.root.unmount());
    document.body.innerHTML = '';

    const noUrl = await render('webpage', null);
    expect(Array.from(noUrl.host.querySelectorAll('[role="tab"]')).map((b) => b.textContent)).toEqual(['Rendered', 'Raw']);
    act(() => noUrl.root.unmount());
  });
});
