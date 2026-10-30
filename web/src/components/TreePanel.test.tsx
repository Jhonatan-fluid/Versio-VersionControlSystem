import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Tree, TreeNode } from '../api';
import { TreePanel } from './TreePanel';

// The desktop's tree row on the web (docs/web/FINISH.md, Step 8): the vendor's pill, the object's
// type, the change glyph, a struck deleted name, and on a collapsed folder hiding a change a dot in
// the strongest hidden state's colour (the count went 2026-10-29, the roll-up colour on the name
// 2026-10-30).

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const node = (over: Partial<TreeNode> & { name: string; path: string }): TreeNode => ({
  label: over.path, kind: 'object', primary: null, files: [over.path], children: [], ...over,
});

const tree: Tree = {
  commit: 'a'.repeat(40), base: 'b'.repeat(40), isVersio: true, truncated: false, problems: [],
  nodes: [
    node({ name: 'Plant.project', path: 'plc/Plant.project', kind: 'project', toolchain: 'CODESYS V3.5 SP19', detail: 'in sync', rollUp: 'deleted', changedDescendants: 2, children: [
      node({ name: 'Pump', path: 'plc/Plant/Pump.st', typeName: 'Function block', status: 'modified' }),
      node({ name: 'Old', path: 'plc/Plant/Old.st', typeName: 'Program', status: 'deleted' }),
      node({ name: 'Valve', path: 'plc/Plant/Valve.st', typeName: 'Program' }),
    ] }),
    node({ name: 'README.md', path: 'README.md', kind: 'file' }),
  ],
};

async function render(t: Tree, comparing = false) {
  const host = document.createElement('div');
  document.body.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(<TreePanel tree={t} selected={null} onSelect={() => {}} comparing={comparing} />);
  });
  return { host, root };
}

describe('TreePanel', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    document.body.innerHTML = '';
  });

  it('draws the pill, the detail, the type, the glyphs and the struck name, as the desktop does, and no count', async () => {
    const { host, root } = await render(tree);
    const rows = Array.from(host.querySelectorAll<HTMLElement>('.tree-row'));
    const row = (name: string) => rows.find((r) => r.querySelector('.tree-name')?.textContent === name)!;

    expect(row('Plant.project').querySelector('.tree-pill')?.textContent).toBe('CODESYS V3.5 SP19');
    expect(row('Plant.project').querySelector('.tree-annotation')?.textContent).toBe('in sync');
    // Two changed descendants in the fixture, and nothing drawn for them while the project is
    // open: no number (2026-10-29), no colour on the name (2026-10-30), and no dot, because the
    // changes themselves are on screen under it. The dot is the collapsed case, tested below.
    expect(row('Plant.project').querySelector('.tree-badge')).toBeNull();
    expect(row('Plant.project').className).not.toContain('tone-');
    expect(row('Plant.project').querySelector('.tree-dot')).toBeNull();
    expect(row('Pump').className).toContain('tone-modified');
    expect(row('Pump').querySelector('.tree-annotation')?.textContent).toBe('Function block');
    expect(row('Pump').querySelector('.change')?.textContent).toBe('M');
    expect(row('Old').querySelector('.change')?.textContent).toBe('D');
    expect(row('Old').className).toContain('is-deleted');
    expect(row('Valve').querySelector('.change')).toBeNull();
    expect(row('README.md').querySelector('.tree-pill')).toBeNull();
    act(() => root.unmount());
  });

  it('draws a dot on a collapsed row only while it hides a change', async () => {
    // Option 1 of three the owner was shown on 2026-10-30: a parent carries a mark only while
    // it is hiding something. Collapsed with changes below, a dot in the strongest hidden
    // state's colour; open, nothing; collapsed with nothing changed below, nothing either, which
    // is what tells two shut projects apart.
    const two: Tree = {
      ...tree,
      nodes: [
        tree.nodes[0]!,
        node({ name: 'Idle.project', path: 'plc/Idle.project', kind: 'project', children: [
          node({ name: 'Nop', path: 'plc/Idle/Nop.st', typeName: 'Program' }),
        ] }),
      ],
    };
    const { host, root } = await render(two);
    const row = (name: string) =>
      Array.from(host.querySelectorAll<HTMLElement>('.tree-row')).find((r) => r.querySelector('.tree-name')?.textContent === name)!;

    expect(row('Plant.project').querySelector('.tree-dot')).toBeNull();
    await act(async () => { row('Plant.project').click(); });
    await act(async () => { row('Idle.project').click(); });

    const dot = row('Plant.project').querySelector<HTMLElement>('.tree-dot');
    expect(dot).not.toBeNull();
    expect(dot!.className).toContain('tone-deleted');
    expect(dot!.getAttribute('aria-label')).toBe('Changes below');
    expect(row('Plant.project').className).not.toContain('tone-');
    expect(row('Idle.project').querySelector('.tree-dot')).toBeNull();

    await act(async () => { row('Plant.project').click(); });
    expect(row('Plant.project').querySelector('.tree-dot')).toBeNull();
    act(() => root.unmount());
  });

  it('says a repository with nothing to draw is plain folders, but never on a comparison', async () => {
    const plain = { ...tree, isVersio: false };
    const code = await render(plain);
    expect(code.host.textContent).toContain('no PLC project Versio can draw');
    act(() => code.root.unmount());
    document.body.innerHTML = '';

    const compared = await render(plain, true);
    expect(compared.host.textContent).not.toContain('plain folders');
    act(() => compared.root.unmount());
  });
});
