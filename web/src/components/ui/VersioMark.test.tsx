import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import reference from '../../../../assets/versio.svg?raw';
import page from '../../../index.html?raw';
import served from '../../../public/versio.svg?raw';
import { AppHeader } from '../AppHeader';
import { VersioMark } from './VersioMark';

// The mark is drawn in four places (the header of assets/versio.svg lists them), and this is
// the only one a test can hold to the reference drawing: the numbers here are read from the
// SVG itself, so a plate nudged in one file and not the other is a red test rather than a logo
// that is almost right. The favicon is the reference file byte for byte, for the same reason.
// The files come in as text through Vite's ?raw, the way ladderScript.test.ts reads Core's
// script; vite.config.ts allows their two folders for it.

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

function shapes(svg: string): { rects: string[][]; circles: string[][] } {
  const attrs = (tag: string, names: string[]) =>
    [...svg.matchAll(new RegExp(`<${tag}\\b[^>]*>`, 'g'))]
      .map((m) => m[0])
      .filter((s) => !/width="64"/.test(s)) // the tile, which the in-page mark leaves out
      .map((s) => names.map((n) => new RegExp(`\\b${n}="([^"]*)"`).exec(s)?.[1] ?? ''));
  return { rects: attrs('rect', ['x', 'y', 'width', 'height', 'rx']), circles: attrs('circle', ['cx', 'cy', 'r']) };
}

describe('VersioMark', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('is the reference drawing without its tile: two plates and the notched disc at the same numbers', async () => {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const root = createRoot(host);
    await act(async () => root.render(<VersioMark size={20} />));
    const drawn = shapes(host.innerHTML);
    const want = shapes(reference);
    expect(want.rects).toHaveLength(2);
    expect(want.circles).toHaveLength(2);
    expect(drawn.rects).toEqual(want.rects);
    expect(drawn.circles).toEqual(want.circles);
    const svg = host.querySelector('svg');
    expect(svg?.getAttribute('aria-label')).toBe('Versio');
    expect(svg?.getAttribute('viewBox')).toBe('0 0 64 64');
    expect(svg?.getAttribute('width')).toBe('20');
    await act(async () => root.unmount());
  });

  it("is what the header shows, in place of any other service's mark", async () => {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const root = createRoot(host);
    await act(async () =>
      root.render(
        <MemoryRouter>
          <AppHeader me={{ signedIn: false, login: null, name: null, avatarUrl: null, signInAvailable: true, anonymousAccess: true, installUrl: null }} />
        </MemoryRouter>,
      ),
    );
    const mark = host.querySelector('.app-header-mark');
    expect(mark?.querySelector('svg.versio-mark')).not.toBeNull();
    expect(mark?.querySelector('.octicon')).toBeNull();
    await act(async () => root.unmount());
  });

  it('is the favicon too, as the reference file itself', () => {
    expect(served).toBe(reference);
    expect(page).toContain('rel="icon" href="/versio.svg"');
  });
});
