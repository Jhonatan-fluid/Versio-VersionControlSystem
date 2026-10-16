import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DocsPage, docKind } from './DocsPage';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const file = (path: string) => ({
  name: path.slice(path.lastIndexOf('/') + 1), path, label: path, kind: 'file', primary: null, files: [path], children: [],
});

function answer(url: string): unknown {
  if (url.includes('/view')) {
    return { kind: 'markdown', title: 'README.md', path: 'README.md', html: '<div class="md-page"><p>Hello docs.</p></div>', notices: [], layers: [], layerLabels: [] };
  }
  if (url.includes('/tree')) {
    return {
      commit: 'c'.repeat(40), isVersio: true, truncated: false, problems: [],
      nodes: [
        file('README.md'),
        { ...file('docs'), kind: 'folder', files: [], children: [file('docs/manual.pdf'), file('docs/spec.docx'), file('docs/notes.md')] },
        { ...file('plc/Main.st') },
      ],
    };
  }
  if (url.endsWith('/branches')) {
    return { branches: [{ name: 'main', label: 'main', commit: 'x' }], truncated: false };
  }
  return { owner: 'acme', name: 'plant', private: false, defaultBranch: 'main' };
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
          <Route path="/:owner/:repo/docs/:ref" element={<DocsPage me={null} />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  for (let i = 0; i < 20 && !host.querySelector('.doc-folder'); i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 5));
    });
  }
  return { host, root };
}

describe('Docs', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('lists only documents, grouped under their folders, the root first', async () => {
    const { host, root } = await render('/acme/plant/docs/main');

    const folders = Array.from(host.querySelectorAll('.doc-folder')).map((f) => f.textContent);
    expect(folders).toEqual(['Repository root', 'docs']);
    // Main.st is code, not a document; the docs folder lists all three of its documents.
    expect(host.textContent).not.toContain('Main.st');
    const rows = Array.from(host.querySelectorAll('.tree-row .tree-name')).map((r) => r.textContent);
    expect(rows).toEqual(['README.md', 'manual.pdf', 'spec.docx', 'notes.md']);
    act(() => root.unmount());
  });

  it('embeds a picked pdf from the raw route', async () => {
    const { host, root } = await render('/acme/plant/docs/main?path=docs%2Fmanual.pdf');

    const frame = host.querySelector('iframe.doc-frame');
    expect(frame?.getAttribute('src')).toBe('/api/repos/acme/plant/raw?ref=main&path=docs%2Fmanual.pdf');
    act(() => root.unmount());
  });

  it('offers a download for a format a browser cannot draw', async () => {
    const { host, root } = await render('/acme/plant/docs/main?path=docs%2Fspec.docx');

    expect(host.textContent).toContain('cannot draw this format');
    const link = Array.from(host.querySelectorAll('a')).find((a) => a.textContent?.startsWith('Download'));
    expect(link?.getAttribute('href')).toBe('/api/repos/acme/plant/raw?ref=main&path=docs%2Fspec.docx');
    act(() => root.unmount());
  });

  it('renders a picked text document through the view pane', async () => {
    const { host, root } = await render('/acme/plant/docs/main?path=README.md');
    for (let i = 0; i < 20 && !host.querySelector('.fragment'); i++) {
      await act(async () => {
        await new Promise((r) => setTimeout(r, 5));
      });
    }

    expect(host.querySelector('.fragment')?.textContent).toContain('Hello docs.');
    act(() => root.unmount());
  });

  it('knows a document from code by extension alone', () => {
    expect(docKind('docs/Manual.PDF')).toBe('pdf');
    expect(docKind('a/b.markdown')).toBe('text');
    expect(docKind('io-list.xlsx')).toBe('download');
    expect(docKind('plc/Main.st')).toBeNull();
    expect(docKind('folder.v1/noext')).toBeNull();
  });
});
